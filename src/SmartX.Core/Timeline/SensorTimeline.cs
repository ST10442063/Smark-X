namespace SmartX.Core.Timeline;

/// <summary>
/// One slot of a sensor timeline: every reading that fell inside one
/// <see cref="SensorTimeline.Resolution"/>-wide window, summarised.
/// </summary>
public sealed class TimelineBucket
{
    internal TimelineBucket(DateTimeOffset startUtc) => StartUtc = startUtc;

    /// <summary>Start of the window this bucket covers.</summary>
    public DateTimeOffset StartUtc { get; }

    /// <summary>Lowest reading in the window.</summary>
    public double Minimum { get; private set; } = double.MaxValue;

    /// <summary>Highest reading in the window.</summary>
    public double Maximum { get; private set; } = double.MinValue;

    /// <summary>Sum of the readings, kept so the mean stays exact as readings arrive.</summary>
    public double Sum { get; private set; }

    /// <summary>Readings in the window.</summary>
    public int Count { get; private set; }

    /// <summary>Readings in the window that were triaged as critical.</summary>
    public int Critical { get; private set; }

    /// <summary>Mean of the readings in the window.</summary>
    public double Mean => Count == 0 ? 0d : Sum / Count;

    internal void Add(double value, bool critical)
    {
        Minimum = Math.Min(Minimum, value);
        Maximum = Math.Max(Maximum, value);
        Sum += value;
        Count++;

        if (critical)
        {
            Critical++;
        }
    }
}

/// <summary>
/// A sensor's recent history, bucketed by time and kept in timestamp order, which
/// is what the Command Stream timeline graph draws from.
/// <para>
/// <b>Why a <see cref="SortedDictionary{TKey,TValue}"/>.</b> Telemetry does not
/// arrive in order. A node that loses its link buffers readings and flushes them
/// when it reconnects, so a packet stamped ten minutes ago can arrive after one
/// stamped now. Appending to a list would put it in the wrong place, and the list
/// would have to be sorted again before every chart render. The sorted dictionary
/// is a red-black tree keyed by bucket start, so a late reading is inserted into
/// the right slot in O(log n) and the timeline always enumerates oldest-first
/// with no sorting at read time.
/// </para>
/// <para>
/// <b>Why buckets rather than raw readings.</b> A graph a few hundred pixels wide
/// cannot show 600 points per node, and the browser should not have to download
/// them. Each bucket keeps min, max, sum and count, so the graph can draw the
/// min-max band and the mean line exactly, at a fixed cost per bucket however many
/// readings landed in it.
/// </para>
/// <para>
/// <b>Bounded.</b> Buckets older than <see cref="Retention"/> behind the newest
/// are evicted. The oldest key is the tree's leftmost node, so finding it is
/// O(log n). Not thread-safe: the stream store serialises access.
/// </para>
/// </summary>
public sealed class SensorTimeline
{
    private readonly SortedDictionary<DateTimeOffset, TimelineBucket> _buckets = [];
    private DateTimeOffset? _newestStart;

    /// <summary>Creates a timeline.</summary>
    /// <param name="resolution">Width of each bucket. Defaults to one minute.</param>
    /// <param name="retention">How far behind the newest bucket history is kept. Defaults to six hours.</param>
    public SensorTimeline(TimeSpan? resolution = null, TimeSpan? retention = null)
    {
        Resolution = resolution ?? TimeSpan.FromMinutes(1);
        Retention = retention ?? TimeSpan.FromHours(6);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Resolution, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(Retention, Resolution);
    }

    /// <summary>Width of each bucket.</summary>
    public TimeSpan Resolution { get; }

    /// <summary>How much history is kept behind the newest bucket.</summary>
    public TimeSpan Retention { get; }

    /// <summary>Buckets currently held.</summary>
    public int Count => _buckets.Count;

    /// <summary>Readings added, across every bucket ever held.</summary>
    public long ReadingsAdded { get; private set; }

    /// <summary>
    /// Readings that arrived for an earlier bucket than the newest one - the
    /// out-of-order case the sorted structure exists for.
    /// </summary>
    public long LateArrivals { get; private set; }

    /// <summary>Readings older than the retention window, which were dropped.</summary>
    public long Discarded { get; private set; }

    /// <summary>Start of the newest bucket, or null while empty.</summary>
    public DateTimeOffset? NewestStartUtc => _newestStart;

    /// <summary>
    /// Adds a reading to the bucket its timestamp falls in, creating the bucket if
    /// needed. O(log n) whatever order readings arrive in.
    /// </summary>
    /// <param name="timestampUtc">When the reading was taken.</param>
    /// <param name="value">The reading.</param>
    /// <param name="critical">Whether triage marked it critical.</param>
    /// <returns>False when the reading was too old to keep.</returns>
    public bool Add(DateTimeOffset timestampUtc, double value, bool critical = false)
    {
        var start = BucketStart(timestampUtc);

        if (_newestStart is { } newest && start < newest - Retention)
        {
            Discarded++;
            return false;
        }

        if (!_buckets.TryGetValue(start, out var bucket))
        {
            bucket = new TimelineBucket(start);
            _buckets.Add(start, bucket);
        }

        bucket.Add(value, critical);
        ReadingsAdded++;

        if (_newestStart is null || start > _newestStart)
        {
            _newestStart = start;
            EvictExpired();
        }
        else if (start < _newestStart)
        {
            LateArrivals++;
        }

        return true;
    }

    /// <summary>
    /// Buckets whose start falls within <c>[fromUtc, toUtc]</c>, oldest first.
    /// Enumeration is already in key order, so the range is a walk that stops at
    /// the first bucket past <paramref name="toUtc"/>.
    /// </summary>
    public List<TimelineBucket> Range(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var result = new List<TimelineBucket>();
        if (toUtc < fromUtc)
        {
            return result;
        }

        foreach (var (start, bucket) in _buckets)
        {
            if (start > toUtc)
            {
                break;
            }

            if (start >= fromUtc)
            {
                result.Add(bucket);
            }
        }

        return result;
    }

    /// <summary>The buckets covering the most recent <paramref name="window"/>, oldest first.</summary>
    public List<TimelineBucket> Latest(TimeSpan window)
        => _newestStart is { } newest
            ? Range(newest - window + Resolution, newest)
            : [];

    /// <summary>Every bucket, oldest first.</summary>
    public IEnumerable<TimelineBucket> All => _buckets.Values;

    /// <summary>Rounds a timestamp down to the start of its bucket.</summary>
    public DateTimeOffset BucketStart(DateTimeOffset timestampUtc)
    {
        var ticks = timestampUtc.UtcTicks;
        return new DateTimeOffset(ticks - (ticks % Resolution.Ticks), TimeSpan.Zero);
    }

    private void EvictExpired()
    {
        var cutoff = _newestStart!.Value - Retention;

        // The smallest key is the leftmost node of the tree, so First() is a walk
        // down one side - O(log n) - rather than a scan.
        while (_buckets.Count > 0)
        {
            var oldest = _buckets.Keys.First();
            if (oldest >= cutoff)
            {
                break;
            }

            _buckets.Remove(oldest);
        }
    }
}
