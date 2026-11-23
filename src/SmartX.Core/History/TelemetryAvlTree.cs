namespace SmartX.Core.History;

/// <summary>
/// A sensor's raw telemetry history, held in a self-balancing AVL tree keyed by
/// timestamp, so that "every moisture reading between 14:00 and 16:00" is answered
/// by walking down to 14:00 rather than reading the whole history.
/// <para>
/// <b>Why a tree and not the lists the gateway already has.</b> Part 1's jagged
/// batch store keeps readings in the order they <i>arrived</i>, and a node that
/// reconnects after an outage flushes old readings late, so arrival order is not
/// time order. Finding a time window in it means reading every row. A sorted list
/// would fix the order but make every late insert an O(n) shuffle. A binary search
/// tree keeps the readings in time order with O(log n) inserts wherever a reading
/// lands, and a range query only descends the paths that can contain the window.
/// </para>
/// <para>
/// <b>Why AVL rather than red-black.</b> Both guarantee O(log n). An AVL tree is
/// balanced more strictly - its height never exceeds about 1.44 log2(n), against
/// 2 log2(n) for red-black - at the price of a few more rotations on insert. The
/// history page is read far more often than any one sensor is written to, so the
/// shallower tree is the better trade. The Part 2 timeline uses .NET's
/// <see cref="SortedDictionary{TKey,TValue}"/>, which is red-black underneath, but
/// it exposes no range query and no way to aggregate a subtree; both are needed
/// here, which is why this tree is hand-written.
/// </para>
/// <para>
/// <b>Augmented nodes.</b> Every node also stores the size, sum, minimum and
/// maximum of its subtree, updated on the way back up from each insert and
/// rotation. That turns two questions that would otherwise cost O(k) into O(log n):
/// <see cref="Summarise"/> (count, min, max and mean of a window) and
/// <see cref="Rank"/>/<see cref="Select"/>, which let the history log jump straight
/// to page 40 without walking pages 1 to 39.
/// </para>
/// <para>
/// <b>Duplicate timestamps.</b> Two readings can carry the same timestamp. The key
/// is the timestamp plus an insertion sequence number, so both are kept, in the
/// order they arrived.
/// </para>
/// <para>
/// <b>Recursion.</b> Insert, delete and the range walks are recursive because the
/// AVL height bound keeps the recursion shallow: a million readings is a tree
/// about 28 levels deep, nowhere near the stack limit.
/// </para>
/// <para>Not thread-safe. The gateway store serialises access.</para>
/// </summary>
public sealed class TelemetryAvlTree
{
    private Node? _root;
    private long _nextSequence;

    /// <summary>Readings currently held.</summary>
    public int Count => SizeOf(_root);

    /// <summary>Height of the tree; 0 when empty, 1 for a single reading.</summary>
    public int Height => HeightOf(_root);

    /// <summary>Readings inserted over the tree's lifetime.</summary>
    public long Inserted { get; private set; }

    /// <summary>Readings removed by <see cref="TrimBefore"/>.</summary>
    public long Trimmed { get; private set; }

    /// <summary>Rotations performed to keep the tree balanced.</summary>
    public long Rotations { get; private set; }

    /// <summary>Timestamp of the oldest reading, or null while empty.</summary>
    public DateTimeOffset? OldestUtc => _root is null ? null : ToTime(Leftmost(_root).Ticks);

    /// <summary>Timestamp of the newest reading, or null while empty.</summary>
    public DateTimeOffset? NewestUtc => _root is null ? null : ToTime(Rightmost(_root).Ticks);

    /// <summary>Summary of every reading held. O(1): it is the root's own aggregate.</summary>
    public RangeSummary Overall => Aggregate(_root);

    // ---------------------------------------------------------------------
    // Insert and remove
    // ---------------------------------------------------------------------

    /// <summary>Adds a reading in its time-ordered place. O(log n) whatever order readings arrive in.</summary>
    /// <param name="timestampUtc">When the reading was taken.</param>
    /// <param name="value">The reading.</param>
    public void Insert(DateTimeOffset timestampUtc, double value)
    {
        var node = new Node(timestampUtc.UtcTicks, _nextSequence++, value);
        _root = Insert(_root, node);
        Inserted++;
    }

    /// <summary>
    /// Removes every reading older than <paramref name="cutoffUtc"/>. Each removal
    /// takes the leftmost node, which is O(log n), so trimming k readings is O(k log n).
    /// </summary>
    /// <returns>Readings removed.</returns>
    public int TrimBefore(DateTimeOffset cutoffUtc)
    {
        var cutoff = cutoffUtc.UtcTicks;
        var removed = 0;

        while (_root is not null && Leftmost(_root).Ticks < cutoff)
        {
            _root = RemoveMin(_root);
            removed++;
        }

        Trimmed += removed;
        return removed;
    }

    // ---------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every reading in the window <c>[from, to)</c>, oldest first.
    /// O(log n + k): the walk only enters a subtree whose keys can fall in the window.
    /// </summary>
    /// <param name="fromUtc">Start of the window, inclusive.</param>
    /// <param name="toUtc">End of the window, exclusive, so adjacent windows never count a reading twice.</param>
    /// <param name="cost">The work the query did.</param>
    public List<HistoryPoint> Range(DateTimeOffset fromUtc, DateTimeOffset toUtc, out QueryCost cost)
    {
        var results = new List<HistoryPoint>();
        var visited = 0;

        if (fromUtc < toUtc)
        {
            CollectRange(_root, fromUtc.UtcTicks, toUtc.UtcTicks, results, ref visited);
        }

        cost = new QueryCost(visited, Count, Height);
        return results;
    }

    /// <summary>
    /// Count, min, max and mean of the window <c>[from, to)</c> in O(log n), without
    /// visiting the readings inside it: whole subtrees that sit inside the window
    /// contribute their stored aggregate in one step.
    /// </summary>
    public RangeSummary Summarise(DateTimeOffset fromUtc, DateTimeOffset toUtc, out QueryCost cost)
    {
        var visited = 0;
        var summary = fromUtc < toUtc
            ? SummariseRange(_root, fromUtc.UtcTicks, toUtc.UtcTicks, ref visited)
            : RangeSummary.Empty;

        cost = new QueryCost(visited, Count, Height);
        return summary;
    }

    /// <summary>Readings in the window <c>[from, to)</c>, counted in O(log n) from two ranks.</summary>
    public int CountBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => fromUtc < toUtc ? Math.Max(0, Rank(toUtc) - Rank(fromUtc)) : 0;

    /// <summary>
    /// How many readings are older than <paramref name="timestampUtc"/> - its rank
    /// in time order. O(log n), using the subtree sizes.
    /// </summary>
    public int Rank(DateTimeOffset timestampUtc)
    {
        var ticks = timestampUtc.UtcTicks;
        var rank = 0;
        var node = _root;

        while (node is not null)
        {
            if (node.Ticks < ticks)
            {
                // This node and its whole left subtree are older.
                rank += SizeOf(node.Left) + 1;
                node = node.Right;
            }
            else
            {
                node = node.Left;
            }
        }

        return rank;
    }

    /// <summary>The reading at position <paramref name="index"/> in time order, 0 being the oldest. O(log n).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the tree.</exception>
    public HistoryPoint Select(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        var node = _root;
        while (node is not null)
        {
            var leftSize = SizeOf(node.Left);

            if (index < leftSize)
            {
                node = node.Left;
            }
            else if (index == leftSize)
            {
                return node.ToPoint();
            }
            else
            {
                index -= leftSize + 1;
                node = node.Right;
            }
        }

        throw new InvalidOperationException("Subtree sizes are inconsistent with the tree's shape.");
    }

    /// <summary>
    /// One page of the window <c>[from, to)</c>. The page's first reading is found by
    /// rank in O(log n) and the page is read from there, so page 40 costs the same
    /// as page 1: O(log n + pageSize).
    /// </summary>
    /// <param name="fromUtc">Start of the window, inclusive.</param>
    /// <param name="toUtc">End of the window, exclusive.</param>
    /// <param name="skip">Readings to skip from the start of the window (or from its end when <paramref name="newestFirst"/>).</param>
    /// <param name="take">Most readings to return.</param>
    /// <param name="newestFirst">Page from the newest reading backwards, the way a log is usually read.</param>
    /// <param name="total">Readings in the whole window.</param>
    /// <param name="cost">The work the query did.</param>
    public List<HistoryPoint> Page(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int skip,
        int take,
        bool newestFirst,
        out int total,
        out QueryCost cost)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);

        var visited = 0;
        var first = fromUtc < toUtc ? RankCounted(fromUtc.UtcTicks, ref visited) : 0;
        var end = fromUtc < toUtc ? RankCounted(toUtc.UtcTicks, ref visited) : 0;
        total = Math.Max(0, end - first);

        var results = new List<HistoryPoint>(Math.Min(take, Math.Max(0, total - skip)));

        if (skip < total && take > 0)
        {
            var count = Math.Min(take, total - skip);

            // Newest-first is the same slice of the time order, read backwards.
            var start = newestFirst ? end - skip - count : first + skip;
            ReadFrom(start, count, results, ref visited);

            if (newestFirst)
            {
                results.Reverse();
            }
        }

        cost = new QueryCost(visited, Count, Height);
        return results;
    }

    /// <summary>The newest reading at or before <paramref name="timestampUtc"/>, if any. O(log n).</summary>
    public HistoryPoint? Floor(DateTimeOffset timestampUtc)
    {
        var ticks = timestampUtc.UtcTicks;
        Node? best = null;
        var node = _root;

        while (node is not null)
        {
            if (node.Ticks <= ticks)
            {
                best = node;
                node = node.Right;
            }
            else
            {
                node = node.Left;
            }
        }

        return best?.ToPoint();
    }

    /// <summary>The oldest reading at or after <paramref name="timestampUtc"/>, if any. O(log n).</summary>
    public HistoryPoint? Ceiling(DateTimeOffset timestampUtc)
    {
        var ticks = timestampUtc.UtcTicks;
        Node? best = null;
        var node = _root;

        while (node is not null)
        {
            if (node.Ticks >= ticks)
            {
                best = node;
                node = node.Left;
            }
            else
            {
                node = node.Right;
            }
        }

        return best?.ToPoint();
    }

    /// <summary>Every reading, oldest first. An iterative in-order walk; used by tests and exports.</summary>
    public IEnumerable<HistoryPoint> InOrder()
    {
        var stack = new Stack<Node>();
        var node = _root;

        while (node is not null || stack.Count > 0)
        {
            while (node is not null)
            {
                stack.Push(node);
                node = node.Left;
            }

            node = stack.Pop();
            yield return node.ToPoint();
            node = node.Right;
        }
    }

    /// <summary>
    /// Checks every invariant the tree depends on: key order, stored heights, the
    /// AVL balance condition and every augmented aggregate. Used by the tests after
    /// each insert and trim, so a broken rotation is caught where it happens.
    /// </summary>
    /// <param name="problem">What is wrong, when the tree is invalid.</param>
    public bool IsValid(out string? problem)
    {
        problem = null;
        return Check(_root, long.MinValue, long.MinValue, long.MaxValue, long.MaxValue, ref problem);
    }

    // ---------------------------------------------------------------------
    // Recursive core
    // ---------------------------------------------------------------------

    private Node Insert(Node? node, Node fresh)
    {
        if (node is null)
        {
            return fresh;
        }

        if (fresh.IsBefore(node))
        {
            node.Left = Insert(node.Left, fresh);
        }
        else
        {
            node.Right = Insert(node.Right, fresh);
        }

        return Rebalance(node);
    }

    private Node? RemoveMin(Node node)
    {
        if (node.Left is null)
        {
            return node.Right;
        }

        node.Left = RemoveMin(node.Left);
        return Rebalance(node);
    }

    private static void CollectRange(Node? node, long from, long to, List<HistoryPoint> results, ref int visited)
    {
        if (node is null)
        {
            return;
        }

        visited++;

        // The left subtree only holds keys at or before this one, so it can only
        // contain the window if this key is not already before the window starts.
        if (node.Ticks >= from)
        {
            CollectRange(node.Left, from, to, results, ref visited);
        }

        if (node.Ticks >= from && node.Ticks < to)
        {
            results.Add(node.ToPoint());
        }

        // Likewise the right subtree only holds keys at or after this one.
        if (node.Ticks < to)
        {
            CollectRange(node.Right, from, to, results, ref visited);
        }
    }

    /// <summary>
    /// Finds the highest node inside the window, where the search paths for the two
    /// ends split, then lets each side add whole subtrees on its way down.
    /// </summary>
    private static RangeSummary SummariseRange(Node? node, long from, long to, ref int visited)
    {
        while (node is not null)
        {
            visited++;

            if (node.Ticks < from)
            {
                node = node.Right;
            }
            else if (node.Ticks >= to)
            {
                node = node.Left;
            }
            else
            {
                var left = SummariseAtOrAfter(node.Left, from, ref visited);
                var right = SummariseBefore(node.Right, to, ref visited);
                return RangeSummary.Combine(RangeSummary.Combine(left, node.Self), right);
            }
        }

        return RangeSummary.Empty;
    }

    /// <summary>Summary of the keys in this subtree at or after <paramref name="from"/>: one root-to-leaf path.</summary>
    private static RangeSummary SummariseAtOrAfter(Node? node, long from, ref int visited)
    {
        if (node is null)
        {
            return RangeSummary.Empty;
        }

        visited++;

        if (node.Ticks >= from)
        {
            // Everything to the right is at or after this node, so it is all in -
            // the stored aggregate covers it without visiting it.
            var left = SummariseAtOrAfter(node.Left, from, ref visited);
            return RangeSummary.Combine(RangeSummary.Combine(left, node.Self), Aggregate(node.Right));
        }

        return SummariseAtOrAfter(node.Right, from, ref visited);
    }

    /// <summary>Summary of the keys in this subtree before <paramref name="to"/>: one root-to-leaf path.</summary>
    private static RangeSummary SummariseBefore(Node? node, long to, ref int visited)
    {
        if (node is null)
        {
            return RangeSummary.Empty;
        }

        visited++;

        if (node.Ticks < to)
        {
            var right = SummariseBefore(node.Right, to, ref visited);
            return RangeSummary.Combine(RangeSummary.Combine(Aggregate(node.Left), node.Self), right);
        }

        return SummariseBefore(node.Left, to, ref visited);
    }

    private int RankCounted(long ticks, ref int visited)
    {
        var rank = 0;
        var node = _root;

        while (node is not null)
        {
            visited++;

            if (node.Ticks < ticks)
            {
                rank += SizeOf(node.Left) + 1;
                node = node.Right;
            }
            else
            {
                node = node.Left;
            }
        }

        return rank;
    }

    /// <summary>
    /// Reads <paramref name="count"/> readings in time order starting at rank
    /// <paramref name="start"/>. Descends by subtree size to the first one, keeping
    /// the ancestors still to come on a stack, then continues as an in-order walk.
    /// </summary>
    private void ReadFrom(int start, int count, List<HistoryPoint> results, ref int visited)
    {
        var stack = new Stack<Node>();
        var node = _root;
        var index = start;

        while (node is not null)
        {
            visited++;
            var leftSize = SizeOf(node.Left);

            if (index < leftSize)
            {
                stack.Push(node);
                node = node.Left;
            }
            else if (index == leftSize)
            {
                stack.Push(node);
                break;
            }
            else
            {
                index -= leftSize + 1;
                node = node.Right;
            }
        }

        while (stack.Count > 0 && results.Count < count)
        {
            var next = stack.Pop();
            results.Add(next.ToPoint());

            for (var child = next.Right; child is not null; child = child.Left)
            {
                visited++;
                stack.Push(child);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Balancing
    // ---------------------------------------------------------------------

    private Node Rebalance(Node node)
    {
        node.Refresh();
        var balance = BalanceOf(node);

        if (balance > 1)
        {
            // Left-heavy. A left-right shape needs its child straightened first.
            if (BalanceOf(node.Left!) < 0)
            {
                node.Left = RotateLeft(node.Left!);
            }

            return RotateRight(node);
        }

        if (balance < -1)
        {
            if (BalanceOf(node.Right!) > 0)
            {
                node.Right = RotateRight(node.Right!);
            }

            return RotateLeft(node);
        }

        return node;
    }

    private Node RotateRight(Node node)
    {
        var pivot = node.Left!;
        node.Left = pivot.Right;
        pivot.Right = node;

        // The demoted node first, because the promoted one's aggregates include it.
        node.Refresh();
        pivot.Refresh();
        Rotations++;
        return pivot;
    }

    private Node RotateLeft(Node node)
    {
        var pivot = node.Right!;
        node.Right = pivot.Left;
        pivot.Left = node;

        node.Refresh();
        pivot.Refresh();
        Rotations++;
        return pivot;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static bool Check(Node? node, long lowTicks, long lowSequence, long highTicks, long highSequence, ref string? problem)
    {
        if (node is null)
        {
            return true;
        }

        var afterLow = node.Ticks > lowTicks || (node.Ticks == lowTicks && node.Sequence > lowSequence);
        var beforeHigh = node.Ticks < highTicks || (node.Ticks == highTicks && node.Sequence < highSequence);
        if (!afterLow || !beforeHigh)
        {
            problem = $"Key ({node.Ticks}, {node.Sequence}) is out of order.";
            return false;
        }

        if (!Check(node.Left, lowTicks, lowSequence, node.Ticks, node.Sequence, ref problem)
            || !Check(node.Right, node.Ticks, node.Sequence, highTicks, highSequence, ref problem))
        {
            return false;
        }

        var expectedHeight = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));
        if (node.Height != expectedHeight)
        {
            problem = $"Node ({node.Ticks}) stores height {node.Height} but is {expectedHeight} high.";
            return false;
        }

        if (Math.Abs(BalanceOf(node)) > 1)
        {
            problem = $"Node ({node.Ticks}) has balance {BalanceOf(node)}, outside -1..1.";
            return false;
        }

        var expected = RangeSummary.Combine(RangeSummary.Combine(Aggregate(node.Left), node.Self), Aggregate(node.Right));
        if (node.Size != expected.Count
            || node.Minimum != expected.Minimum
            || node.Maximum != expected.Maximum
            || Math.Abs(node.Sum - expected.Sum) > 1e-6 * Math.Max(1d, Math.Abs(expected.Sum)))
        {
            problem = $"Node ({node.Ticks}) has stale subtree aggregates.";
            return false;
        }

        return true;
    }

    private static Node Leftmost(Node node)
    {
        while (node.Left is not null)
        {
            node = node.Left;
        }

        return node;
    }

    private static Node Rightmost(Node node)
    {
        while (node.Right is not null)
        {
            node = node.Right;
        }

        return node;
    }

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    private static int SizeOf(Node? node) => node?.Size ?? 0;

    private static int BalanceOf(Node node) => HeightOf(node.Left) - HeightOf(node.Right);

    private static RangeSummary Aggregate(Node? node)
        => node is null ? RangeSummary.Empty : new RangeSummary(node.Size, node.Minimum, node.Maximum, node.Sum);

    private static DateTimeOffset ToTime(long ticks) => new(ticks, TimeSpan.Zero);

    /// <summary>A reading plus the aggregates of the subtree it roots.</summary>
    private sealed class Node(long ticks, long sequence, double value)
    {
        public long Ticks { get; } = ticks;

        public long Sequence { get; } = sequence;

        public double Value { get; } = value;

        public Node? Left { get; set; }

        public Node? Right { get; set; }

        public int Height { get; private set; } = 1;

        public int Size { get; private set; } = 1;

        public double Sum { get; private set; } = value;

        public double Minimum { get; private set; } = value;

        public double Maximum { get; private set; } = value;

        /// <summary>This node's own reading as a one-reading summary.</summary>
        public RangeSummary Self => new(1, Value, Value, Value);

        /// <summary>Orders by timestamp, then by arrival, so equal timestamps are all kept.</summary>
        public bool IsBefore(Node other)
            => Ticks < other.Ticks || (Ticks == other.Ticks && Sequence < other.Sequence);

        /// <summary>Recomputes height and aggregates from the children. O(1).</summary>
        public void Refresh()
        {
            Height = 1 + Math.Max(Left?.Height ?? 0, Right?.Height ?? 0);
            Size = 1 + (Left?.Size ?? 0) + (Right?.Size ?? 0);
            Sum = Value + (Left?.Sum ?? 0d) + (Right?.Sum ?? 0d);
            Minimum = Math.Min(Value, Math.Min(Left?.Minimum ?? double.MaxValue, Right?.Minimum ?? double.MaxValue));
            Maximum = Math.Max(Value, Math.Max(Left?.Maximum ?? double.MinValue, Right?.Maximum ?? double.MinValue));
        }

        public HistoryPoint ToPoint() => new(new DateTimeOffset(Ticks, TimeSpan.Zero), Value);
    }
}
