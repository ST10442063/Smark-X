using SmartX.Core.Insights;

namespace SmartX.Core.Tests.Insights;

/// <summary>Covers the heap-based top-k selection that ranks suggestions.</summary>
public class TopKTests
{
    [Fact]
    public void TheHighestScores_AreReturnedHighestFirst()
    {
        var random = new Random(7312);
        var values = Enumerable.Range(0, 1000).Select(_ => random.NextDouble()).ToList();

        var top = TopK.Select(values, value => value, 5);

        Assert.Equal(values.OrderByDescending(value => value).Take(5), top);
    }

    [Fact]
    public void FewerItemsThanK_ReturnsThemAll()
        => Assert.Equal([3, 2, 1], TopK.Select([1, 3, 2], value => value, 10));

    [Fact]
    public void AKOfZero_ReturnsNothing()
        => Assert.Empty(TopK.Select([1, 2, 3], value => value, 0));
}
