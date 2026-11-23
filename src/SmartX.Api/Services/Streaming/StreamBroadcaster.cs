using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Insights;
using SmartX.Contracts.Stream;
using SmartX.Core.Devices;
using SmartX.Core.Streaming;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// Pushes the command stream to every connected console over SignalR.
/// <para>
/// <b>Two speeds, on purpose.</b> The push side mirrors the intake. A critical
/// alert gets its own channel, read by its own loop, and goes out the moment it is
/// processed - it never waits for a batch. Everything else (processed packets,
/// device changes, history entries, queue depths) is gathered from the store's
/// change journal and sent every half second. A fleet publishing every few seconds
/// would otherwise produce one browser re-render per packet, and the console would
/// spend its time redrawing rather than staying responsive.
/// </para>
/// <para>
/// Each batch takes the store lock once, maps everything it needs, releases the
/// lock, and only then talks to the network, so a slow browser can never hold up
/// ingestion.
/// </para>
/// </summary>
public sealed class StreamBroadcaster(
    IHubContext<StreamHub> hub,
    CommandStreamStore store,
    DualLaneDispatchQueue<QueuedPacket> queue,
    ILogger<StreamBroadcaster> logger) : BackgroundService
{
    /// <summary>Most packets sent per batch. A flood is summarised by the queue stats rather than streamed in full.</summary>
    public const int MaxPacketsPerBatch = 60;

    private readonly Channel<CriticalAlertResponse> _critical = Channel.CreateUnbounded<CriticalAlertResponse>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queues a critical alert for immediate delivery. Never blocks.</summary>
    public void PushCritical(CriticalAlertResponse alert) => _critical.Writer.TryWrite(alert);

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(CriticalLoopAsync(stoppingToken), BatchLoopAsync(stoppingToken));

    private async Task CriticalLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var alert in _critical.Reader.ReadAllAsync(cancellationToken))
            {
                await SendAsync(StreamHubContract.CriticalAlert, alert, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task BatchLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(store.Options.BroadcastIntervalMs);

        // Trend insights move with time even when nothing is published, so the
        // suggestions are recomputed every few seconds as well as whenever
        // something happens that could change them.
        var refreshEvery = Math.Max(1, (int)(5000 / interval.TotalMilliseconds));
        using var timer = new PeriodicTimer(interval);
        var tick = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                tick++;

                try
                {
                    var batch = Collect(forceSuggestions: tick % refreshEvery == 0);
                    await PublishAsync(batch, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // A failed push must never stop the stream.
                    logger.LogWarning(error, "Stream broadcast failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private Batch Collect(bool forceSuggestions)
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = queue.Snapshot();

        return store.Write(state =>
        {
            InsightsResponse? insights = null;
            CommandHistoryResponse? commands = null;

            if (state.SuggestionsDirty || forceSuggestions)
            {
                var before = state.Suggestions.Where(s => s.Target is not null).Select(s => s.Target!.Value).ToHashSet();
                store.RefreshSuggestions(state, now);
                var after = state.Suggestions.Where(s => s.Target is not null).Select(s => s.Target!.Value).ToHashSet();

                // Devices that gained or lost a flag need redrawing: exactly the
                // symmetric difference of the two sets.
                before.SymmetricExceptWith(after);
                state.ChangedDevices.UnionWith(before);

                insights = StreamMapper.Insights(state, now, store.Options);
            }

            if (state.CommandsChanged)
            {
                commands = StreamMapper.History(state);
                state.CommandsChanged = false;
            }

            var devices = new List<DeviceLiveResponse>(state.ChangedDevices.Count);
            foreach (var mac in state.ChangedDevices)
            {
                if (state.Devices.TryGet(mac, out var device))
                {
                    devices.Add(StreamMapper.ToLive(state, device, now));
                }
            }

            state.ChangedDevices.Clear();

            var packets = state.PendingPackets.Count <= MaxPacketsPerBatch
                ? [.. state.PendingPackets]
                : state.PendingPackets.GetRange(state.PendingPackets.Count - MaxPacketsPerBatch, MaxPacketsPerBatch);
            state.PendingPackets.Clear();

            var events = state.PendingEvents.Select(entry => StreamMapper.Event(state, entry)).ToList();
            state.PendingEvents.Clear();

            var stats = StreamMapper.QueueStats(metrics, state, now, store.Options.SimulatedProcessingMs);

            return new Batch(stats, packets, devices, events, commands, insights);
        });
    }

    private async Task PublishAsync(Batch batch, CancellationToken cancellationToken)
    {
        await SendAsync(StreamHubContract.QueueStats, batch.Stats, cancellationToken);

        if (batch.Packets.Count > 0)
        {
            await SendAsync(StreamHubContract.PacketsProcessed, batch.Packets, cancellationToken);
        }

        if (batch.Devices.Count > 0)
        {
            await SendAsync(StreamHubContract.DevicesChanged, batch.Devices, cancellationToken);
        }

        if (batch.Events.Count > 0)
        {
            await SendAsync(StreamHubContract.EventsLogged, batch.Events, cancellationToken);
        }

        if (batch.Commands is not null)
        {
            await SendAsync(StreamHubContract.CommandsChanged, batch.Commands, cancellationToken);
        }

        if (batch.Insights is not null)
        {
            await SendAsync(StreamHubContract.SuggestionsChanged, batch.Insights, cancellationToken);
        }
    }

    private Task SendAsync<T>(string method, T payload, CancellationToken cancellationToken)
        => hub.Clients.All.SendAsync(method, payload, cancellationToken);

    private sealed record Batch(
        QueueStatsResponse Stats,
        List<ProcessedPacketResponse> Packets,
        List<DeviceLiveResponse> Devices,
        List<StreamEventResponse> Events,
        CommandHistoryResponse? Commands,
        InsightsResponse? Insights);
}
