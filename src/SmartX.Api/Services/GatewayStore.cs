using Microsoft.Extensions.Options;
using SmartX.Api.Models;
using SmartX.Core.Collections;
using SmartX.Core.History;
using SmartX.Core.Models;
using SmartX.Core.Storage;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// The gateway's in-memory state: the sensor registry, one ingestion ring buffer
/// and one historical batch store per sensor, the attachment index, and the open
/// anomaly list.
/// <para>
/// Registered as a singleton, so every request shares one instance. Each of the
/// data structures it holds is documented as single-threaded, so this class owns
/// the lock that makes them safe under concurrent requests. Every method that
/// touches state takes that lock, and every method that returns a collection
/// returns a snapshot rather than a live reference - a caller enumerating a live
/// list while another request appended to it would throw, and the failure would
/// only appear under load.
/// </para>
/// <para>
/// State is deliberately in-memory for Part 1. The brief scopes persistence out,
/// and the data structures are the thing being demonstrated; swapping the backing
/// store later means reimplementing this one class.
/// </para>
/// </summary>
public sealed class GatewayStore
{
    private readonly object _gate = new();
    private readonly GatewayOptions _options;

    private readonly SortedSensorIndex _index = new();
    private readonly Dictionary<Guid, IngestionBuffer<ITelemetryPacket>> _buffers = [];
    private readonly Dictionary<Guid, HistoricalBatchStore> _history = [];

    // The final PoE's time index: every reading again, this time in a per-sensor
    // AVL tree ordered by timestamp, so the topology page can query a time window
    // without scanning the arrival-ordered batches above.
    private readonly SensorHistoryIndex _timeIndex;
    private readonly Dictionary<Guid, List<SensorAttachment>> _attachments = [];
    private readonly List<Anomaly> _anomalies = [];
    private readonly HashSet<Guid> _simulated = [];

    // One entry per outage already reported: the node, and when it last reported
    // before going silent. See TryRaiseDisconnect.
    private readonly HashSet<(Guid SensorId, DateTimeOffset OutageStartedUtc)> _reportedOutages = [];

    /// <summary>Creates the store with the configured buffer capacity.</summary>
    public GatewayStore(IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeIndex = new SensorHistoryIndex(TimeSpan.FromHours(_options.HistoryRetentionHours));
    }

    /// <summary>When the gateway started, used for uptime reporting.</summary>
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Consecutive readings accepted without raising an anomaly.</summary>
    public long CleanIngestStreak { get; private set; }

    /// <summary>Readings accepted since startup.</summary>
    public long TotalReadingsAccepted { get; private set; }

    // -----------------------------------------------------------------
    // Registry
    // -----------------------------------------------------------------

    /// <summary>
    /// Raised after a sensor is registered, once this store's lock has been released.
    /// The Part 2 command stream listens to keep its live device registry in step;
    /// raising it outside the lock means a listener taking its own lock can never
    /// deadlock against this one.
    /// </summary>
    public event Action<SensorRecord>? SensorRegistered;

    /// <summary>Registers a sensor. Returns false when its MAC is already known.</summary>
    public bool TryRegister(SensorRecord sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        lock (_gate)
        {
            if (!_index.Add(sensor))
            {
                return false;
            }

            _buffers[sensor.Id] = new IngestionBuffer<ITelemetryPacket>(_options.BufferCapacity);
            _history[sensor.Id] = new HistoricalBatchStore(sensor.Unit);
            _attachments[sensor.Id] = [];
        }

        SensorRegistered?.Invoke(sensor);
        return true;
    }

    /// <summary>Finds a sensor by MAC address in O(log n), and reports the comparisons used.</summary>
    public (SensorRecord? Sensor, int Comparisons) FindByMac(string? macAddress)
    {
        lock (_gate)
        {
            var sensor = _index.FindByMac(macAddress);
            return (sensor, _index.LastBinarySearchComparisons);
        }
    }

    /// <summary>Finds a sensor by its gateway-assigned key.</summary>
    public SensorRecord? FindById(Guid id)
    {
        lock (_gate)
        {
            return _index.FindById(id);
        }
    }

    /// <summary>Free-text search across the registry, and the records it had to scan.</summary>
    public (List<SensorRecord> Matches, int Comparisons) Search(string? query)
    {
        lock (_gate)
        {
            var matches = _index.Search(query);
            return (matches, _index.LastLinearSearchComparisons);
        }
    }

    /// <summary>Every registered sensor, in MAC order.</summary>
    public List<SensorRecord> AllSensors()
    {
        lock (_gate)
        {
            return [.. _index.Sensors];
        }
    }

    /// <summary>
    /// Marks the sensors the device emulator should drive - the fleet it is handed in
    /// the <c>/api/devices/manifest</c> provisioning record. Held as
    /// an explicit set rather than inferred from registry position: the registry is
    /// ordered by MAC, so "every node but the last" changes meaning the moment an
    /// operator registers a node that sorts after the deliberately silent one.
    /// </summary>
    public void MarkSimulated(IEnumerable<Guid> sensorIds)
    {
        ArgumentNullException.ThrowIfNull(sensorIds);

        lock (_gate)
        {
            _simulated.UnionWith(sensorIds);
        }
    }

    /// <summary>The registered sensors handed to the device emulator, in MAC order.</summary>
    public List<SensorRecord> SimulatedSensors()
    {
        lock (_gate)
        {
            return [.. _index.Sensors.Where(sensor => _simulated.Contains(sensor.Id))];
        }
    }

    /// <summary>Number of registered sensors.</summary>
    public int SensorCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>Distinct zone labels across the registry.</summary>
    public List<string> Zones()
    {
        lock (_gate)
        {
            return _index.Zones();
        }
    }

    /// <summary>Comparison counts from the most recent search of each kind.</summary>
    public (int Binary, int Linear) LastSearchCosts()
    {
        lock (_gate)
        {
            return (_index.LastBinarySearchComparisons, _index.LastLinearSearchComparisons);
        }
    }

    // -----------------------------------------------------------------
    // Ingestion
    // -----------------------------------------------------------------

    /// <summary>
    /// Accepts a batch of packets for a sensor: stages them in the ring buffer,
    /// appends them to the jagged historical store as one batch, and advances the
    /// sensor heartbeat.
    /// </summary>
    /// <param name="sensorId">Sensor the packets belong to.</param>
    /// <param name="packets">The packets, oldest first.</param>
    /// <returns>Buffer occupancy and history depth after the append.</returns>
    public IngestionSnapshot Accept(Guid sensorId, IReadOnlyList<ITelemetryPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        lock (_gate)
        {
            var buffer = BufferFor(sensorId);
            var history = HistoryFor(sensorId);

            foreach (var packet in packets)
            {
                buffer.Add(packet);
            }

            // One call, one batch row - which is what makes the jagged layout
            // reflect how the data actually arrived.
            history.AppendBatch(packets);
            _timeIndex.Append(sensorId, packets);

            var sensor = _index.FindById(sensorId);
            if (sensor is not null && packets.Count > 0)
            {
                sensor.LastSeenUtc = packets[^1].TimestampUtc;
                sensor.PacketsReceived += packets.Count;
            }

            TotalReadingsAccepted += packets.Count;
            CleanIngestStreak += packets.Count;

            return new IngestionSnapshot(
                buffer.Count,
                buffer.Capacity,
                buffer.OverwrittenCount,
                history.BatchCount,
                history.TotalReadings);
        }
    }

    /// <summary>The most recent readings held for a sensor, oldest first.</summary>
    public List<SensorReading> RecentReadings(Guid sensorId, int count)
    {
        lock (_gate)
        {
            return BufferFor(sensorId).TakeNewest(count).Select(packet =>
                new SensorReading(packet.NumericValue, packet.Unit, packet.TimestampUtc)).ToList();
        }
    }

    /// <summary>Ring-buffer occupancy, capacity and overwrite count for one sensor.</summary>
    public (int Count, int Capacity, long Overwritten) BufferState(Guid sensorId)
    {
        lock (_gate)
        {
            var buffer = BufferFor(sensorId);
            return (buffer.Count, buffer.Capacity, buffer.OverwrittenCount);
        }
    }

    /// <summary>Combined ring-buffer occupancy, capacity and overwrite count across the fleet.</summary>
    public (int Count, int Capacity, long Overwritten) FleetBufferState()
    {
        lock (_gate)
        {
            var count = 0;
            var capacity = 0;
            var overwritten = 0L;

            foreach (var buffer in _buffers.Values)
            {
                count += buffer.Count;
                capacity += buffer.Capacity;
                overwritten += buffer.OverwrittenCount;
            }

            return (count, capacity, overwritten);
        }
    }

    /// <summary>
    /// Runs <paramref name="reader"/> against a sensor historical store under the
    /// lock. The store is handed in rather than returned so it cannot be read after
    /// the lock is released, which would race with a concurrent append.
    /// </summary>
    public T ReadHistory<T>(Guid sensorId, Func<HistoricalBatchStore, T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock (_gate)
        {
            return reader(HistoryFor(sensorId));
        }
    }

    /// <summary>
    /// Runs <paramref name="reader"/> against a sensor's AVL history tree under the
    /// lock - null when the sensor has never reported. Handed in rather than returned
    /// for the same reason as <see cref="ReadHistory{T}"/>.
    /// </summary>
    public T ReadTimeIndex<T>(Guid sensorId, Func<TelemetryAvlTree?, T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock (_gate)
        {
            return reader(_timeIndex.Find(sensorId));
        }
    }

    /// <summary>Sensors and readings held in the AVL time index, and the retention it keeps.</summary>
    public (int Sensors, long Readings, TimeSpan Retention) TimeIndexState()
    {
        lock (_gate)
        {
            return (_timeIndex.SensorCount, _timeIndex.TotalReadings, _timeIndex.Retention);
        }
    }

    /// <summary>Historical batch count and reading total across the fleet.</summary>
    public (int Batches, long Readings) FleetHistoryState()
    {
        lock (_gate)
        {
            var batches = 0;
            var readings = 0L;

            foreach (var store in _history.Values)
            {
                batches += store.BatchCount;
                readings += store.TotalReadings;
            }

            return (batches, readings);
        }
    }

    // -----------------------------------------------------------------
    // Attachments
    // -----------------------------------------------------------------

    /// <summary>Records an uploaded file against a sensor profile.</summary>
    public void AddAttachment(SensorAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        lock (_gate)
        {
            if (!_attachments.TryGetValue(attachment.SensorId, out var list))
            {
                list = [];
                _attachments[attachment.SensorId] = list;
            }

            list.Add(attachment);
        }
    }

    /// <summary>Files attached to one sensor, newest first.</summary>
    public List<SensorAttachment> Attachments(Guid sensorId)
    {
        lock (_gate)
        {
            return _attachments.TryGetValue(sensorId, out var list)
                ? [.. list.OrderByDescending(item => item.UploadedAtUtc)]
                : [];
        }
    }

    /// <summary>One attachment by sensor and attachment key.</summary>
    public SensorAttachment? FindAttachment(Guid sensorId, Guid attachmentId)
    {
        lock (_gate)
        {
            return _attachments.TryGetValue(sensorId, out var list)
                ? list.FirstOrDefault(item => item.Id == attachmentId)
                : null;
        }
    }

    /// <summary>Total files attached across every sensor.</summary>
    public int AttachmentCount
    {
        get
        {
            lock (_gate)
            {
                return _attachments.Values.Sum(list => list.Count);
            }
        }
    }

    /// <summary>How many files are attached to one sensor.</summary>
    public int AttachmentCountFor(Guid sensorId)
    {
        lock (_gate)
        {
            return _attachments.TryGetValue(sensorId, out var list) ? list.Count : 0;
        }
    }

    // -----------------------------------------------------------------
    // Anomalies
    // -----------------------------------------------------------------

    /// <summary>Records an anomaly and resets the clean-ingest streak.</summary>
    public void RaiseAnomaly(Anomaly anomaly)
    {
        ArgumentNullException.ThrowIfNull(anomaly);

        lock (_gate)
        {
            _anomalies.Add(anomaly);
            CleanIngestStreak = 0;
        }
    }

    /// <summary>Anomalies, worst first.</summary>
    /// <param name="openOnly">When true, acknowledged anomalies are excluded.</param>
    /// <param name="limit">Maximum to return.</param>
    public List<Anomaly> Anomalies(bool openOnly = true, int limit = 50)
    {
        lock (_gate)
        {
            return _anomalies
                .Where(anomaly => !openOnly || !anomaly.Acknowledged)
                .OrderByDescending(anomaly => anomaly.TriageWeight)
                .ThenByDescending(anomaly => anomaly.DetectedAtUtc)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>Clears one anomaly. Returns false when the key is unknown.</summary>
    public bool Acknowledge(Guid anomalyId)
    {
        lock (_gate)
        {
            var anomaly = _anomalies.FirstOrDefault(item => item.Id == anomalyId);
            if (anomaly is null || anomaly.Acknowledged)
            {
                return false;
            }

            anomaly.Acknowledged = true;
            return true;
        }
    }

    /// <summary>Clears every open anomaly and returns how many were cleared.</summary>
    public int AcknowledgeAll()
    {
        lock (_gate)
        {
            var cleared = 0;
            foreach (var anomaly in _anomalies.Where(item => !item.Acknowledged))
            {
                anomaly.Acknowledged = true;
                cleared++;
            }

            return cleared;
        }
    }

    /// <summary>Total and open anomaly counts.</summary>
    public (int Total, int Open) AnomalyCounts()
    {
        lock (_gate)
        {
            return (_anomalies.Count, _anomalies.Count(anomaly => !anomaly.Acknowledged));
        }
    }

    /// <summary>Open anomalies against one sensor.</summary>
    public int OpenAnomaliesFor(Guid sensorId)
    {
        lock (_gate)
        {
            return _anomalies.Count(anomaly => anomaly.SensorId == sensorId && !anomaly.Acknowledged);
        }
    }

    /// <summary>
    /// Records a disconnect anomaly only if this outage has not already been
    /// reported, checking and adding under a single lock.
    /// <para>
    /// "This outage" means since the node last reported, so an outage is identified
    /// by the pair (node, last-seen time) - which stays fixed for as long as the node
    /// is silent. Those pairs live in a <see cref="HashSet{T}"/>, and
    /// <see cref="HashSet{T}.Add"/> is both the check and the record: it returns
    /// false for an outage already reported. That replaced a scan of every anomaly
    /// ever raised on each sweep, which grew with the gateway's uptime.
    /// </para>
    /// <para>
    /// An alert the operator has acknowledged still counts: without that,
    /// acknowledging a node that is still offline raised an identical alert on the
    /// very next sweep, a few seconds later, so the Acknowledge button appeared to do
    /// nothing. A node that recovers and then drops again has a new last-seen time,
    /// so it is a new outage and alerts again.
    /// </para>
    /// <para>
    /// The check and the insert have to be one atomic operation. Asking
    /// "is one already open?" and then adding takes the lock twice, and several
    /// dashboard endpoints sweep for disconnects - the client fires four of them
    /// concurrently on every poll - so two sweeps could both see no open anomaly
    /// before either had added one. That produced duplicate alerts for a single
    /// offline node, which is exactly what the check exists to prevent.
    /// </para>
    /// </summary>
    /// <param name="anomaly">The disconnect to record.</param>
    /// <param name="lastSeenUtc">When the node last reported, or null if it never has.</param>
    /// <returns>True when the anomaly was recorded.</returns>
    public bool TryRaiseDisconnect(Anomaly anomaly, DateTimeOffset? lastSeenUtc)
    {
        ArgumentNullException.ThrowIfNull(anomaly);

        var outageStartedUtc = lastSeenUtc ?? DateTimeOffset.MinValue;

        lock (_gate)
        {
            if (!_reportedOutages.Add((anomaly.SensorId, outageStartedUtc)))
            {
                return false;
            }

            _anomalies.Add(anomaly);
            CleanIngestStreak = 0;
            return true;
        }
    }

    // -----------------------------------------------------------------
    // Helpers - callers already hold the lock
    // -----------------------------------------------------------------

    private IngestionBuffer<ITelemetryPacket> BufferFor(Guid sensorId)
    {
        if (!_buffers.TryGetValue(sensorId, out var buffer))
        {
            buffer = new IngestionBuffer<ITelemetryPacket>(_options.BufferCapacity);
            _buffers[sensorId] = buffer;
        }

        return buffer;
    }

    private HistoricalBatchStore HistoryFor(Guid sensorId)
    {
        if (!_history.TryGetValue(sensorId, out var store))
        {
            var unit = _index.FindById(sensorId)?.Unit ?? TelemetryUnit.None;
            store = new HistoricalBatchStore(unit);
            _history[sensorId] = store;
        }

        return store;
    }
}

/// <summary>Pipeline state immediately after an ingestion call.</summary>
/// <param name="BufferCount">Live packets in the ring buffer.</param>
/// <param name="BufferCapacity">Ring-buffer capacity.</param>
/// <param name="BufferOverwritten">Packets the ring buffer has overwritten.</param>
/// <param name="BatchCount">Historical batches held.</param>
/// <param name="TotalReadings">Readings held across those batches.</param>
public readonly record struct IngestionSnapshot(
    int BufferCount,
    int BufferCapacity,
    long BufferOverwritten,
    int BatchCount,
    long TotalReadings);
