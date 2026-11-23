using SmartX.Core.History;
using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Tests.History;

/// <summary>Covers the per-sensor index of AVL trees and its retention window.</summary>
public class SensorHistoryIndexTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Append_KeepsEachSensorInItsOwnTree()
    {
        var index = new SensorHistoryIndex(TimeSpan.FromHours(48));
        var probe = Guid.NewGuid();
        var meter = Guid.NewGuid();

        index.Append(probe, Packets(probe, 0, 10, 45.5));
        index.Append(meter, Packets(meter, 0, 4, 820));

        Assert.Equal(2, index.SensorCount);
        Assert.Equal(10, index.Find(probe)!.Count);
        Assert.Equal(4, index.Find(meter)!.Count);
        Assert.Equal(14, index.TotalReadings);
        Assert.Null(index.Find(Guid.NewGuid()));
    }

    [Fact]
    public void Append_TrimsBehindTheNewestReading()
    {
        var index = new SensorHistoryIndex(TimeSpan.FromHours(2));
        var probe = Guid.NewGuid();

        index.Append(probe, Packets(probe, 0, 300, 40));

        var tree = index.Find(probe)!;
        Assert.Equal(Midnight.AddMinutes(299), tree.NewestUtc);
        Assert.Equal(Midnight.AddMinutes(179), tree.OldestUtc);
        Assert.Equal(121, tree.Count);
        Assert.True(tree.IsValid(out var problem), problem);
    }

    [Fact]
    public void Append_ReadingOlderThanTheWindow_IsDiscarded()
    {
        var index = new SensorHistoryIndex(TimeSpan.FromHours(1));
        var probe = Guid.NewGuid();
        index.Append(probe, Packets(probe, 600, 10, 40));

        var stored = index.Append(probe, Packets(probe, 0, 5, 40));

        Assert.Equal(0, stored);
        Assert.Equal(5, index.Discarded);
        Assert.Equal(10, index.Find(probe)!.Count);
    }

    [Fact]
    public void Append_LateButInWindow_LandsInTimeOrder()
    {
        var index = new SensorHistoryIndex(TimeSpan.FromHours(48));
        var probe = Guid.NewGuid();

        index.Append(probe, Packets(probe, 100, 10, 1));
        index.Append(probe, Packets(probe, 50, 10, 2));

        var times = index.Find(probe)!.InOrder().Select(point => point.TimestampUtc).ToList();
        Assert.Equal(times.Order(), times);
        Assert.Equal(Midnight.AddMinutes(50), times[0]);
    }

    private static List<ITelemetryPacket> Packets(Guid sensorId, int startMinute, int count, double value)
        => Enumerable.Range(startMinute, count)
            .Select(minute => (ITelemetryPacket)TelemetryPacket.Create(
                sensorId, "A4:CF:12:00:00:01", value, TelemetryUnit.Percent, minute, Midnight.AddMinutes(minute)))
            .ToList();
}
