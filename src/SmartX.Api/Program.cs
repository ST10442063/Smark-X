using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Hubs;
using SmartX.Api.Models;
using SmartX.Api.Services;
using SmartX.Api.Services.Streaming;
using SmartX.Contracts.Stream;
using SmartX.Core.Streaming;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------
builder.Services
    .AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
    .ValidateDataAnnotations()
    .PostConfigure(gateway =>
    {
        // Applied after binding rather than as a property initialiser: the binder
        // merges into a non-empty collection instead of replacing it, so a default
        // set there would be appended to whatever appsettings.json supplies.
        if (gateway.AllowedUploadExtensions.Length == 0)
        {
            gateway.AllowedUploadExtensions = GatewayOptions.DefaultUploadExtensions;
        }
    })
    // A malformed key is refused at startup rather than on the first upload, so a
    // typo in a deployment secret fails the boot instead of a user's request.
    .Validate(
        gateway => string.IsNullOrWhiteSpace(gateway.AttachmentEncryptionKey)
                   || AttachmentKeyProvider.TryDecodeKey(gateway.AttachmentEncryptionKey, out _),
        $"Gateway:AttachmentEncryptionKey must be the base64 encoding of exactly " +
        $"{SmartX.Core.Security.AttachmentEncryption.KeySizeBytes} bytes.")
    .ValidateOnStart();

builder.Services
    .AddOptions<StreamOptions>()
    .Bind(builder.Configuration.GetSection(StreamOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ---------------------------------------------------------------------
// Gateway services
// ---------------------------------------------------------------------
// The store is a singleton because it *is* the gateway state - the sensor
// registry, the ring buffers and the historical batch stores all live in it for
// the life of the process. Everything else is stateless and can be scoped.
builder.Services.AddSingleton<GatewayStore>();
builder.Services.AddScoped<TelemetryIngestionService>();
builder.Services.AddSingleton<AttachmentKeyProvider>();
builder.Services.AddSingleton<AttachmentService>();
builder.Services.AddScoped<AnomalyDetectionService>();
builder.Services.AddScoped<DashboardService>();

builder.Services.AddScoped<TelemetryPipeline>();

// ---------------------------------------------------------------------
// Part 2 - the real-time command stream
// ---------------------------------------------------------------------
// The live state and the two-lane intake queue are singletons for the same reason
// the gateway store is: they are the state. The queue is the one structure that is
// thread-safe on its own, because request threads produce into it while the
// dispatch worker consumes.
builder.Services.AddSingleton<CommandStreamStore>();
builder.Services.AddSingleton(services => new DualLaneDispatchQueue<QueuedPacket>(
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StreamOptions>>().Value.StandardLaneCapacity));
builder.Services.AddSingleton<UplinkService>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<InsightService>();
builder.Services.AddSingleton<StreamTestTools>();
builder.Services.AddSingleton<OperatorHistorySeeder>();
builder.Services.AddSingleton<StreamBroadcaster>();
builder.Services.AddSingleton<HealthSweepService>();

// ---------------------------------------------------------------------
// Final PoE - history trees and the mesh topology
// ---------------------------------------------------------------------
// Stateless over the gateway store's AVL time index, so one instance serves all.
builder.Services.AddSingleton<SmartX.Api.Services.History.HistoryQueryService>();

// The mesh graph is state, like the stores, and listens for registrations.
builder.Services.AddSingleton<SmartX.Api.Services.Topology.MeshTopologyService>();

builder.Services.AddSignalR().AddJsonProtocol(json =>
    json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The seeder resolves scoped services, so it takes them from a scope it creates
// itself rather than being registered as a scoped service inside a hosted one.
// Registered first: hosted services start in order, so every stream worker below
// starts against a fully seeded gateway.
builder.Services.AddScoped<TelemetrySeeder>();
builder.Services.AddHostedService<SeedHostedService>();
builder.Services.AddHostedService<TelemetryDispatchWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<HealthSweepService>());
builder.Services.AddHostedService(services => services.GetRequiredService<StreamBroadcaster>());

// How deeply nested a request body may be. Each deployment tier costs two JSON
// levels (the node object and its children array), so the framework defaults of 32
// rejected a profile only sixteen tiers deep - and did it by throwing a 500 from
// inside model binding, before RecursiveDeploymentValidator could report MAX_DEPTH
// itself. These limits admit profiles well past the validator's own 32-tier guard,
// so an over-deep profile is refused by that guard, with a proper finding, instead.
const int MaxRequestJsonDepth = 128;

builder.Services.AddControllers(mvc =>
    {
        // Sized above MaxRequestJsonDepth so the error key for even the deepest
        // rejected body still fits in ModelState rather than throwing.
        mvc.MaxModelBindingRecursionDepth = MaxRequestJsonDepth * 2;
        mvc.MaxValidationDepth = MaxRequestJsonDepth * 2;
    })
    .AddJsonOptions(json =>
    {
        // Enums travel as strings on the wire so the published contract stays
        // readable and does not shift when a member is inserted.
        json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        json.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        json.JsonSerializerOptions.MaxDepth = MaxRequestJsonDepth;
    });

// Model-binding failures return RFC 7807 ProblemDetails with a field-level error
// dictionary, so the Blazor client can map each message back onto its own form field
// rather than showing one generic "bad request".
builder.Services.Configure<ApiBehaviorOptions>(apiBehaviour =>
{
    apiBehaviour.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Title = "The request failed validation.",
            Status = StatusCodes.Status400BadRequest,
            Instance = context.HttpContext.Request.Path
        };

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" }
        };
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new()
    {
        Title = "Smart-X Ingestion Gateway",
        Version = "v1",
        Description =
            "Telemetry ingestion and sensor configuration API for the Smart-X IoT mesh. " +
            "Accepts sensor registrations, single and batch telemetry, device log and " +
            "configuration attachments, and recursive deployment-profile validation."
    });

    // Surface the XML doc comments in Swagger, so the endpoint descriptions in the
    // UI are the same ones that document the code.
    var xmlPath = Path.Combine(AppContext.BaseDirectory, "SmartX.Api.xml");
    if (File.Exists(xmlPath))
    {
        swagger.IncludeXmlComments(xmlPath);
    }
});

// The Blazor WebAssembly client is served from a different origin, so it needs an explicit
// allowance. The allowed origins are configured rather than wildcarded - which is also
// what permits AllowCredentials, needed by the SignalR negotiate request.
const string WebClientCors = "SmartXWebClient";
builder.Services.AddCors(cors =>
    cors.AddPolicy(WebClientCors, policy => policy
        .WithOrigins(builder.Configuration.GetSection("AllowedWebOrigins").Get<string[]>()
                     ?? ["http://localhost:5090", "https://localhost:5091"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

// Resolve the attachment key during startup, not on the first upload. A missing or
// corrupt key file then stops the gateway booting with a clear message, instead of
// surfacing as a 500 the first time an operator attaches a log.
app.Services.GetRequiredService<AttachmentKeyProvider>();

// Created now, before the hosted services start, so it is already listening for
// registrations when the seeder begins registering the fleet.
app.Services.GetRequiredService<CommandStreamStore>();

// Likewise the topology service, so the seeded fleet is placed on the site plan as
// it registers.
app.Services.GetRequiredService<SmartX.Api.Services.Topology.MeshTopologyService>();

// ---------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(ui =>
    {
        ui.SwaggerEndpoint("/swagger/v1/swagger.json", "Smart-X Ingestion Gateway v1");
        ui.DocumentTitle = "Smart-X Ingestion Gateway";
    });
}

// Any unhandled exception becomes a ProblemDetails response rather than an HTML
// error page, so the Blazor client always gets JSON back and can report it usefully.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";

    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Title = "The gateway could not complete the request.",
        Status = StatusCodes.Status500InternalServerError,
        Instance = context.Request.Path
    });
}));

app.UseCors(WebClientCors);
app.MapControllers();
app.MapHub<StreamHub>(StreamHubContract.Path);

// A liveness probe. Docker Compose polls it before starting the console, so the
// client never boots against a gateway that is still seeding.
app.MapGet("/api/health", (GatewayStore store) => Results.Ok(new
{
    status = "healthy",
    startedAtUtc = store.StartedAtUtc,
    uptimeSeconds = (DateTimeOffset.UtcNow - store.StartedAtUtc).TotalSeconds,
    registeredSensors = store.SensorCount,
    readingsAccepted = store.TotalReadingsAccepted
}))
.WithName("GatewayHealth")
.WithTags("Gateway");

app.Run();

/// <summary>
/// Exposed so the integration tests can spin the gateway up in-process.
/// </summary>
public partial class Program;
