using SmartX.Core.Timeline;

namespace SmartX.Core.Tests.Timeline;

/// <summary>
/// Covers the sorted timeline. The case it exists for is the out-of-order one: a
/// late flush of buffered readings must land in the right place, and enumeration
/// must still come out in time order without anything being sorted.
/// </summary>
public class SensorTimelineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadingsInOneMinute_ShareABucket()
    {
        var timeline = new SensorTimeline();

        timeline.Add(Noon.AddSeconds(5), 40d);
        timeline.Add(Noon.AddSeconds(25), 46d);
        timeline.Add(Noon.AddSeconds(55), 43d);

        var bucket = Assert.Single(timeline.All);
        Assert.Equal(Noon, bucket.StartUtc);
        Assert.Equal(3, bucket.Count);
        Assert.Equal(40d, bucket.Minimum);
        Assert.Equal(46d, bucket.Maximum);
        Assert.Equal(43d, bucket.Mean, precision: 6);
    }

    [Fact]
    public void OutOfOrderReadings_EnumerateInTimeOrder()
    {
        var timeline = new SensorTimeline();
        int[] arrivalMinutes = [5, 1, 9, 3, 7, 2, 8, 4, 6, 0];

        foreach (var minute in arrivalMinutes)
        {
            timeline.Add(Noon.AddMinutes(minute), minute);
        }

        Assert.Equal(Enumerable.Range(0, 10).Select(minute => Noon.AddMinutes(minute)),
                     timeline.All.Select(bucket => bucket.StartUtc));
        Assert.True(timeline.LateArrivals > 0);
    }

    [Fact]
    public void BucketsPastTheRetention_AreEvicted()
    {
        var timeline = new SensorTimeline(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

        for (var minute = 0; minute < 30; minute++)
        {
            timeline.Add(Noon.AddMinutes(minute), minute);
        }

        Assert.Equal(11, timeline.Count);
        Assert.Equal(Noon.AddMinutes(19), timeline.All.First().StartUtc);
    }

    [Fact]
    public void AReadingOlderThanTheRetention_IsDiscarded()
    {
        var timeline = new SensorTimeline(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        timeline.Add(Noon, 1d);

        Assert.False(timeline.Add(Noon.AddHours(-1), 2d));
        Assert.Equal(1, timeline.Discarded);
        Assert.Single(timeline.All);
    }

    [Fact]
    public void Range_ReturnsOnlyBucketsInsideTheBounds()
    {
        var timeline = new SensorTimeline();
        for (var minute = 0; minute < 20; minute++)
        {
            timeline.Add(Noon.AddMinutes(minute), minute);
        }

        var range = timeline.Range(Noon.AddMinutes(5), Noon.AddMinutes(9));

        Assert.Equal([5d, 6d, 7d, 8d, 9d], range.Select(bucket => bucket.Mean));
        Assert.Empty(timeline.Range(Noon.AddMinutes(9), Noon.AddMinutes(5)));
    }

    [Fact]
    public void Latest_CoversTheWindowEndingAtTheNewestBucket()
    {
        var timeline = new SensorTimeline();
        for (var minute = 0; minute < 120; minute++)
        {
            timeline.Add(Noon.AddMinutes(minute), minute);
        }

        var lastHour = timeline.Latest(TimeSpan.FromMinutes(60));

        Assert.Equal(60, lastHour.Count);
        Assert.Equal(Noon.AddMinutes(119), lastHour[^1].StartUtc);
    }

    [Fact]
    public void CriticalReadings_AreCountedPerBucket()
    {
        var timeline = new SensorTimeline();
        timeline.Add(Noon, 40d);
        timeline.Add(Noon.AddSeconds(10), 7d, critical: true);

        Assert.Equal(1, Assert.Single(timeline.All).Critical);
    }
}
