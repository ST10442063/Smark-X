using System.Collections;

namespace SmartX.Core.Collections;

/// <summary>
/// A fixed-capacity circular buffer - the staging area every telemetry packet
/// lands in before it is flushed into a <see cref="List{T}"/> for querying.
/// <para>
/// <b>Why a custom collection rather than a List or a Queue.</b> The gateway
/// ingests continuously and must never grow without bound: a <c>List&lt;T&gt;</c>
/// would keep doubling its backing array until the process ran out of memory, and
/// a <c>Queue&lt;T&gt;</c> has the same problem unless something dequeues at
/// exactly the rate packets arrive, which on a bursty mesh nothing does.
/// </para>
/// <para>
/// This buffer allocates its array once and then never allocates again. Writes go
/// to a head index that wraps around, so appending is O(1) with no shifting and no
/// resizing, and when the buffer is full the oldest packet is overwritten rather
/// than the newest being dropped. On a telemetry feed that is the right trade:
/// the most recent readings are the ones the dashboard and the anomaly detector
/// care about, and a reading from four thousand packets ago has already been
/// flushed to the historical store.
/// </para>
/// <para>
/// Enumeration always yields oldest-first regardless of where the head currently
/// sits, so callers never have to reason about the wrap.
/// </para>
/// <para>
/// <b>Thread safety.</b> This type is not thread-safe. The ingestion repository
/// owns the only instances and serialises access behind its own lock.
/// </para>
/// </summary>
/// <typeparam name="T">Element type. Typically a telemetry packet or a reading.</typeparam>
public sealed class IngestionBuffer<T> : ICollection<T>, IReadOnlyCollection<T>
{
    private readonly T[] _items;
    private int _head;   // index the next write goes to
    private int _count;  // live elements, never above Capacity

    /// <summary>Creates a buffer that will hold at most <paramref name="capacity"/> elements.</summary>
    /// <param name="capacity">Maximum live elements. Must be at least one.</param>
    public IngestionBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        // Allocated once, up front. Nothing here allocates again for the life
        // of the buffer, which is the whole point.
        _items = new T[capacity];
        Capacity = capacity;
    }

    /// <summary>Maximum number of elements held at once.</summary>
    public int Capacity { get; }

    /// <summary>Elements currently held.</summary>
    public int Count => _count;

    /// <summary>Always false - the buffer is writable.</summary>
    public bool IsReadOnly => false;

    /// <summary>True when the next append will overwrite the oldest element.</summary>
    public bool IsFull => _count == Capacity;

    /// <summary>Total elements ever appended, including those since overwritten.</summary>
    public long TotalAccepted { get; private set; }

    /// <summary>
    /// How many elements have been overwritten by the wrap. A non-zero value means
    /// the consumer is not draining fast enough, which the dashboard surfaces as
    /// ingestion back-pressure.
    /// </summary>
    public long OverwrittenCount { get; private set; }

    /// <summary>
    /// Appends an element in O(1). When the buffer is full this overwrites the
    /// oldest element and increments <see cref="OverwrittenCount"/>.
    /// </summary>
    public void Add(T item)
    {
        if (IsFull)
        {
            OverwrittenCount++;
        }

        _items[_head] = item;
        _head = (_head + 1) % Capacity;

        if (_count < Capacity)
        {
            _count++;
        }

        TotalAccepted++;
    }

    /// <summary>Appends a sequence, oldest first.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var item in items)
        {
            Add(item);
        }
    }

    /// <summary>
    /// The element at <paramref name="index"/> counting from the oldest.
    /// Index 0 is the oldest live element, <c>Count - 1</c> the newest.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the live range.</exception>
    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);

            return _items[PhysicalIndex(index)];
        }
    }

    /// <summary>The most recently appended element, or default when empty.</summary>
    public T? Newest => _count == 0 ? default : _items[PhysicalIndex(_count - 1)];

    /// <summary>The oldest live element, or default when empty.</summary>
    public T? Oldest => _count == 0 ? default : _items[PhysicalIndex(0)];

    /// <summary>
    /// The most recent <paramref name="count"/> elements, oldest first. Asking for
    /// more than are held simply returns everything.
    /// </summary>
    public IEnumerable<T> TakeNewest(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var take = Math.Min(count, _count);
        for (var i = _count - take; i < _count; i++)
        {
            yield return _items[PhysicalIndex(i)];
        }
    }

    /// <summary>
    /// Copies the live elements into a <see cref="List{T}"/>, oldest first, and
    /// empties the buffer. This is the hand-off from the fixed-size ingestion
    /// staging area into the growable collection the query layer works against.
    /// </summary>
    public List<T> Drain()
    {
        var drained = ToList();
        Clear();
        return drained;
    }

    /// <summary>Copies the live elements into a new <see cref="List{T}"/>, oldest first.</summary>
    public List<T> ToList()
    {
        var list = new List<T>(_count);
        for (var i = 0; i < _count; i++)
        {
            list.Add(_items[PhysicalIndex(i)]);
        }

        return list;
    }

    /// <summary>
    /// Empties the buffer. The backing array is cleared so that reference elements
    /// do not stay reachable and pin memory the caller thinks it has released.
    /// The running counters are deliberately kept, since they describe the
    /// lifetime of the pipeline rather than its current contents.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _head = 0;
        _count = 0;
    }

    /// <inheritdoc />
    public bool Contains(T item) => this.Any(element => EqualityComparer<T>.Default.Equals(element, item));

    /// <inheritdoc />
    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);

        if (array.Length - arrayIndex < _count)
        {
            throw new ArgumentException(
                "The destination array does not have room for the buffer contents.", nameof(array));
        }

        for (var i = 0; i < _count; i++)
        {
            array[arrayIndex + i] = _items[PhysicalIndex(i)];
        }
    }

    /// <summary>
    /// Not supported. Removing an arbitrary element would break the wrap-around
    /// arithmetic that makes appending O(1); a telemetry stage is append-and-drain,
    /// never remove-in-the-middle.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public bool Remove(T item) => throw new NotSupportedException(
        "IngestionBuffer is append-and-drain. Use Drain() or Clear() instead of removing individual elements.");

    /// <summary>Enumerates the live elements, oldest first.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return _items[PhysicalIndex(i)];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Translates a logical index (0 = oldest) into its physical slot in the
    /// backing array. This single line is what hides the wrap from every caller.
    /// </summary>
    private int PhysicalIndex(int logicalIndex) => (_head - _count + logicalIndex + Capacity) % Capacity;

    /// <inheritdoc />
    public override string ToString() =>
        $"IngestionBuffer<{typeof(T).Name}> {_count}/{Capacity} " +
        $"(accepted {TotalAccepted}, overwritten {OverwrittenCount})";
}
