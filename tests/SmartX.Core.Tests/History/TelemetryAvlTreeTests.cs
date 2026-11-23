using SmartX.Core.History;

namespace SmartX.Core.Tests.History;

/// <summary>
/// Covers the shape of the AVL tree: that it stays ordered and balanced however
/// readings arrive, that its augmented aggregates stay correct through rotations,
/// and that trimming keeps all of that true.
/// </summary>
public class TelemetryAvlTreeTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyTree_HasNoReadingsAndNoHeight()
    {
        var tree = new TelemetryAvlTree();

        Assert.Equal(0, tree.Count);
        Assert.Equal(0, tree.Height);
        Assert.Null(tree.OldestUtc);
        Assert.Null(tree.NewestUtc);
        Assert.Equal(RangeSummary.Empty, tree.Overall);
        Assert.True(tree.IsValid(out _));
    }

    [Fact]
    public void AscendingInserts_StayBalanced()
    {
        // The worst case for a plain BST: in-order arrival builds a linked list.
        var tree = new TelemetryAvlTree();

        for (var minute = 0; minute < 1023; minute++)
        {
            tree.Insert(Midnight.AddMinutes(minute), minute);
            AssertValid(tree);
        }

        // 1023 = 2^10 - 1 readings fit a perfect tree of height 10.
        Assert.Equal(1023, tree.Count);
        Assert.Equal(10, tree.Height);
        Assert.True(tree.Rotations > 0);
    }

    [Fact]
    public void DescendingInserts_StayBalanced()
    {
        var tree = new TelemetryAvlTree();

        for (var minute = 999; minute >= 0; minute--)
        {
            tree.Insert(Midnight.AddMinutes(minute), minute);
        }

        AssertValid(tree);
        Assert.True(tree.Height <= MaxAvlHeight(tree.Count));
    }

    [Fact]
    public void OutOfOrderInserts_EnumerateInTimeOrder()
    {
        var tree = new TelemetryAvlTree();
        int[] arrival = [5, 1, 9, 3, 7, 2, 8, 4, 6, 0];

        foreach (var minute in arrival)
        {
            tree.Insert(Midnight.AddMinutes(minute), minute);
        }

        Assert.Equal(Enumerable.Range(0, 10).Select(minute => (double)minute),
                     tree.InOrder().Select(point => point.Value));
        Assert.Equal(Midnight, tree.OldestUtc);
        Assert.Equal(Midnight.AddMinutes(9), tree.NewestUtc);
    }

    [Fact]
    public void RandomInserts_KeepEveryInvariant()
    {
        var random = new Random(7312);
        var tree = new TelemetryAvlTree();

        for (var i = 0; i < 5000; i++)
        {
            tree.Insert(Midnight.AddSeconds(random.Next(0, 86_400)), random.NextDouble() * 100);

            // Checked after every insert so a broken rotation fails at the insert
            // that broke it, not thousands of inserts later.
            if (i % 50 == 0)
            {
                AssertValid(tree);
            }
        }

        AssertValid(tree);
        Assert.Equal(5000, tree.Count);
        Assert.True(tree.Height <= MaxAvlHeight(tree.Count), $"Height {tree.Height} exceeds the AVL bound.");
    }

    [Fact]
    public void LargeTree_StaysWithinTheAvlHeightBound()
    {
        var random = new Random(42);
        var tree = new TelemetryAvlTree();

        for (var i = 0; i < 100_000; i++)
        {
            tree.Insert(Midnight.AddMilliseconds(random.NextInt64(0, 7L * 86_400_000)), i);
        }

        AssertValid(tree);
        Assert.True(tree.Height <= MaxAvlHeight(tree.Count));

        // A plain BST fed the same data could be anything up to 100,000 deep; a
        // perfectly balanced one is 17. AVL keeps it close to the perfect case.
        Assert.InRange(tree.Height, 17, 24);
    }

    [Fact]
    public void DuplicateTimestamps_AreAllKeptInArrivalOrder()
    {
        var tree = new TelemetryAvlTree();

        tree.Insert(Midnight, 1);
        tree.Insert(Midnight, 2);
        tree.Insert(Midnight, 3);
        tree.Insert(Midnight.AddMinutes(-1), 0);

        AssertValid(tree);
        Assert.Equal([0d, 1d, 2d, 3d], tree.InOrder().Select(point => point.Value));
    }

    [Fact]
    public void Overall_IsTheRootAggregate()
    {
        var tree = new TelemetryAvlTree();
        double[] values = [12, -4, 30, 7.5, 18];

        for (var i = 0; i < values.Length; i++)
        {
            tree.Insert(Midnight.AddMinutes(i), values[i]);
        }

        var overall = tree.Overall;
        Assert.Equal(5, overall.Count);
        Assert.Equal(-4, overall.Minimum);
        Assert.Equal(30, overall.Maximum);
        Assert.Equal(values.Average(), overall.Mean, precision: 9);
    }

    [Fact]
    public void TrimBefore_RemovesOnlyOlderReadings()
    {
        var tree = new TelemetryAvlTree();

        for (var minute = 0; minute < 500; minute++)
        {
            tree.Insert(Midnight.AddMinutes(minute), minute);
        }

        var removed = tree.TrimBefore(Midnight.AddMinutes(200));

        Assert.Equal(200, removed);
        Assert.Equal(300, tree.Count);
        Assert.Equal(200, tree.Trimmed);
        Assert.Equal(Midnight.AddMinutes(200), tree.OldestUtc);
        AssertValid(tree);
    }

    [Fact]
    public void TrimBefore_PastTheNewest_EmptiesTheTree()
    {
        var tree = new TelemetryAvlTree();

        for (var minute = 0; minute < 50; minute++)
        {
            tree.Insert(Midnight.AddMinutes(minute), minute);
        }

        Assert.Equal(50, tree.TrimBefore(Midnight.AddDays(1)));
        Assert.Equal(0, tree.Count);
        Assert.True(tree.IsValid(out _));
    }

    [Fact]
    public void InterleavedInsertAndTrim_KeepEveryInvariant()
    {
        // The live pattern: readings keep arriving while retention keeps cutting
        // the oldest ones off the left edge.
        var random = new Random(9);
        var tree = new TelemetryAvlTree();

        for (var minute = 0; minute < 3000; minute++)
        {
            tree.Insert(Midnight.AddMinutes(minute - random.Next(0, 5)), minute);

            if (minute % 100 == 0)
            {
                tree.TrimBefore(Midnight.AddMinutes(minute - 600));
                AssertValid(tree);
            }
        }

        AssertValid(tree);
        Assert.True(tree.Count <= 700);
    }

    private static void AssertValid(TelemetryAvlTree tree)
        => Assert.True(tree.IsValid(out var problem), problem);

    /// <summary>The AVL height bound: h &lt; 1.4405 log2(n + 2) - 0.3277.</summary>
    private static int MaxAvlHeight(int count)
        => (int)Math.Floor(1.4405 * Math.Log2(count + 2) - 0.3277);
}
