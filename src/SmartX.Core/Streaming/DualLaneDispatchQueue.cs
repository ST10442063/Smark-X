using System.Diagnostics;

namespace SmartX.Core.Streaming;

/// <summary>Which lane a packet travels in.</summary>
public enum DispatchLane
{
    /// <summary>Routine telemetry, processed first-in-first-out.</summary>
    Standard = 0,

    /// <summary>Critical alerts, processed before anything in the standard lane.</summary>
    Priority = 1
}

/// <summary>What the queue told the producer when it accepted, or refused, an item.</summary>
/// <param name="Accepted">False when the standard lane was full and the item was shed.</param>
/// <param name="Sequence">Arrival number across both lanes.</param>
/// <param name="Lane">Lane it was placed in.</param>
/// <param name="Position">Items ahead of it in its own lane, plus one.</param>
/// <param name="StandardWaiting">Standard items already queued when it arrived.</param>
public readonly record struct DispatchTicket(
    bool Accepted,
    long Sequence,
    DispatchLane Lane,
    int Position,
    int StandardWaiting);

/// <summary>An item as it leaves the queue, with how long it waited and who it overtook.</summary>
/// <param name="Item">The item.</param>
/// <param name="Lane">Lane it travelled in.</param>
/// <param name="Sequence">Arrival number across both lanes.</param>
/// <param name="Severity">Priority-lane severity, lower is more urgent. Zero in the standard lane.</param>
/// <param name="Waited">Time from enqueue to dequeue.</param>
/// <param name="Overtook">
/// Standard items that arrived before this one and were still waiting when it left -
/// the packets a critical alert bypassed.
/// </param>
public readonly record struct Dispatched<T>(
    T Item,
    DispatchLane Lane,
    long Sequence,
    int Severity,
    TimeSpan Waited,
    int Overtook);

/// <summary>A point-in-time view of the queue for the dashboard.</summary>
public readonly record struct DispatchMetrics(
    int StandardDepth,
    int PriorityDepth,
    int StandardCapacity,
    int StandardHighWater,
    long StandardProcessed,
    long PriorityProcessed,
    long StandardShed,
    long Bypasses,
    long PacketsOvertaken,
    double StandardMeanWaitMs,
    double PriorityMeanWaitMs,
    double? LastPriorityWaitMs,
    int? LastPriorityOvertook);

/// <summary>
/// The gateway's two-lane intake: a <see cref="Queue{T}"/> for routine telemetry
/// and a <see cref="PriorityQueue{TElement,TPriority}"/> for critical alerts that
/// must not wait behind it.
/// <para>
/// <b>Why two lanes.</b> Ordinary telemetry is processed strictly in arrival
/// order, and a FIFO queue is the structure that guarantees it: O(1) enqueue at
/// the tail, O(1) dequeue from the head. But under load that order is the
/// problem. If a thousand routine readings are queued and a hydroponic bay's
/// moisture crashes, a single queue would make that alert wait its turn behind
/// all thousand. The consumer therefore always looks at the priority lane first,
/// and only takes from the standard lane when the priority lane is empty.
/// </para>
/// <para>
/// <b>Why the priority is a pair.</b> <see cref="PriorityQueue{TElement,TPriority}"/>
/// is a binary min-heap, and a heap is not stable: two alerts of equal severity
/// can come out in either order. Ordering critical alerts for the same incident
/// out of sequence would make a power spike appear to end before it began. The
/// priority is <c>(severity, arrival sequence)</c>, compared in that order, so the
/// more severe alert always leaves first and equal severities leave in the order
/// they arrived. Enqueue and dequeue stay O(log n).
/// </para>
/// <para>
/// <b>Shedding.</b> The standard lane has a capacity. Past it, routine telemetry
/// is refused with a ticket that says so, and the producer can retry. The
/// priority lane has no cap: the gateway would rather fall behind on routine
/// readings than drop an alert.
/// </para>
/// <para>
/// <b>Thread safety.</b> Unlike the other Smart-X structures, this one is
/// thread-safe on its own. It is the hand-off point where many request threads
/// produce and one background worker consumes, so it owns its lock and signals
/// the consumer with a semaphore whose count always equals the items waiting.
/// </para>
/// </summary>
/// <typeparam name="T">The queued item, typically an uplink packet.</typeparam>
public sealed class DualLaneDispatchQueue<T>
{
    private readonly object _gate = new();
    private readonly Queue<Entry> _standard = new();
    private readonly PriorityQueue<Entry, (int Severity, long Sequence)> _priority = new();
    private readonly SemaphoreSlim _available = new(0);

    private long _sequence;
    private int _standardHighWater;

    private long _standardProcessed;
    private long _priorityProcessed;
    private long _standardShed;
    private long _bypasses;
    private long _overtaken;
    private double _standardWaitMsTotal;
    private double _priorityWaitMsTotal;
    private double? _lastPriorityWaitMs;
    private int? _lastPriorityOvertook;

    /// <summary>Creates the queue.</summary>
    /// <param name="standardCapacity">Routine items held before new ones are shed.</param>
    public DualLaneDispatchQueue(int standardCapacity = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(standardCapacity, 1);
        StandardCapacity = standardCapacity;
    }

    /// <summary>Routine items held before new ones are shed.</summary>
    public int StandardCapacity { get; }

    /// <summary>Items waiting across both lanes.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _standard.Count + _priority.Count;
            }
        }
    }

    /// <summary>Queues routine telemetry at the back of the standard lane.</summary>
    public DispatchTicket Enqueue(T item)
    {
        lock (_gate)
        {
            if (_standard.Count >= StandardCapacity)
            {
                _standardShed++;
                return new DispatchTicket(false, 0, DispatchLane.Standard, 0, _standard.Count);
            }

            var entry = NewEntry(item, severity: 0);
            _standard.Enqueue(entry);
            _standardHighWater = Math.Max(_standardHighWater, _standard.Count);

            Signal();
            return new DispatchTicket(true, entry.Sequence, DispatchLane.Standard, _standard.Count, entry.StandardWaiting);
        }
    }

    /// <summary>
    /// Queues a critical item in the priority lane, ahead of every routine item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="severity">Urgency, lower first. Critical is 0.</param>
    public DispatchTicket EnqueuePriority(T item, int severity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(severity);

        lock (_gate)
        {
            var entry = NewEntry(item, severity);
            _priority.Enqueue(entry, (severity, entry.Sequence));

            Signal();

            // The heap does not expose positions, so this is the lane depth - an upper
            // bound. Behind it in the lane can only be more critical items, never routine ones.
            return new DispatchTicket(true, entry.Sequence, DispatchLane.Priority, _priority.Count, entry.StandardWaiting);
        }
    }

    /// <summary>
    /// Waits until an item is available and takes it, priority lane first.
    /// </summary>
    public async ValueTask<Dispatched<T>> DequeueAsync(CancellationToken cancellationToken = default)
    {
        await _available.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            // The semaphore count equals the items held, so having acquired it there
            // is guaranteed to be one to take.
            return Take();
        }
    }

    /// <summary>Takes an item if one is waiting, priority lane first, without blocking.</summary>
    public bool TryDequeue(out Dispatched<T> dispatched)
    {
        if (!_available.Wait(0))
        {
            dispatched = default;
            return false;
        }

        lock (_gate)
        {
            dispatched = Take();
            return true;
        }
    }

    /// <summary>Lane depths, throughput and the bypass record.</summary>
    public DispatchMetrics Snapshot()
    {
        lock (_gate)
        {
            return new DispatchMetrics(
                _standard.Count,
                _priority.Count,
                StandardCapacity,
                _standardHighWater,
                _standardProcessed,
                _priorityProcessed,
                _standardShed,
                _bypasses,
                _overtaken,
                _standardProcessed == 0 ? 0d : _standardWaitMsTotal / _standardProcessed,
                _priorityProcessed == 0 ? 0d : _priorityWaitMsTotal / _priorityProcessed,
                _lastPriorityWaitMs,
                _lastPriorityOvertook);
        }
    }

    // Callers hold the lock.
    private Entry NewEntry(T item, int severity)
        => new(item, ++_sequence, severity, Stopwatch.GetTimestamp(), _standard.Count);

    private void Signal() => _available.Release();

    // Callers hold the lock and have acquired the semaphore.
    private Dispatched<T> Take()
    {
        if (_priority.TryDequeue(out var urgent, out _))
        {
            // Every routine item waiting when this alert arrived is still waiting now:
            // nothing leaves the standard lane while the priority lane holds anything.
            // So the count taken at arrival is exactly the packets it jumped ahead of,
            // known in O(1) without walking the queue.
            var overtook = urgent.StandardWaiting;
            var waited = Stopwatch.GetElapsedTime(urgent.EnqueuedTimestamp);

            _priorityProcessed++;
            _priorityWaitMsTotal += waited.TotalMilliseconds;
            _lastPriorityWaitMs = waited.TotalMilliseconds;
            _lastPriorityOvertook = overtook;

            if (overtook > 0)
            {
                _bypasses++;
                _overtaken += overtook;
            }

            return new Dispatched<T>(urgent.Item, DispatchLane.Priority, urgent.Sequence, urgent.Severity, waited, overtook);
        }

        var routine = _standard.Dequeue();
        var routineWait = Stopwatch.GetElapsedTime(routine.EnqueuedTimestamp);

        _standardProcessed++;
        _standardWaitMsTotal += routineWait.TotalMilliseconds;

        return new Dispatched<T>(routine.Item, DispatchLane.Standard, routine.Sequence, 0, routineWait, 0);
    }

    private readonly record struct Entry(
        T Item,
        long Sequence,
        int Severity,
        long EnqueuedTimestamp,
        int StandardWaiting);
}
