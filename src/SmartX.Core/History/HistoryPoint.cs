namespace SmartX.Core.History;

/// <summary>One stored reading: when it was taken and what it read.</summary>
/// <param name="TimestampUtc">Capture time, as the device stamped it.</param>
/// <param name="Value">The reading, already converted to a double by the packet.</param>
public readonly record struct HistoryPoint(DateTimeOffset TimestampUtc, double Value);

/// <summary>
/// Count, minimum, maximum and sum over a set of readings - what a range query
/// returns when the caller wants the shape of a window rather than every reading in it.
/// </summary>
/// <param name="Count">Readings in the window.</param>
/// <param name="Minimum">Lowest reading, or 0 when the window is empty.</param>
/// <param name="Maximum">Highest reading, or 0 when the window is empty.</param>
/// <param name="Sum">Sum of the readings, kept so the mean stays exact.</param>
public readonly record struct RangeSummary(int Count, double Minimum, double Maximum, double Sum)
{
    /// <summary>The summary of no readings at all.</summary>
    public static RangeSummary Empty => new(0, 0d, 0d, 0d);

    /// <summary>Mean of the readings, or 0 when the window is empty.</summary>
    public double Mean => Count == 0 ? 0d : Sum / Count;

    /// <summary>Combines two summaries of disjoint sets of readings.</summary>
    public static RangeSummary Combine(RangeSummary left, RangeSummary right)
    {
        if (left.Count == 0)
        {
            return right;
        }

        if (right.Count == 0)
        {
            return left;
        }

        return new RangeSummary(
            left.Count + right.Count,
            Math.Min(left.Minimum, right.Minimum),
            Math.Max(left.Maximum, right.Maximum),
            left.Sum + right.Sum);
    }
}

/// <summary>
/// What a tree query cost, so the dashboard can show the work the tree saved.
/// Part 1's binary search reports its comparisons the same way.
/// </summary>
/// <param name="NodesVisited">Tree nodes the query touched.</param>
/// <param name="TreeSize">Readings held in the tree when it ran.</param>
/// <param name="TreeHeight">Height of the tree when it ran.</param>
public readonly record struct QueryCost(int NodesVisited, int TreeSize, int TreeHeight)
{
    /// <summary>
    /// Readings a scan of an unordered list would have had to read to answer the
    /// same question: all of them, because without order it cannot stop early.
    /// </summary>
    public int LinearScanEquivalent => TreeSize;
}
