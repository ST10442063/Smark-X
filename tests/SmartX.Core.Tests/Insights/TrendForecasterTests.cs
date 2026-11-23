using SmartX.Core.Insights;

namespace SmartX.Core.Tests.Insights;

/// <summary>Covers the least-squares projection used to warn before a threshold is crossed.</summary>
public class TrendForecasterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static List<(DateTimeOffset, double)> Line(double start, double perMinute, int minutes)
        => [.. Enumerable.Range(0, minutes).Select(minute => (Start.AddMinutes(minute), start + (perMinute * minute)))];

    [Fact]
    public void AnExactLine_IsRecoveredExactly()
    {
        var fit = TrendForecaster.Fit(Line(46d, -0.5d, 20))!.Value;

        Assert.Equal(-0.5d, fit.SlopePerMinute, precision: 9);
        Assert.Equal(36.5d, fit.Current, precision: 9);
        Assert.Equal(1d, fit.RSquared, precision: 9);
    }

    [Fact]
    public void TheCrossingTime_IsProjectedFromTheNewestPoint()
    {
        // At 36.5% and falling half a percent a minute, 20% is 33 minutes away.
        var fit = TrendForecaster.Fit(Line(46d, -0.5d, 20))!.Value;

        Assert.Equal(33d, fit.TimeUntil(20d)!.Value.TotalMinutes, precision: 6);
    }

    [Fact]
    public void ALineHeadingAway_NeverCrosses()
    {
        var fit = TrendForecaster.Fit(Line(30d, 0.5d, 20))!.Value;

        Assert.Null(fit.TimeUntil(20d));
    }

    [Fact]
    public void Noise_FitsPoorly()
    {
        var random = new Random(7312);
        var noise = Enumerable.Range(0, 60)
            .Select(minute => (Start.AddMinutes(minute), 45d + ((random.NextDouble() - 0.5d) * 10d)))
            .ToList();

        Assert.True(TrendForecaster.Fit(noise)!.Value.RSquared < 0.3d);
    }

    [Fact]
    public void TooFewPoints_GiveNoForecast()
    {
        Assert.Null(TrendForecaster.Fit(Line(46d, -1d, 2)));
        Assert.Null(TrendForecaster.Fit([(Start, 1d), (Start, 2d), (Start, 3d)]));
    }
}
