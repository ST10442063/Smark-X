using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Storage;

/// <summary>
/// Holds one sensor's historical telemetry as a sequence of raw batches, then
/// hands them to the query layer as a <see cref="List{T}"/>.
/// <para>
/// <b>Why jagged rather than a rectangular array.</b> Batches arrive at whatever
/// size the node happened to buffer - a gateway flushing on a timer sends 40
/// readings, one flushing on reconnect after an outage might send 900. A
/// rectangular <c>double[batches, readings]</c> would have to size every row to
/// the largest batch ever seen, so those 40-reading batches would each carry 860
/// unused slots. A jagged <c>double[][]</c> allocates each row at exactly the
/// length it needs, so the footprint tracks the data rather than the worst case.
/// </para>
/// <para>
/// Values and timestamps are kept in two parallel jagged arrays of identical shape
/// rather than in one array of a reading struct. Keeping them apart means a
/// statistical pass over the values - a mean, a standard deviation, a min/max -
/// touches only the numbers it needs, so a cache line holds eight useful doubles
/// instead of being half-filled with timestamps the calculation never reads.
/// </para>
/// </summary>
public sealed class HistoricalBatchStore
{
    private const int InitialCapacity = 4;

    // Parallel jagged arrays. _values[b][i] and _timestamps[b][i] are the same
    // reading; the two outer arrays always have the same length and each pair of
    // rows always has the same length.
    private double[][] _values;
    private long[][] _timestamps;
    private int _batchCount;

    /// <summary>Creates an empty store for a sensor publishing in <paramref name="unit"/>.</summary>
    /// <param name="unit">Unit every reading in this store is expressed in.</param>
    public HistoricalBatchStore(TelemetryUnit unit)
    {
        Unit = unit;
        _values = new double[InitialCapacity][];
        _timestamps = new long[InitialCapacity][];
    }

    /// <summary>Unit every reading in this store carries.</summary>
    public TelemetryUnit Unit { get; }

    /// <summary>Number of batches stored.</summary>
    public int BatchCount => _batchCount;

    /// <summary>Total readings across every batch.</summary>
    public long TotalReadings { get; private set; }

    /// <summary>
    /// Slots allocated in the outer arrays. Higher than <see cref="BatchCount"/>
    /// between growth steps, since the outer array doubles rather than growing by one.
    /// </summary>
    public int AllocatedBatchSlots => _values.Length;

    /// <summary>
    /// Appends a raw batch. The row is allocated at exactly
    /// <paramref name="values"/>.Length, which is the point of the jagged layout.
    /// </summary>
    /// <param name="values">Readings in the batch, oldest first.</param>
    /// <param name="timestampsUnixMs">Capture times, one per reading, same order.</param>
    /// <exception cref="ArgumentException">The two spans are different lengths.</exception>
    public void AppendBatch(ReadOnlySpan<double> values, ReadOnlySpan<long> timestampsUnixMs)
    {
        if (values.Length != timestampsUnixMs.Length)
        {
            throw new ArgumentException(
                $"A batch needs one timestamp per reading, but got {values.Length} readings " +
                $"and {timestampsUnixMs.Length} timestamps.",
                nameof(timestampsUnixMs));
        }

        EnsureOuterCapacity();

        _values[_batchCount] = values.ToArray();
        _timestamps[_batchCount] = timestampsUnixMs.ToArray();
        _batchCount++;
        TotalReadings += values.Length;
    }

    /// <summary>
    /// Appends a batch of packets, projecting each to its numeric value and capture
    /// time. This is the path the batch ingestion endpoint uses.
    /// </summary>
    public void AppendBatch(IReadOnlyList<ITelemetryPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        if (packets.Count == 0)
        {
            return;
        }

        var values = new double[packets.Count];
        var timestamps = new long[packets.Count];

        for (var i = 0; i < packets.Count; i++)
        {
            values[i] = packets[i].NumericValue;
            timestamps[i] = packets[i].TimestampUtc.ToUnixTimeMilliseconds();
        }

        AppendBatch(values, timestamps);
    }

    /// <summary>The raw values of one batch, by its position in the sequence.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No batch sits at that index.</exception>
    public ReadOnlySpan<double> GetBatch(int batchIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(batchIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(batchIndex, _batchCount);

        return _values[batchIndex];
    }

    /// <summary>Length of the batch at <paramref name="batchIndex"/>.</summary>
    public int GetBatchLength(int batchIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(batchIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(batchIndex, _batchCount);

        return _values[batchIndex].Length;
    }

    /// <summary>
    /// Transfers every stored reading out of the raw arrays into an optimised
    /// generic collection, in ingestion order.
    /// <para>
    /// This is the hand-off the pipeline is built around: arrays are the right
    /// shape for bulk capture and statistical passes, and <see cref="List{T}"/> is
    /// the right shape for the query layer, which needs LINQ, ordering and growth.
    /// The list is pre-sized to <see cref="TotalReadings"/> so it never reallocates
    /// while filling.
    /// </para>
    /// </summary>
    public List<SensorReading> FlattenToList()
    {
        var readings = new List<SensorReading>((int)Math.Min(TotalReadings, int.MaxValue));

        for (var batch = 0; batch < _batchCount; batch++)
        {
            var values = _values[batch];
            var timestamps = _timestamps[batch];

            for (var i = 0; i < values.Length; i++)
            {
                readings.Add(new SensorReading(
                    values[i],
                    Unit,
                    DateTimeOffset.FromUnixTimeMilliseconds(timestamps[i])));
            }
        }

        return readings;
    }

    /// <summary>
    /// The most recent <paramref name="count"/> readings, oldest first, without
    /// materialising the whole history. Walks batches backwards from the newest.
    /// </summary>
    public List<SensorReading> TakeNewest(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var readings = new List<SensorReading>(count);
        if (count == 0)
        {
            return readings;
        }

        // Walk backwards collecting, then reverse once at the end - cheaper than
        // inserting at the front of the list repeatedly.
        for (var batch = _batchCount - 1; batch >= 0 && readings.Count < count; batch--)
        {
            var values = _values[batch];
            var timestamps = _timestamps[batch];

            for (var i = values.Length - 1; i >= 0 && readings.Count < count; i--)
            {
                readings.Add(new SensorReading(
                    values[i],
                    Unit,
                    DateTimeOffset.FromUnixTimeMilliseconds(timestamps[i])));
            }
        }

        readings.Reverse();
        return readings;
    }

    /// <summary>
    /// Mean, minimum and maximum across every stored reading, computed by walking
    /// the value rows only. Returns zeroes when the store is empty.
    /// </summary>
    public (double Mean, double Min, double Max) Summarise()
    {
        if (TotalReadings == 0)
        {
            return (0d, 0d, 0d);
        }

        var sum = 0d;
        var min = double.MaxValue;
        var max = double.MinValue;

        for (var batch = 0; batch < _batchCount; batch++)
        {
            // Only the value rows are touched here - the timestamp rows stay out
            // of cache entirely, which is why the two are kept separate.
            foreach (var value in _values[batch])
            {
                sum += value;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }

        return (sum / TotalReadings, min, max);
    }

    /// <summary>
    /// Lengths of every stored batch, oldest first. Exposed so the dashboard can
    /// show that batch sizes genuinely vary, which is what the jagged layout buys.
    /// </summary>
    public int[] BatchLengths()
    {
        var lengths = new int[_batchCount];
        for (var i = 0; i < _batchCount; i++)
        {
            lengths[i] = _values[i].Length;
        }

        return lengths;
    }

    /// <summary>
    /// Slots a rectangular array would have had to allocate for the same data -
    /// batches multiplied by the longest batch. Compared against
    /// <see cref="TotalReadings"/> this is the concrete saving from going jagged.
    /// </summary>
    public long RectangularEquivalentSlots()
    {
        if (_batchCount == 0)
        {
            return 0;
        }

        var longest = 0;
        for (var i = 0; i < _batchCount; i++)
        {
            longest = Math.Max(longest, _values[i].Length);
        }

        return (long)_batchCount * longest;
    }

    /// <summary>Doubles the outer arrays when the next batch would not fit.</summary>
    private void EnsureOuterCapacity()
    {
        if (_batchCount < _values.Length)
        {
            return;
        }

        var grown = _values.Length * 2;

        // Only the outer arrays of row references are copied here. The rows
        // themselves are untouched, so growth costs a pointer copy per batch
        // rather than a copy of every reading ever stored.
        Array.Resize(ref _values, grown);
        Array.Resize(ref _timestamps, grown);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"HistoricalBatchStore[{Unit}] {_batchCount} batches, {TotalReadings} readings";
}
