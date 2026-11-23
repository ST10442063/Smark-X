using SmartX.Core.Devices;

namespace SmartX.Core.Alerts;

/// <summary>The condition an alert reports.</summary>
public enum AlertCode
{
    /// <summary>
    /// An environmental reading fell through its safety floor - the hydroponic bay
    /// whose soil moisture has dropped below 20%.
    /// </summary>
    CriticalLow = 0,

    /// <summary>A power meter jumped far above its normal draw, or past the absolute ceiling.</summary>
    PowerSpike = 1,

    /// <summary>A mesh link's signal fell below the level a hop can be trusted at.</summary>
    LinkLoss = 2,

    /// <summary>The device itself raised an alarm flag from its own on-board threshold.</summary>
    DeviceAlarm = 3
}

/// <summary>How urgent an alert is. Lower values are dispatched first.</summary>
public enum AlertSeverity
{
    /// <summary>Needs attention now; bypasses the standard queue.</summary>
    Critical = 0,

    /// <summary>Worth attention soon; also bypasses, but behind anything critical.</summary>
    High = 1
}

/// <summary>Severity and wording for each <see cref="AlertCode"/>.</summary>
public static class AlertCodes
{
    /// <summary>The severity an alert of this kind is raised at.</summary>
    public static AlertSeverity SeverityOf(AlertCode code) => code switch
    {
        AlertCode.CriticalLow or AlertCode.PowerSpike => AlertSeverity.Critical,
        _ => AlertSeverity.High
    };

    /// <summary>A short operator-facing label.</summary>
    public static string Describe(AlertCode code) => code switch
    {
        AlertCode.CriticalLow => "Critical low reading",
        AlertCode.PowerSpike => "Power spike",
        AlertCode.LinkLoss => "Mesh link loss",
        AlertCode.DeviceAlarm => "Device alarm",
        _ => code.ToString()
    };
}

/// <summary>
/// Identifies one alert condition on one device - the unit the alert set
/// deduplicates on. A moisture probe can be critically low and lose its link at
/// the same time, and those are two alerts; it cannot be critically low twice.
/// </summary>
/// <param name="Mac">The device.</param>
/// <param name="Code">The condition.</param>
public readonly record struct AlertKey(MacKey Mac, AlertCode Code)
{
    /// <summary>Severity of the condition.</summary>
    public AlertSeverity Severity => AlertCodes.SeverityOf(Code);
}
