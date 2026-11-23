using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartX.Client;
using SmartX.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The gateway address is configuration, read from wwwroot/appsettings.json when
// the app boots in the browser. It is not the address this client is served from:
// the console and the API are separate origins, and the API's CORS policy names
// this one explicitly.
var apiBaseUrl = builder.Configuration["SmartX:ApiBaseUrl"] ?? "http://localhost:5080";

// Singletons are per browser tab in WebAssembly - there is exactly one user - so
// the reachability flag and the toast stack are shared by every page and survive
// navigation, which is what keeps a toast raised just before a route change alive.
builder.Services.AddSingleton<GatewayStatus>();
builder.Services.AddSingleton<ToastService>();

// Part 2. One SignalR connection per tab, opened by the layout and kept across
// navigation. The live model it feeds is scoped - which in WebAssembly is also one
// per tab - because it uses the scoped API client to load its snapshots.
builder.Services.AddSingleton(services => new StreamConnection(
    apiBaseUrl,
    services.GetRequiredService<ILogger<StreamConnection>>()));
builder.Services.AddScoped<CommandStreamState>();

// Final PoE. The topology page's selection, view options and history window, kept
// for the life of the tab so leaving the page and coming back restores it.
builder.Services.AddScoped<TopologyState>();

builder.Services.AddScoped(services => new SmartXApiClient(
    new HttpClient
    {
        BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"),

        // Short enough that a dead gateway shows the offline banner promptly rather
        // than leaving the operator watching a spinner, long enough for a five
        // thousand reading batch to be accepted.
        Timeout = TimeSpan.FromSeconds(30)
    },
    services.GetRequiredService<GatewayStatus>(),
    services.GetRequiredService<ILogger<SmartXApiClient>>()));

await builder.Build().RunAsync();
