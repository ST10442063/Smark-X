using SmartX.Core.Timeline;

namespace SmartX.Core.Tests.Timeline;

/// <summary>Covers the newest-first event history.</summary>
public class StreamEventLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static StreamEvent Write(StreamEventLog log, DateTimeOffset at, string title, StreamEventKind kind = StreamEventKind.CommandIssued)
        => log.Append(kind, StreamEventLevel.Info, at, null, title, string.Empty);

    [Fact]
    public void Latest_IsNewestFirstWhateverOrderEventsWereWritten()
    {
        var log = new StreamEventLog();
        Write(log, Noon.AddMinutes(2), "two");
        Write(log, Noon, "zero");
        Write(log, Noon.AddMinutes(3), "three");
        Write(log, Noon.AddMinutes(1), "one");

        Assert.Equal(["three", "two", "one", "zero"], log.Latest(10).Select(entry => entry.Title));
    }

    [Fact]
    public void EventsWithTheSameTimestamp_AreAllKept()
    {
        var log = new StreamEventLog();
        for (var valve = 1; valve <= 6; valve++)
        {
            Write(log, Noon, $"valve {valve}");
        }

        Assert.Equal(6, log.Count);

        // Within one instant, the later write is treated as the newer event.
        Assert.Equal("valve 6", log.Latest(1)[0].Title);
    }

    [Fact]
    public void PastCapacity_TheOldestWrittenAreEvicted()
    {
        var log = new StreamEventLog(capacity: 3);
        for (var minute = 0; minute < 5; minute++)
        {
            Write(log, Noon.AddMinutes(minute), $"m{minute}");
        }

        Assert.Equal(3, log.Count);
        Assert.Equal(5, log.TotalWritten);
        Assert.Equal(["m4", "m3", "m2"], log.Latest(10).Select(entry => entry.Title));
    }

    [Fact]
    public void Between_ReturnsOnlyTheRangeNewestFirst()
    {
        var log = new StreamEventLog();
        for (var minute = 0; minute < 10; minute++)
        {
            Write(log, Noon.AddMinutes(minute), $"m{minute}");
        }

        var range = log.Between(Noon.AddMinutes(3), Noon.AddMinutes(6), take: 100);

        Assert.Equal(["m6", "m5", "m4", "m3"], range.Select(entry => entry.Title));
    }

    [Fact]
    public void AFilter_IsAppliedBeforeTake()
    {
        var log = new StreamEventLog();
        Write(log, Noon, "alert", StreamEventKind.AlertRaised);
        Write(log, Noon.AddMinutes(1), "command");
        Write(log, Noon.AddMinutes(2), "command");
        Write(log, Noon.AddMinutes(3), "alert again", StreamEventKind.AlertRaised);

        var alerts = log.Latest(5, entry => entry.Kind == StreamEventKind.AlertRaised);

        Assert.Equal(["alert again", "alert"], alerts.Select(entry => entry.Title));
    }
}
