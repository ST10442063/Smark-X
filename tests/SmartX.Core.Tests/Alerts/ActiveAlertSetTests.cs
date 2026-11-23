using SmartX.Core.Alerts;
using SmartX.Core.Devices;

namespace SmartX.Core.Tests.Alerts;

/// <summary>Covers alert deduplication: one condition, one alert, however many packets repeat it.</summary>
public class ActiveAlertSetTests
{
    private static readonly MacKey Probe = MacKey.Parse("A4:CF:12:00:00:01");
    private static readonly MacKey Meter = MacKey.Parse("A4:CF:12:00:00:02");

    [Fact]
    public void ARepeatedCondition_IsRaisedOnceAndTheRepeatsCounted()
    {
        var alerts = new ActiveAlertSet();
        var low = new AlertKey(Probe, AlertCode.CriticalLow);

        var raised = Enumerable.Range(0, 40).Count(_ => alerts.TryRaise(low));

        Assert.Equal(1, raised);
        Assert.Equal(1, alerts.Raised);
        Assert.Equal(39, alerts.DuplicatesSuppressed);
        Assert.Equal(1, alerts.Count);
    }

    [Fact]
    public void TwoConditionsOnOneDevice_AreTwoAlerts()
    {
        var alerts = new ActiveAlertSet();

        Assert.True(alerts.TryRaise(new AlertKey(Probe, AlertCode.CriticalLow)));
        Assert.True(alerts.TryRaise(new AlertKey(Probe, AlertCode.DeviceAlarm)));
        Assert.Equal(2, alerts.For(Probe).Count);
    }

    [Fact]
    public void AClearedCondition_CanBeRaisedAgain()
    {
        var alerts = new ActiveAlertSet();
        var spike = new AlertKey(Meter, AlertCode.PowerSpike);

        alerts.TryRaise(spike);
        Assert.True(alerts.Clear(spike));
        Assert.False(alerts.IsActive(spike));
        Assert.True(alerts.TryRaise(spike));
        Assert.Equal(2, alerts.Raised);
    }

    [Fact]
    public void DevicesAt_GroupsBySeverityForTheFilter()
    {
        var alerts = new ActiveAlertSet();
        alerts.TryRaise(new AlertKey(Probe, AlertCode.CriticalLow));
        alerts.TryRaise(new AlertKey(Meter, AlertCode.DeviceAlarm));

        Assert.Equal([Probe], alerts.DevicesAt(AlertSeverity.Critical));
        Assert.Equal([Meter], alerts.DevicesAt(AlertSeverity.High));
        Assert.Equal(2, alerts.DevicesWithAny().Count);
    }

    [Fact]
    public void ClearDevice_RemovesEveryConditionOnThatDeviceOnly()
    {
        var alerts = new ActiveAlertSet();
        alerts.TryRaise(new AlertKey(Probe, AlertCode.CriticalLow));
        alerts.TryRaise(new AlertKey(Probe, AlertCode.DeviceAlarm));
        alerts.TryRaise(new AlertKey(Meter, AlertCode.PowerSpike));

        Assert.Equal(2, alerts.ClearDevice(Probe));
        Assert.Equal(1, alerts.Count);
    }
}
