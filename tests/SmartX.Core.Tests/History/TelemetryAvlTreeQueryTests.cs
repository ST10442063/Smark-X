using SmartX.Core.History;

namespace SmartX.Core.Tests.History;

/// <summary>
/// Covers the queries the history page runs. Every one is checked against the
/// obvious LINQ answer over a plain list, across hundreds of random windows, so
/// the tree is proven to return exactly what a full scan would - while doing far
/// less work to get there.
/// </summary>
public class TelemetryAvlTreeQueryTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Range_ReturnsTheWindowHalfOpen()
    {
        var tree = Minutes(0, 600);

        var window = tree.Range(Midnight.AddHours(2), Midnight.AddHours(4), out _);

        Assert.Equal(120, window.Count);
        Assert.Equal(Midnight.AddHours(2), window[0].TimestampUtc);
        Assert.Equal(Midnight.AddHours(4).AddMinutes(-1), window[^1].TimestampUtc);
    }

    [Fact]
    public void Range_FourteenToSixteen_MatchesTheBriefsExample()
    {
        // "Pull all moisture readings between 14:00 and 16:00 for Node A."
        var tree = Minutes(0, 24 * 60);

        var afternoon = tree.Range(Midnight.AddHours(14), Midnight.AddHours(16), out var cost);

        Assert.Equal(120, afternoon.Count);
        Assert.All(afternoon, point => Assert.InRange(point.TimestampUtc.Hour, 14, 15));

        // 1,440 readings held; the walk touched the 120 it returned plus the paths
        // down to them, nowhere near the whole day.
        Assert.Equal(1440, cost.TreeSize);
        Assert.True(cost.NodesVisited < 120 + 4 * cost.TreeHeight, $"Visited {cost.NodesVisited}.");
    }

    [Fact]
    public void Range_EmptyOrInvertedWindow_ReturnsNothing()
    {
        var tree = Minutes(0, 100);

        Assert.Empty(tree.Range(Midnight.AddMinutes(50), Midnight.AddMinutes(50), out _));
        Assert.Empty(tree.Range(Midnight.AddMinutes(60), Midnight.AddMinutes(10), out _));
        Assert.Empty(tree.Range(Midnight.AddDays(2), Midnight.AddDays(3), out _));
    }

    [Fact]
    public void RandomWindows_MatchALinearScan()
    {
        var (tree, reference) = RandomHistory(seed: 1, count: 20_000);
        var random = new Random(2);

        for (var trial = 0; trial < 500; trial++)
        {
            var (from, to) = RandomWindow(random);

            var expected = reference
                .Where(point => point.TimestampUtc >= from && point.TimestampUtc < to)
                .Select(point => point.TimestampUtc)
                .ToList();

            var actual = tree.Range(from, to, out _).Select(point => point.TimestampUtc).ToList();

            Assert.Equal(expected, actual);
            Assert.Equal(expected.Count, tree.CountBetween(from, to));
        }
    }

    [Fact]
    public void Summarise_MatchesLinqAggregates()
    {
        var (tree, reference) = RandomHistory(seed: 3, count: 20_000);
        var random = new Random(4);

        for (var trial = 0; trial < 500; trial++)
        {
            var (from, to) = RandomWindow(random);
            var inWindow = reference.Where(point => point.TimestampUtc >= from && point.TimestampUtc < to).ToList();

            var summary = tree.Summarise(from, to, out _);

            Assert.Equal(inWindow.Count, summary.Count);

            if (inWindow.Count > 0)
            {
                Assert.Equal(inWindow.Min(point => point.Value), summary.Minimum);
                Assert.Equal(inWindow.Max(point => point.Value), summary.Maximum);
                // A tolerance, not decimal places: the tree adds the same values in a
                // different order, so the last bits can differ.
                Assert.Equal(inWindow.Average(point => point.Value), summary.Mean, tolerance: 1e-9);
            }
        }
    }

    [Fact]
    public void Summarise_VisitsALogarithmicNumberOfNodes()
    {
        var tree = Minutes(0, 100_000);

        // A window holding 50,000 readings, summarised without visiting them.
        var summary = tree.Summarise(Midnight.AddMinutes(25_000), Midnight.AddMinutes(75_000), out var cost);

        Assert.Equal(50_000, summary.Count);
        Assert.True(cost.NodesVisited <= 3 * cost.TreeHeight,
            $"Visited {cost.NodesVisited} nodes for a tree {cost.TreeHeight} high.");
    }

    [Fact]
    public void RankAndSelect_AreInverse()
    {
        var (tree, _) = RandomHistory(seed: 5, count: 5000);

        for (var index = 0; index < tree.Count; index += 37)
        {
            var point = tree.Select(index);

            // Rank counts strictly-older readings, so for duplicates it lands on
            // the first reading with that timestamp, at or before this index.
            Assert.True(tree.Rank(point.TimestampUtc) <= index);
            Assert.True(tree.Rank(point.TimestampUtc.AddTicks(1)) > index);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Select(tree.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Select(-1));
    }

    [Fact]
    public void Page_WalksTheWindowWithoutGapsOrRepeats()
    {
        var (tree, reference) = RandomHistory(seed: 6, count: 3000);
        var from = Midnight.AddHours(30);
        var to = Midnight.AddHours(90);
        var expected = reference
            .Where(point => point.TimestampUtc >= from && point.TimestampUtc < to)
            .Select(point => point.TimestampUtc)
            .ToList();

        var paged = new List<DateTimeOffset>();
        for (var skip = 0; ; skip += 64)
        {
            var page = tree.Page(from, to, skip, 64, newestFirst: false, out var total, out _);
            Assert.Equal(expected.Count, total);

            if (page.Count == 0)
            {
                break;
            }

            paged.AddRange(page.Select(point => point.TimestampUtc));
        }

        Assert.Equal(expected, paged);
    }

    [Fact]
    public void Page_NewestFirst_IsTheSameWindowReversed()
    {
        var tree = Minutes(0, 600);
        var from = Midnight.AddMinutes(100);
        var to = Midnight.AddMinutes(400);

        var first = tree.Page(from, to, 0, 25, newestFirst: true, out var total, out _);
        var second = tree.Page(from, to, 25, 25, newestFirst: true, out _, out _);

        Assert.Equal(300, total);
        Assert.Equal(Midnight.AddMinutes(399), first[0].TimestampUtc);
        Assert.Equal(Midnight.AddMinutes(375), first[^1].TimestampUtc);
        Assert.Equal(Midnight.AddMinutes(374), second[0].TimestampUtc);
    }

    [Fact]
    public void Page_DeepPage_CostsTheSameAsTheFirst()
    {
        var tree = Minutes(0, 100_000);
        var from = Midnight;
        var to = Midnight.AddMinutes(100_000);

        tree.Page(from, to, 0, 50, newestFirst: false, out _, out var firstCost);
        tree.Page(from, to, 90_000, 50, newestFirst: false, out _, out var deepCost);

        // Rank-based seeking: page 1,800 is not reached by walking pages 1 to 1,799.
        Assert.True(deepCost.NodesVisited < 50 + 6 * deepCost.TreeHeight, $"Deep page visited {deepCost.NodesVisited}.");
        Assert.InRange(deepCost.NodesVisited, 1, firstCost.NodesVisited * 3);
    }

    [Fact]
    public void Page_PastTheEnd_IsEmpty()
    {
        var tree = Minutes(0, 10);

        var page = tree.Page(Midnight, Midnight.AddHours(1), 50, 10, newestFirst: false, out var total, out _);

        Assert.Equal(10, total);
        Assert.Empty(page);
    }

    [Fact]
    public void FloorAndCeiling_FindTheNearestReadings()
    {
        var tree = new TelemetryAvlTree();
        foreach (var minute in new[] { 0, 10, 20, 30 })
        {
            tree.Insert(Midnight.AddMinutes(minute), minute);
        }

        Assert.Equal(10d, tree.Floor(Midnight.AddMinutes(15))?.Value);
        Assert.Equal(20d, tree.Ceiling(Midnight.AddMinutes(15))?.Value);
        Assert.Equal(20d, tree.Floor(Midnight.AddMinutes(20))?.Value);
        Assert.Null(tree.Floor(Midnight.AddMinutes(-1)));
        Assert.Null(tree.Ceiling(Midnight.AddMinutes(31)));
    }

    private static TelemetryAvlTree Minutes(int start, int count)
    {
        var tree = new TelemetryAvlTree();

        for (var minute = start; minute < start + count; minute++)
        {
            tree.Insert(Midnight.AddMinutes(minute), minute % 97);
        }

        return tree;
    }

    /// <summary>Random readings over a week, in random order, plus the same readings as a plain sorted list.</summary>
    private static (TelemetryAvlTree Tree, List<HistoryPoint> Reference) RandomHistory(int seed, int count)
    {
        var random = new Random(seed);
        var tree = new TelemetryAvlTree();
        var reference = new List<HistoryPoint>(count);

        for (var i = 0; i < count; i++)
        {
            // Whole seconds, so plenty of readings share a timestamp.
            var point = new HistoryPoint(Midnight.AddSeconds(random.Next(0, 7 * 86_400 / 20) * 20), Math.Round(random.NextDouble() * 200 - 50, 3));
            tree.Insert(point.TimestampUtc, point.Value);
            reference.Add(point);
        }

        // A stable sort keeps arrival order among equal timestamps, as the tree does.
        return (tree, reference.OrderBy(point => point.TimestampUtc).ToList());
    }

    private static (DateTimeOffset From, DateTimeOffset To) RandomWindow(Random random)
    {
        var from = Midnight.AddSeconds(random.Next(-3600, 7 * 86_400));
        return (from, from.AddSeconds(random.Next(0, 2 * 86_400)));
    }
}
