using SmartX.Core.Alerts;
using SmartX.Core.Models;

namespace SmartX.Core.Streaming;

/// <summary>
/// The safety bands triage measures packets against. Bound from configuration,
/// so a deployment can tighten them without a rebuild.
/// </summary>
public sealed class CriticalBands
{
    /// <summary>
    /// An environmental percentage below this is critical - the hydroponics case in
    /// the brief, a bay whose soil moisture has fallen under 20%.
    /// </summary>
    public double EnvironmentalFloorPercent { get; set; } = 20d;

    /// <summary>An environmental alert clears once the reading is back above floor plus this margin.</summary>
    public double EnvironmentalRecoveryMargin { get; set; } = 5d;

    /// <summary>Instantaneous draw above this many watts is critical, whatever the meter normally reads.</summary>
    public double PowerCeilingWatts { get; set; } = 2500d;

    /// <summary>Draw more than this multiple of the meter's own baseline is also critical.</summary>
    public double PowerSpikeRatio { get; set; } = 2.5d;

    /// <summary>A link weaker than this many dBm can no longer be trusted to carry a hop.</summary>
    public double LinkFloorDbm { get; set; } = -90d;
}

/// <summary>The lane triage chose and, for the priority lane, why.</summary>
/// <param name="Lane">Standard or priority.</param>
/// <param name="Severity">Priority-lane urgency, lower first. Zero for standard packets.</param>
/// <param name="Code">The alert condition, when there is one.</param>
/// <param name="Reason">Plain-language explanation, when there is one.</param>
public readonly record struct TriageResult(DispatchLane Lane, int Severity, AlertCode? Code, string? Reason)
{
    /// <summary>A routine packet.</summary>
    public static TriageResult Routine { get; } = new(DispatchLane.Standard, 0, null, null);

    /// <summary>Whether this packet goes in the priority lane.</summary>
    public bool IsUrgent => Lane == DispatchLane.Priority;
}

/// <summary>
/// Decides, as a packet arrives and before it is queued, whether it is routine or
/// an alert that must jump the queue.
/// <para>
/// <b>Why fixed bands here, when Part 1 uses a rolling z-score.</b> The z-score
/// needs a window of history and runs after a reading is stored. Triage has to be
/// decided at the door, before the packet is queued at all, so it must be O(1).
/// It needs only the value, the device category and the device's running
/// baseline, which the device shadow keeps for exactly this. The bands are the
/// hard physical limits - a crop is in danger below 20% moisture whatever its
/// history says - and the z-score still runs afterwards and catches the subtler
/// faults that never cross a hard limit.
/// </para>
/// </summary>
public static class CriticalBandClassifier
{
    /// <summary>Classifies one reading.</summary>
    /// <param name="category">Device class.</param>
    /// <param name="unit">Unit the reading is in.</param>
    /// <param name="value">The reading.</param>
    /// <param name="baseline">The device's running baseline, or null for its first packet.</param>
    /// <param name="deviceAlarm">Whether the device flagged the packet itself.</param>
    /// <param name="bands">The limits.</param>
    public static TriageResult Classify(
        SensorCategory category,
        TelemetryUnit unit,
        double value,
        double? baseline,
        bool deviceAlarm,
        CriticalBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);

        if (category == SensorCategory.Environmental && unit == TelemetryUnit.Percent
            && value < bands.EnvironmentalFloorPercent)
        {
            return Urgent(AlertCode.CriticalLow,
                $"{value:0.#}% is below the {bands.EnvironmentalFloorPercent:0}% safety floor");
        }

        if (category == SensorCategory.PowerConsumption && unit == TelemetryUnit.Watt)
        {
            if (value > bands.PowerCeilingWatts)
            {
                return Urgent(AlertCode.PowerSpike,
                    $"{value:0} W is above the {bands.PowerCeilingWatts:0} W ceiling");
            }

            if (baseline is > 0d and { } normal && value > normal * bands.PowerSpikeRatio)
            {
                return Urgent(AlertCode.PowerSpike,
                    $"{value:0} W is {value / normal:0.0}x this meter's normal {normal:0} W");
            }
        }

        if (category == SensorCategory.Connectivity && unit == TelemetryUnit.Dbm
            && value < bands.LinkFloorDbm)
        {
            return Urgent(AlertCode.LinkLoss,
                $"{value:0} dBm is weaker than the {bands.LinkFloorDbm:0} dBm hop floor");
        }

        // Checked last: a device that flags its own packet still gets the more
        // specific reason above when its value also crosses a gateway band.
        if (deviceAlarm)
        {
            return Urgent(AlertCode.DeviceAlarm, "the device raised its own alarm flag");
        }

        return TriageResult.Routine;
    }

    /// <summary>
    /// Whether a condition has cleared. Each clears with a margin past the line
    /// that raised it, so a reading hovering on the line does not raise and clear
    /// the same alert on alternate packets.
    /// </summary>
    public static bool HasRecovered(AlertCode code, double value, double? baseline, CriticalBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);

        return code switch
        {
            AlertCode.CriticalLow => value >= bands.EnvironmentalFloorPercent + bands.EnvironmentalRecoveryMargin,
            AlertCode.PowerSpike => value < bands.PowerCeilingWatts * 0.8d
                                    && (baseline is not > 0d || value < baseline.Value * (bands.PowerSpikeRatio * 0.8d)),
            AlertCode.LinkLoss => value >= bands.LinkFloorDbm + 5d,
            AlertCode.DeviceAlarm => true,
            _ => true
        };
    }

    private static TriageResult Urgent(AlertCode code, string reason)
        => new(DispatchLane.Priority, (int)AlertCodes.SeverityOf(code), code, reason);
}
