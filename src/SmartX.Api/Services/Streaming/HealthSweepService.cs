using SmartX.Core.Devices;
using SmartX.Core.Insights;
using SmartX.Core.Models;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// Sweeps the fleet every few seconds for nodes that have gone silent and
/// commands that were never confirmed.
/// <para>
/// The sweep finds every Silent node and hands the whole set to the
/// <see cref="SmartX.Core.Alerts.OutageTracker"/>, which reconciles it against the
/// nodes already known to be down by set difference. Only the difference raises
/// anything. A node that has been offline for an hour is found silent on seven
/// hundred sweeps and reported on exactly one.
/// </para>
/// </summary>
public sealed class HealthSweepService(
    CommandStreamStore store,
    ILogger<HealthSweepService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(store.Options.SweepIntervalSeconds);
        var timeout = TimeSpan.FromSeconds(store.Options.CommandTimeoutSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                try
                {
                    Sweep(DateTimeOffset.UtcNow, timeout);
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Health sweep failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Runs one sweep. Public so the integration tests can drive it without waiting.</summary>
    public void Sweep(DateTimeOffset now, TimeSpan commandTimeout)
    {
        store.Write(state =>
        {
            var silent = new List<MacKey>();
            foreach (var device in state.Devices.All)
            {
                if (device.EvaluateStatus(now) == SensorStatus.Silent)
                {
                    silent.Add(device.Mac);
                }
            }

            var sweep = state.Outages.Reconcile(silent);

            foreach (var mac in sweep.NewlyDown)
            {
                if (!state.Devices.TryGet(mac, out var device))
                {
                    continue;
                }

                var silence = device.LastSeenUtc is { } seen
                    ? $"No packet for {(now - seen).TotalSeconds:0} s, against a heartbeat every {device.HeartbeatIntervalSeconds} s."
                    : "Registered but has never reported.";

                state.Log(StreamEventKind.NodeDown, StreamEventLevel.Critical, now, mac,
                    $"{device.DisplayName} went offline", silence);

                state.Miner.Observe(ActivityToken.Disconnect(mac), now);
                state.ChangedDevices.Add(mac);
            }

            foreach (var mac in sweep.Recovered)
            {
                state.Log(StreamEventKind.NodeRecovered, StreamEventLevel.Info, now, mac,
                    $"{state.NameOf(mac)} is reporting again", "Back inside its heartbeat window.");
                state.ChangedDevices.Add(mac);
            }

            if (sweep.NewlyDown.Count > 0 || sweep.Recovered.Count > 0)
            {
                state.SuggestionsDirty = true;
            }

            CommandService.ExpireStale(state, now, commandTimeout);
        });
    }
}
