using SmartX.Api.Models;
using SmartX.Api.Services.Streaming;
using SmartX.Core.Models;
using SmartX.Core.Streaming;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>What processing a set of packets produced.</summary>
/// <param name="Snapshot">Part 1 pipeline state after the append.</param>
/// <param name="Anomalies">Readings the rolling z-score flagged.</param>
public readonly record struct PipelineResult(IngestionSnapshot Snapshot, IReadOnlyList<Anomaly> Anomalies);

/// <summary>
/// The one path every accepted reading takes, whether it came through the Part 2
/// dispatch queue or straight from the Part 1 workbench:
/// <list type="number">
/// <item>Part 1 storage - the ring buffer, the jagged historical store, the heartbeat.</item>
/// <item>Part 1 scoring - the rolling z-score anomaly detector.</item>
/// <item>Part 2 live state - the device shadow, timeline, alert set and event log,
/// with any critical alert pushed to the console straight away.</item>
/// </list>
/// Keeping it to one path is what lets both ingestion routes agree about a node
/// without either knowing the other exists.
/// </summary>
public sealed class TelemetryPipeline(
    GatewayStore store,
    AnomalyDetectionService detector,
    CommandStreamStore stream,
    StreamBroadcaster broadcaster)
{
    /// <summary>Processes packets accepted synchronously - the Part 1 workbench.</summary>
    public PipelineResult ProcessDirect(SensorRecord sensor, IReadOnlyList<ITelemetryPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(packets);

        var snapshot = store.Accept(sensor.Id, packets);

        // Scored after acceptance, so each new reading is measured against the
        // window that genuinely preceded it in the buffer.
        var anomalies = detector.InspectLatest(sensor, packets.Count);

        foreach (var observation in stream.ObserveDirect(sensor, packets))
        {
            Push(observation);
        }

        return new PipelineResult(snapshot, anomalies);
    }

    /// <summary>Processes one packet the dispatch worker took from the queue.</summary>
    /// <returns>False when the device has been removed since the packet was queued.</returns>
    public bool ProcessQueued(Dispatched<QueuedPacket> dispatched)
    {
        var item = dispatched.Item;

        // O(1): the Part 1 index now keeps a key dictionary beside its MAC-ordered list.
        var sensor = store.FindById(item.SensorId);
        if (sensor is null)
        {
            return false;
        }

        store.Accept(sensor.Id, [item.Packet]);
        detector.InspectLatest(sensor, 1);
        Push(stream.ObserveQueued(dispatched));
        return true;
    }

    private void Push(Observation observation)
    {
        // Only a new condition interrupts the operator. A repeat of one already
        // active was absorbed by the alert set and travels with the routine batch.
        if (observation.Alert is { IsNew: true } alert)
        {
            broadcaster.PushCritical(alert);
        }
    }
}
