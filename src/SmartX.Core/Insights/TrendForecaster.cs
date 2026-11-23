namespace SmartX.Core.Insights;

/// <summary>A straight line fitted through recent readings.</summary>
/// <param name="SlopePerMinute">Change per minute; negative when falling.</param>
/// <param name="Current">The fitted value at the newest point.</param>
/// <param name="RSquared">How well the line fits, 0 to 1. Low means the signal is noise, not a trend.</param>
/// <param name="Points">Points the line was fitted through.</param>
/// <param name="Span">Time between the first and last point.</param>
public readonly record struct TrendForecast(
    double SlopePerMinute,
    double Current,
    double RSquared,
    int Points,
    TimeSpan Span)
{
    /// <summary>
    /// How long until the line crosses <paramref name="threshold"/>, or null when
    /// it is heading away from it, is flat, or has already crossed.
    /// </summary>
    public TimeSpan? TimeUntil(double threshold)
    {
        if (Math.Abs(SlopePerMinute) < 1e-9)
        {
            return null;
        }

        var minutes = (threshold - Current) / SlopePerMinute;
        return minutes > 0d ? TimeSpan.FromMinutes(minutes) : null;
    }
}

/// <summary>
/// Projects where a reading is heading, so the dashboard can warn about a
/// threshold before it is crossed rather than after.
/// <para>
/// Ordinary least squares over the recent timeline: the slope that minimises the
/// squared error between the line and the readings, computed in one pass. The R²
/// of the fit comes with it, and callers ignore forecasts that fit poorly - a
/// noisy probe should not be reported as "falling" because its last two readings
/// happened to go down.
/// </para>
/// </summary>
public static class TrendForecaster
{
    /// <summary>Fits a line through timestamped values. Needs at least three points spanning some time.</summary>
    public static TrendForecast? Fit(IReadOnlyList<(DateTimeOffset TimestampUtc, double Value)> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 3)
        {
            return null;
        }

        var origin = points[0].TimestampUtc;
        double sumX = 0, sumY = 0;

        foreach (var (timestamp, value) in points)
        {
            sumX += (timestamp - origin).TotalMinutes;
            sumY += value;
        }

        var meanX = sumX / points.Count;
        var meanY = sumY / points.Count;

        double covariance = 0, varianceX = 0, varianceY = 0;
        foreach (var (timestamp, value) in points)
        {
            var dx = (timestamp - origin).TotalMinutes - meanX;
            var dy = value - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        if (varianceX <= double.Epsilon)
        {
            return null;
        }

        var slope = covariance / varianceX;
        var lastX = (points[^1].TimestampUtc - origin).TotalMinutes;
        var current = meanY + (slope * (lastX - meanX));

        // A perfectly flat series fits a flat line perfectly.
        var rSquared = varianceY <= double.Epsilon
            ? 1d
            : (covariance * covariance) / (varianceX * varianceY);

        return new TrendForecast(slope, current, rSquared, points.Count, points[^1].TimestampUtc - origin);
    }
}
