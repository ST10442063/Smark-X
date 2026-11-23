using SmartX.Api.Services.Streaming;

namespace SmartX.Api.Services;

/// <summary>
/// Runs the seeder once at startup.
/// <para>
/// A hosted service is a singleton, and the seeder depends on scoped services, so
/// resolving it directly would either capture a scoped dependency for the life of
/// the process or fail validation outright. This wrapper creates a scope, runs the
/// seed inside it, and disposes it - which is the same lifetime shape a request
/// would have given the seeder.
/// </para>
/// <para>
/// Once the telemetry is in, the command stream loads each device's timeline from
/// the seeded history, and the pattern miner is given its demonstration sessions.
/// </para>
/// </summary>
public sealed class SeedHostedService(IServiceScopeFactory scopeFactory) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<TelemetrySeeder>();

        await seeder.StartAsync(cancellationToken);

        scope.ServiceProvider.GetRequiredService<CommandStreamStore>().BackfillFromHistory();
        scope.ServiceProvider.GetRequiredService<OperatorHistorySeeder>().Seed();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
