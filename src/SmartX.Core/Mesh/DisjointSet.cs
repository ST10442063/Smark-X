namespace SmartX.Core.Mesh;

/// <summary>
/// Union-find: a forest of sets that answers "are these two in the same set?" and
/// merges two sets, both in effectively constant time.
/// <para>
/// Each set is a tree whose root names the set. <see cref="Find"/> walks to the
/// root and compresses the path as it goes, pointing each node it passes at its
/// grandparent, so the next walk is shorter. <see cref="Union"/> hangs the
/// shallower tree under the deeper one (union by rank), so trees stay flat. With
/// both, a sequence of m operations costs O(m alpha(n)), where alpha is the inverse
/// Ackermann function - below 5 for any n that fits in memory.
/// </para>
/// <para>
/// Kruskal's algorithm uses it to reject a link whose ends are already connected,
/// which is exactly the test for "would this link close a cycle?".
/// </para>
/// </summary>
/// <typeparam name="T">The elements.</typeparam>
public sealed class DisjointSet<T> where T : notnull
{
    private readonly Dictionary<T, T> _parent = [];
    private readonly Dictionary<T, int> _rank = [];

    /// <summary>Distinct sets currently held.</summary>
    public int SetCount { get; private set; }

    /// <summary>Adds <paramref name="item"/> as a set of one. Returns false if it is already held.</summary>
    public bool MakeSet(T item)
    {
        if (!_parent.TryAdd(item, item))
        {
            return false;
        }

        _rank[item] = 0;
        SetCount++;
        return true;
    }

    /// <summary>The representative of the set holding <paramref name="item"/>.</summary>
    /// <exception cref="KeyNotFoundException">The item was never added.</exception>
    public T Find(T item)
    {
        var current = item;

        while (!EqualityComparer<T>.Default.Equals(_parent[current], current))
        {
            // Path halving: skip every other node on the way up.
            var grandparent = _parent[_parent[current]];
            _parent[current] = grandparent;
            current = grandparent;
        }

        return current;
    }

    /// <summary>Whether two items are in the same set.</summary>
    public bool Connected(T a, T b) => EqualityComparer<T>.Default.Equals(Find(a), Find(b));

    /// <summary>Merges the sets holding <paramref name="a"/> and <paramref name="b"/>.</summary>
    /// <returns>False when they were already in the same set.</returns>
    public bool Union(T a, T b)
    {
        var rootA = Find(a);
        var rootB = Find(b);

        if (EqualityComparer<T>.Default.Equals(rootA, rootB))
        {
            return false;
        }

        var rankA = _rank[rootA];
        var rankB = _rank[rootB];

        if (rankA < rankB)
        {
            _parent[rootA] = rootB;
        }
        else if (rankA > rankB)
        {
            _parent[rootB] = rootA;
        }
        else
        {
            _parent[rootB] = rootA;
            _rank[rootA] = rankA + 1;
        }

        SetCount--;
        return true;
    }
}
