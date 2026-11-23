namespace SmartX.Core.Mesh;

/// <summary>
/// A binary min-heap of keys ordered by a priority, with a position index so a
/// key's priority can be lowered in place.
/// <para>
/// <b>Why not <see cref="PriorityQueue{TElement,TPriority}"/>.</b> Prim's algorithm
/// repeatedly discovers a cheaper link to a node that is already waiting in the
/// queue. .NET's priority queue cannot find an element to update it, so the usual
/// workaround is to enqueue a duplicate and skip stale entries when they surface.
/// That works, but the queue grows to O(E) entries instead of O(V). This heap keeps
/// a dictionary from each key to its slot in the array, so
/// <see cref="TryDecrease"/> finds the key in O(1) and sifts it up in O(log V),
/// and the heap never holds more than one entry per node.
/// </para>
/// <para>
/// <b>Layout.</b> The heap is a complete binary tree stored in an array: the
/// children of slot <c>i</c> are slots <c>2i + 1</c> and <c>2i + 2</c>, and its
/// parent is <c>(i - 1) / 2</c>. No pointers, and the tree is always as shallow as
/// possible, so every operation is bounded by log2 of the count.
/// </para>
/// <para>
/// Ties on priority are broken by the key's own ordering, so the same graph
/// always produces the same tree - important when an operator compares two runs.
/// </para>
/// </summary>
/// <typeparam name="TKey">The keys; each may be in the heap at most once.</typeparam>
public sealed class IndexedMinHeap<TKey> where TKey : notnull, IComparable<TKey>
{
    private readonly List<(TKey Key, double Priority)> _items = [];
    private readonly Dictionary<TKey, int> _position = [];

    /// <summary>Keys waiting in the heap.</summary>
    public int Count => _items.Count;

    /// <summary>Swaps performed while sifting, across the heap's lifetime.</summary>
    public long Swaps { get; private set; }

    /// <summary>Whether <paramref name="key"/> is in the heap. O(1).</summary>
    public bool Contains(TKey key) => _position.ContainsKey(key);

    /// <summary>The priority <paramref name="key"/> is waiting at. O(1).</summary>
    /// <exception cref="KeyNotFoundException">The key is not in the heap.</exception>
    public double PriorityOf(TKey key) => _items[_position[key]].Priority;

    /// <summary>Adds a key. O(log n).</summary>
    /// <exception cref="InvalidOperationException">The key is already in the heap.</exception>
    public void Push(TKey key, double priority)
    {
        if (_position.ContainsKey(key))
        {
            throw new InvalidOperationException($"{key} is already in the heap; use TryDecrease to change its priority.");
        }

        _items.Add((key, priority));
        _position[key] = _items.Count - 1;
        SiftUp(_items.Count - 1);
    }

    /// <summary>
    /// Lowers a waiting key's priority, or adds the key if it is not waiting.
    /// O(log n). A higher priority is ignored, because a min-heap only ever needs
    /// to hear about a cheaper way to reach a key.
    /// </summary>
    /// <returns>True when the key was added or its priority lowered.</returns>
    public bool TryDecrease(TKey key, double priority)
    {
        if (!_position.TryGetValue(key, out var slot))
        {
            Push(key, priority);
            return true;
        }

        if (priority >= _items[slot].Priority)
        {
            return false;
        }

        _items[slot] = (key, priority);
        SiftUp(slot);
        return true;
    }

    /// <summary>The lowest-priority key without removing it.</summary>
    public bool TryPeek(out TKey key, out double priority)
    {
        if (_items.Count == 0)
        {
            key = default!;
            priority = 0;
            return false;
        }

        (key, priority) = _items[0];
        return true;
    }

    /// <summary>Removes and returns the lowest-priority key. O(log n).</summary>
    /// <exception cref="InvalidOperationException">The heap is empty.</exception>
    public (TKey Key, double Priority) Pop()
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException("The heap is empty.");
        }

        var top = _items[0];
        var last = _items.Count - 1;

        // Move the last leaf to the root, shrink, and let it sink to its level.
        Place(0, _items[last]);
        _items.RemoveAt(last);
        _position.Remove(top.Key);

        if (_items.Count > 0)
        {
            SiftDown(0);
        }

        return top;
    }

    /// <summary>Checks the heap property and the position index; used by the tests.</summary>
    public bool IsValid()
    {
        for (var slot = 0; slot < _items.Count; slot++)
        {
            if (_position[_items[slot].Key] != slot)
            {
                return false;
            }

            if (slot > 0 && Less(slot, (slot - 1) / 2))
            {
                return false;
            }
        }

        return _position.Count == _items.Count;
    }

    private void SiftUp(int slot)
    {
        while (slot > 0)
        {
            var parent = (slot - 1) / 2;
            if (!Less(slot, parent))
            {
                return;
            }

            Swap(slot, parent);
            slot = parent;
        }
    }

    private void SiftDown(int slot)
    {
        while (true)
        {
            var left = (2 * slot) + 1;
            if (left >= _items.Count)
            {
                return;
            }

            var right = left + 1;
            var smaller = right < _items.Count && Less(right, left) ? right : left;

            if (!Less(smaller, slot))
            {
                return;
            }

            Swap(slot, smaller);
            slot = smaller;
        }
    }

    private bool Less(int a, int b)
    {
        var (keyA, priorityA) = _items[a];
        var (keyB, priorityB) = _items[b];
        return priorityA < priorityB || (priorityA == priorityB && keyA.CompareTo(keyB) < 0);
    }

    private void Swap(int a, int b)
    {
        var itemA = _items[a];
        Place(a, _items[b]);
        Place(b, itemA);
        Swaps++;
    }

    private void Place(int slot, (TKey Key, double Priority) item)
    {
        _items[slot] = item;
        _position[item.Key] = slot;
    }
}
