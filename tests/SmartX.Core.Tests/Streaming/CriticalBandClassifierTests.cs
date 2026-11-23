using SmartX.Core.Alerts;
using SmartX.Core.Models;
using SmartX.Core.Streaming;

namespace SmartX.Core.Tests.Streaming;

/// <summary>Covers the at-the-door triage that decides which lane a packet takes.</summary>
public class CriticalBandClassifierTests
{
    private static readonly CriticalBands Bands = new();

    private static TriageResult Classify(SensorCategory category, TelemetryUnit unit, double value, double? baseline = null, bool alarm = false)
        => CriticalBandClassifier.Classify(category, unit, value, baseline, alarm, Bands);

    [Fact]
    public void MoistureBelowTwentyPercent_IsCritical()
    {
        var result = Classify(SensorCategory.Environmental, TelemetryUnit.Percent, 7d, 46d);

        Assert.Equal(DispatchLane.Priority, result.Lane);
        Assert.Equal(AlertCode.CriticalLow, result.Code);
        Assert.Equal((int)AlertSeverity.Critical, result.Severity);
        Assert.Contains("20%", result.Reason);
    }

    [Fact]
    public void OrdinaryMoisture_IsRoutine()
        => Assert.Equal(TriageResult.Routine, Classify(SensorCategory.Environmental, TelemetryUnit.Percent, 43d, 46d));

    [Fact]
    public void DrawAboveTheCeiling_IsCriticalWithNoHistory()
    {
        var result = Classify(SensorCategory.PowerConsumption, TelemetryUnit.Watt, 2800d);

        Assert.Equal(AlertCode.PowerSpike, result.Code);
    }

    [Fact]
    public void DrawFarAboveTheMetersOwnBaseline_IsCriticalBelowTheCeiling()
    {
        var result = Classify(SensorCategory.PowerConsumption, TelemetryUnit.Watt, 2100d, baseline: 800d);

        Assert.Equal(AlertCode.PowerSpike, result.Code);
        Assert.Contains("2.6x", result.Reason);
    }

    [Fact]
    public void ABusyButNormalMeter_IsRoutine()
        => Assert.False(Classify(SensorCategory.PowerConsumption, TelemetryUnit.Watt, 1100d, baseline: 820d).IsUrgent);

    [Fact]
    public void AWeakLink_IsHighButNotCritical()
    {
        var result = Classify(SensorCategory.Connectivity, TelemetryUnit.Dbm, -94d);

        Assert.Equal(AlertCode.LinkLoss, result.Code);
        Assert.Equal((int)AlertSeverity.High, result.Severity);
    }

    [Fact]
    public void ADeviceAlarmFlag_IsHonouredForAnyCategory()
        => Assert.Equal(AlertCode.DeviceAlarm, Classify(SensorCategory.Actuator, TelemetryUnit.State, 1d, alarm: true).Code);

    [Fact]
    public void AnActuatorSwitching_IsNeverAnAlertByItself()
        => Assert.False(Classify(SensorCategory.Actuator, TelemetryUnit.State, 1d, baseline: 0d).IsUrgent);

    [Theory]
    [InlineData(21d, false)]
    [InlineData(24.9d, false)]
    [InlineData(25d, true)]
    public void ALowAlert_ClearsOnlyPastTheRecoveryMargin(double value, bool recovered)
        => Assert.Equal(recovered, CriticalBandClassifier.HasRecovered(AlertCode.CriticalLow, value, 40d, Bands));

    [Fact]
    public void ASpike_ClearsOnceDrawIsBackNearNormal()
    {
        Assert.False(CriticalBandClassifier.HasRecovered(AlertCode.PowerSpike, 2400d, 900d, Bands));
        Assert.True(CriticalBandClassifier.HasRecovered(AlertCode.PowerSpike, 850d, 900d, Bands));
    }
}
