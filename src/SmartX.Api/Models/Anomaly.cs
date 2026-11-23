namespace SmartX.Api.Models;

/// <summary>What sort of problem an anomaly represents.</summary>
public enum AnomalyKind
{
    /// <summary>A reading far above the sensor rolling mean.</summary>
    Spike = 0,

    /// <summary>A reading far below it - a moisture crash, a voltage sag.</summary>
    Drop = 1,

    /// <summary>The node stopped publishing inside its heartbeat window.</summary>
    Disconnect = 2
}

/// <summary>How urgent an anomaly is.</summary>
public enum AnomalySeverity
{
    /// <summary>Outside the nominal band but not yet alarming.</summary>
    Warning = 0,

    /// <summary>Far enough out to need attention now.</summary>
    Critical = 1
}

/// <summary>
/// A flagged reading or a missed heartbeat, held so the dashboard can rank the
/// fleet worst-first and an operator can clear items as they are dealt with.
/// </summary>
public sealed class Anomaly
{
    /// <summary>Stable key, so an acknowledgement can name what it clears.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Sensor that produced it.</summary>
    public required Guid SensorId { get; init; }

    /// <summary>Node address, denormalised so the dashboard needs no second lookup.</summary>
    public required string MacAddress { get; init; }

    /// <summary>What sort of problem this is.</summary>
    public required AnomalyKind Kind { get; init; }

    /// <summary>How urgent it is.</summary>
    public required AnomalySeverity Severity { get; init; }

    /// <summary>The offending reading, formatted with its unit.</summary>
    public required string Reading { get; init; }

    /// <summary>Standard deviations from the sensor rolling mean. Zero for a disconnect.</summary>
    public double ZScore { get; init; }

    /// <summary>When it was detected.</summary>
    public DateTimeOffset DetectedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Plain-language explanation for the operator.</summary>
    public required string Explanation { get; init; }

    /// <summary>Whether an operator has cleared it.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>
    /// Ranking weight for the triage queue. Critical outranks warning, and within a
    /// band the larger deviation comes first. A disconnect scores above every spike
    /// because a node that has stopped reporting cannot report anything worse.
    /// </summary>
    public double TriageWeight => Kind == AnomalyKind.Disconnect
        ? 1000d
        : (Severity == AnomalySeverity.Critical ? 100d : 0d) + Math.Abs(ZScore);
}
