using SmartX.Core.Commands;
using SmartX.Core.Models;
using SmartX.Core.Timeline;

namespace SmartX.Core.Devices;

/// <summary>
/// The gateway's live picture of one device - what IoT platforms call a device
/// shadow or digital twin.
/// <para>
/// It holds what the device last reported, what an operator last asked it to do,
/// and the gap between the two. For an actuator that gap is the interesting part:
/// a pump commanded on but still reporting off is a pump whose command has not
/// landed yet, or never will.
/// </para>
/// <para>
/// Not thread-safe: the stream store owns every instance and serialises access.
/// </para>
/// </summary>
public sealed class DeviceState
{
    /// <summary>
    /// Weight of the newest reading in the running baseline. At 0.1 the baseline
    /// reflects roughly the last ten readings - long enough that one spike barely
    /// moves it, short enough that it follows a genuine change in a few minutes.
    /// </summary>
    public const double BaselineWeight = 0.1;

    /// <summary>Creates the shadow for a registered sensor.</summary>
    public DeviceState(MacKey mac, SensorRecord sensor, SensorTimeline? timeline = null)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        Mac = mac;
        SensorId = sensor.Id;
        DisplayName = sensor.DisplayName;
        Category = sensor.Category;
        Unit = sensor.Unit;
        QualifiedPath = sensor.Location.QualifiedPath;
        Zone = string.IsNullOrWhiteSpace(sensor.Location.Zone)
            ? sensor.Location.Facility
            : $"{sensor.Location.Facility} / {sensor.Location.Zone}";
        HeartbeatIntervalSeconds = sensor.HeartbeatIntervalSeconds;
        Timeline = timeline ?? new SensorTimeline();
    }

    /// <summary>The dictionary key.</summary>
    public MacKey Mac { get; }

    /// <summary>The Part 1 registry key for the same node.</summary>
    public Guid SensorId { get; }

    /// <summary>Operator-facing label.</summary>
    public string DisplayName { get; }

    /// <summary>Operational class.</summary>
    public SensorCategory Category { get; }

    /// <summary>Unit every reading is in.</summary>
    public TelemetryUnit Unit { get; }

    /// <summary>Full deployment path.</summary>
    public string QualifiedPath { get; }

    /// <summary>Facility and zone, for grouping.</summary>
    public string Zone { get; }

    /// <summary>Expected seconds between packets.</summary>
    public int HeartbeatIntervalSeconds { get; }

    /// <summary>Whether this is a relay, valve or pump that accepts commands.</summary>
    public bool IsActuator => Category == SensorCategory.Actuator;

    /// <summary>Latest reported value, by capture time.</summary>
    public double? LatestValue { get; private set; }

    /// <summary>Capture time of <see cref="LatestValue"/>.</summary>
    public DateTimeOffset? LastSeenUtc { get; private set; }

    /// <summary>
    /// Exponentially weighted moving average of the readings. The triage step
    /// compares a new packet against this in O(1), without touching history.
    /// </summary>
    public double? Baseline { get; private set; }

    /// <summary>Packets observed for this device.</summary>
    public long PacketsObserved { get; private set; }

    /// <summary>Recent history, in timestamp order.</summary>
    public SensorTimeline Timeline { get; }

    /// <summary>
    /// Commands waiting for the device to collect them, oldest first. A queue,
    /// because a device must apply its commands in the order they were issued.
    /// </summary>
    public Queue<DeviceCommand> Downlink { get; } = new();

    /// <summary>The relay state an operator last asked for. Null until one is issued.</summary>
    public bool? DesiredState { get; set; }

    /// <summary>The relay state the device last reported or acknowledged.</summary>
    public bool? ReportedState { get; private set; }

    /// <summary>
    /// The state undo and toggle should treat as current: what was asked for if
    /// anything was, otherwise what the device says.
    /// </summary>
    public bool? EffectiveState => DesiredState ?? ReportedState;

    /// <summary>True while a command has been issued but not yet confirmed.</summary>
    public bool AwaitingConfirmation => DesiredState is { } desired && ReportedState != desired;

    /// <summary>
    /// Records a reading. A packet older than the latest one still goes into the
    /// timeline in its correct place, but does not overwrite the current value -
    /// a late flush of buffered readings must not make a device look as if it has
    /// jumped back in time.
    /// </summary>
    /// <param name="value">The reading.</param>
    /// <param name="timestampUtc">When it was captured.</param>
    /// <param name="critical">Whether triage flagged it.</param>
    /// <returns>
    /// True when this is now the device's latest reading. False for a late packet,
    /// which must not, for instance, clear an alert raised by a newer one.
    /// </returns>
    public bool Observe(double value, DateTimeOffset timestampUtc, bool critical = false)
    {
        PacketsObserved++;
        Timeline.Add(timestampUtc, value, critical);

        Baseline = Baseline is { } baseline
            ? baseline + (BaselineWeight * (value - baseline))
            : value;

        if (LastSeenUtc is null || timestampUtc >= LastSeenUtc)
        {
            LatestValue = value;
            LastSeenUtc = timestampUtc;

            if (IsActuator)
            {
                ReportedState = value > 0.5d;
            }

            return true;
        }

        return false;
    }

    /// <summary>Records a state the device confirmed applying.</summary>
    public void ConfirmState(bool state) => ReportedState = state;

    /// <summary>
    /// Heartbeat health, by the same rule as Part 1: one missed window is
    /// Degraded, two is Silent, and a device that has never reported is Silent.
    /// </summary>
    public SensorStatus EvaluateStatus(DateTimeOffset asOfUtc)
    {
        if (LastSeenUtc is not { } seen)
        {
            return SensorStatus.Silent;
        }

        var window = TimeSpan.FromSeconds(HeartbeatIntervalSeconds);
        var silence = asOfUtc - seen;

        if (silence > window * 2)
        {
            return SensorStatus.Silent;
        }

        return silence > window ? SensorStatus.Degraded : SensorStatus.Online;
    }
}
