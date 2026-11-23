using SmartX.Core.Telemetry;

namespace SmartX.Core.History;

/// <summary>
/// One <see cref="TelemetryAvlTree"/> per sensor, found by sensor id in O(1), with
/// a retention window so the trees cannot grow without bound.
/// <para>
/// Retention is measured back from each sensor's newest reading rather than from
/// the wall clock, the same rule <see cref="Timeline.SensorTimeline"/> follows. A
/// node that goes quiet for a weekend therefore keeps its last two days of history
/// for the operator investigating why, instead of losing it to a clock that kept
/// running while nothing arrived.
/// </para>
/// <para>Not thread-safe. The gateway store calls it under its own lock.</para>
/// </summary>
public sealed class SensorHistoryIndex
{
    private readonly Dictionary<Guid, TelemetryAvlTree> _trees = [];

    /// <summary>Creates an index that keeps <paramref name="retention"/> of history per sensor.</summary>
    public SensorHistoryIndex(TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        Retention = retention;
    }

    /// <summary>How much history is kept behind each sensor's newest reading.</summary>
    public TimeSpan Retention { get; }

    /// <summary>Sensors with a tree.</summary>
    public int SensorCount => _trees.Count;

    /// <summary>Readings that arrived already older than the retention window, and were not stored.</summary>
    public long Discarded { get; private set; }

    /// <summary>Readings held across every tree.</summary>
    public long TotalReadings
    {
        get
        {
            var total = 0L;
            foreach (var tree in _trees.Values)
            {
                total += tree.Count;
            }

            return total;
        }
    }

    /// <summary>
    /// Inserts a batch of packets into their sensor's tree, then trims anything that
    /// has fallen out of the retention window.
    /// </summary>
    /// <returns>Readings stored.</returns>
    public int Append(Guid sensorId, IReadOnlyList<ITelemetryPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        if (packets.Count == 0)
        {
            return 0;
        }

        if (!_trees.TryGetValue(sensorId, out var tree))
        {
            tree = new TelemetryAvlTree();
            _trees[sensorId] = tree;
        }

        var newest = tree.NewestUtc ?? DateTimeOffset.MinValue;
        foreach (var packet in packets)
        {
            if (packet.TimestampUtc > newest)
            {
                newest = packet.TimestampUtc;
            }
        }

        var cutoff = newest - Retention;
        var stored = 0;

        foreach (var packet in packets)
        {
            // Inserting a reading only to trim it straight back out would cost two
            // O(log n) operations for nothing.
            if (packet.TimestampUtc < cutoff)
            {
                Discarded++;
                continue;
            }

            tree.Insert(packet.TimestampUtc, packet.NumericValue);
            stored++;
        }

        tree.TrimBefore(cutoff);
        return stored;
    }

    /// <summary>The sensor's tree, or null when it has never reported.</summary>
    public TelemetryAvlTree? Find(Guid sensorId) => _trees.GetValueOrDefault(sensorId);
}
