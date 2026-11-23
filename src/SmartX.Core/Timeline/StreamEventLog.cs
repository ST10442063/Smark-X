namespace SmartX.Core.Timeline;

/// <summary>What happened.</summary>
public enum StreamEventKind
{
    /// <summary>A critical or high-severity packet was triaged into the priority lane and raised an alert.</summary>
    AlertRaised = 0,

    /// <summary>An alert condition returned to normal.</summary>
    AlertCleared = 1,

    /// <summary>A node missed two heartbeats.</summary>
    NodeDown = 2,

    /// <summary>A node that was down reported again.</summary>
    NodeRecovered = 3,

    /// <summary>An operator issued a manual override.</summary>
    CommandIssued = 4,

    /// <summary>A device collected and confirmed a command.</summary>
    CommandAcknowledged = 5,

    /// <summary>A device never confirmed a command.</summary>
    CommandTimedOut = 6,

    /// <summary>An operator reverted a command from the undo stack.</summary>
    CommandUndone = 7,

    /// <summary>An operator re-applied an undone command.</summary>
    CommandRedone = 8,

    /// <summary>An operator accepted a suggested action.</summary>
    SuggestionAccepted = 9,

    /// <summary>Something the gateway itself did, such as priming the pattern engine at startup.</summary>
    GatewayNote = 10
}

/// <summary>How much an event matters, for colouring and filtering.</summary>
public enum StreamEventLevel
{
    /// <summary>Routine.</summary>
    Info = 0,

    /// <summary>Needs attention soon.</summary>
    Warning = 1,

    /// <summary>Needs attention now.</summary>
    Critical = 2
}

/// <summary>One entry in the command stream history.</summary>
/// <param name="Sequence">Arrival number, unique across the log.</param>
/// <param name="TimestampUtc">When it happened.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Level">How much it matters.</param>
/// <param name="Mac">Device it concerns, in canonical form, if any.</param>
/// <param name="Title">One-line summary.</param>
/// <param name="Detail">The explanation.</param>
public sealed record StreamEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    StreamEventKind Kind,
    StreamEventLevel Level,
    string? Mac,
    string Title,
    string Detail);

/// <summary>
/// The gateway's event history, held newest-first by timestamp.
/// <para>
/// <b>Why a <see cref="SortedDictionary{TKey,TValue}"/>, and why reversed.</b>
/// Events are recorded as things happen, but their timestamps are not always in
/// order - an alert carries the device's capture time, which for a buffered packet
/// can be minutes earlier than the event logged just before it. Keyed by
/// timestamp, the tree keeps them in true time order whatever order they were
/// written in. The comparer is reversed so that the newest event is the leftmost
/// node: the history page's commonest question is "the latest hundred events",
/// and that query then reads a hundred nodes rather than walking the whole log
/// to reach its end.
/// </para>
/// <para>
/// <b>Why the key is a pair.</b> Two events can share a timestamp to the tick -
/// a bulk command logs one per valve at the same instant - and a dictionary
/// cannot hold two entries under one key. The key is <c>(timestamp, sequence)</c>,
/// which is always unique and still sorts by time first.
/// </para>
/// <para>
/// <b>Bounded.</b> A parallel <see cref="Queue{T}"/> remembers keys in the order
/// they were written, so evicting the oldest is an O(1) dequeue plus an
/// O(log n) removal rather than a search. Not thread-safe: the stream store
/// serialises access.
/// </para>
/// </summary>
public sealed class StreamEventLog
{
    private readonly SortedDictionary<EventKey, StreamEvent> _events = new(NewestFirst.Instance);
    private readonly Queue<EventKey> _writeOrder = new();
    private long _sequence;

    /// <summary>Creates a log.</summary>
    /// <param name="capacity">Events kept before the oldest written are evicted.</param>
    public StreamEventLog(int capacity = 5000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>Events kept before the oldest are evicted.</summary>
    public int Capacity { get; }

    /// <summary>Events held.</summary>
    public int Count => _events.Count;

    /// <summary>Events written since startup, including any since evicted.</summary>
    public long TotalWritten => _sequence;

    /// <summary>Records an event.</summary>
    public StreamEvent Append(
        StreamEventKind kind,
        StreamEventLevel level,
        DateTimeOffset timestampUtc,
        string? mac,
        string title,
        string detail)
    {
        var entry = new StreamEvent(++_sequence, timestampUtc, kind, level, mac, title, detail);
        var key = new EventKey(timestampUtc, entry.Sequence);

        _events.Add(key, entry);
        _writeOrder.Enqueue(key);

        while (_events.Count > Capacity && _writeOrder.TryDequeue(out var oldest))
        {
            _events.Remove(oldest);
        }

        return entry;
    }

    /// <summary>The newest events, newest first, optionally filtered.</summary>
    /// <param name="take">How many to return.</param>
    /// <param name="filter">Which to include, or null for all.</param>
    public List<StreamEvent> Latest(int take, Func<StreamEvent, bool>? filter = null)
        => Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, take, filter);

    /// <summary>
    /// Events with a timestamp in <c>[fromUtc, toUtc]</c>, newest first. The walk
    /// skips anything newer than <paramref name="toUtc"/> and stops at the first
    /// event older than <paramref name="fromUtc"/>, since everything after it is older still.
    /// </summary>
    public List<StreamEvent> Between(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        Func<StreamEvent, bool>? filter = null)
    {
        var result = new List<StreamEvent>(Math.Clamp(take, 0, 256));
        if (take <= 0)
        {
            return result;
        }

        foreach (var (key, entry) in _events)
        {
            if (key.TimestampUtc > toUtc)
            {
                continue;
            }

            if (key.TimestampUtc < fromUtc)
            {
                break;
            }

            if (filter is null || filter(entry))
            {
                result.Add(entry);
                if (result.Count == take)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Unique key: time first, then arrival number to separate identical times.</summary>
    private readonly record struct EventKey(DateTimeOffset TimestampUtc, long Sequence);

    /// <summary>Orders keys newest first, so the tree's leftmost node is the latest event.</summary>
    private sealed class NewestFirst : IComparer<EventKey>
    {
        public static readonly NewestFirst Instance = new();

        public int Compare(EventKey x, EventKey y)
        {
            var byTime = y.TimestampUtc.CompareTo(x.TimestampUtc);
            return byTime != 0 ? byTime : y.Sequence.CompareTo(x.Sequence);
        }
    }
}
