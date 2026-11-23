namespace SmartX.Core.Models;

/// <summary>
/// A registered Smart-X mesh node. This is the internal domain object the
/// gateway stores after a registration request has been validated.
/// </summary>
public sealed class SensorRecord
{
    /// <summary>Gateway-assigned surrogate key.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Normalised MAC address in upper-case colon form, e.g. "A4:CF:12:9B:0E:77".
    /// Normalisation happens once at registration so the sorted index can rely on
    /// ordinal comparison for its binary search.
    /// </summary>
    public required string MacAddress { get; init; }

    /// <summary>Operator-facing label, e.g. "Hydro Bay 3 - Moisture".</summary>
    public required string DisplayName { get; set; }

    /// <summary>Operational class of the node.</summary>
    public required SensorCategory Category { get; set; }

    /// <summary>Position in the deployment hierarchy.</summary>
    public required DeploymentLocation Location { get; set; }

    /// <summary>Unit every reading from this node is expected to carry.</summary>
    public required TelemetryUnit Unit { get; set; }

    /// <summary>Reported firmware build, used to flag nodes lagging behind the fleet.</summary>
    public string FirmwareVersion { get; set; } = "0.0.0";

    /// <summary>Expected seconds between heartbeats. Exceeding it marks the node Silent.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>When the gateway accepted the registration.</summary>
    public DateTimeOffset RegisteredAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Timestamp of the most recent accepted telemetry packet, if any.</summary>
    public DateTimeOffset? LastSeenUtc { get; set; }

    /// <summary>Total packets accepted from this node since gateway start.</summary>
    public long PacketsReceived { get; set; }

    /// <summary>
    /// Derives health from heartbeat freshness. A node that has never reported is
    /// treated as Silent rather than Online so a mis-flashed device is visible
    /// on the dashboard immediately.
    /// </summary>
    /// <param name="asOfUtc">Clock reading to evaluate against - injected so this is testable.</param>
    public SensorStatus EvaluateStatus(DateTimeOffset asOfUtc)
    {
        if (Status == SensorStatus.Decommissioned)
        {
            return SensorStatus.Decommissioned;
        }

        if (LastSeenUtc is null)
        {
            return SensorStatus.Silent;
        }

        var silence = asOfUtc - LastSeenUtc.Value;
        var window = TimeSpan.FromSeconds(HeartbeatIntervalSeconds);

        // Two missed heartbeats is a disconnect; one is merely degraded.
        if (silence > window * 2)
        {
            return SensorStatus.Silent;
        }

        return silence > window ? SensorStatus.Degraded : SensorStatus.Online;
    }

    /// <summary>Explicitly assigned status. Only Decommissioned is set by an operator.</summary>
    public SensorStatus Status { get; set; } = SensorStatus.Online;
}
