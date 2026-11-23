using SmartX.Core.Models;

namespace SmartX.Core.Tests.Devices;

/// <summary>Covers the device shadow: current value, baseline, relay state and heartbeat.</summary>
public class DeviceStateTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ALatePacket_IsKeptInTheTimelineButDoesNotRewindTheCurrentValue()
    {
        var probe = TestFleet.Device("A4:CF:12:00:00:01", "Probe", SensorCategory.Environmental);

        Assert.True(probe.Observe(44d, Noon));
        Assert.False(probe.Observe(12d, Noon.AddMinutes(-5)));

        Assert.Equal(44d, probe.LatestValue);
        Assert.Equal(Noon, probe.LastSeenUtc);
        Assert.Equal(2, probe.Timeline.Count);
        Assert.Equal(1, probe.Timeline.LateArrivals);
    }

    [Fact]
    public void TheBaseline_MovesATenthOfTheWayTowardsEachReading()
    {
        var meter = TestFleet.Device("A4:CF:12:00:00:02", "Meter", SensorCategory.PowerConsumption);

        meter.Observe(800d, Noon);
        meter.Observe(1800d, Noon.AddSeconds(5));

        Assert.Equal(900d, meter.Baseline!.Value, precision: 6);
    }

    [Fact]
    public void AnActuator_ReportsItsStateFromItsTelemetry()
    {
        var pump = TestFleet.Device("A4:CF:12:00:00:03", "Pump", SensorCategory.Actuator);

        pump.DesiredState = true;
        Assert.True(pump.AwaitingConfirmation);

        pump.Observe(1d, Noon);

        Assert.True(pump.ReportedState);
        Assert.False(pump.AwaitingConfirmation);
        Assert.True(pump.EffectiveState);
    }

    [Fact]
    public void Heartbeat_FollowsThePartOneRule()
    {
        var probe = TestFleet.Device("A4:CF:12:00:00:01", "Probe", SensorCategory.Environmental);

        Assert.Equal(SensorStatus.Silent, probe.EvaluateStatus(Noon));

        probe.Observe(40d, Noon);

        Assert.Equal(SensorStatus.Online, probe.EvaluateStatus(Noon.AddSeconds(30)));
        Assert.Equal(SensorStatus.Degraded, probe.EvaluateStatus(Noon.AddSeconds(90)));
        Assert.Equal(SensorStatus.Silent, probe.EvaluateStatus(Noon.AddSeconds(150)));
    }
}
