namespace SmartX.Contracts.Dashboard;

/// <summary>
/// One flagged reading, ranked so the worst sits at the top of the triage queue.
/// </summary>
public sealed class AnomalyResponse
{
    /// <summary>Stable key, so an acknowledgement can name the anomaly it clears.</summary>
    public Guid Id { get; set; }

    /// <summary>Sensor that produced the reading.</summary>
    public Guid SensorId { get; set; }

    /// <summary>Node address.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Operator-facing label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Full deployment path, so an operator knows where to walk.</summary>
    public string QualifiedPath { get; set; } = string.Empty;

    /// <summary>What kind of problem this is - "Spike", "Drop" or "Disconnect".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>How bad it is - "Warning" or "Critical".</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>The offending reading, formatted with its unit.</summary>
    public string Reading { get; set; } = string.Empty;

    /// <summary>Standard deviations from the sensor rolling mean.</summary>
    public double ZScore { get; set; }

    /// <summary>When it was detected.</summary>
    public DateTimeOffset DetectedAtUtc { get; set; }

    /// <summary>Plain-language explanation for the operator.</summary>
    public string Explanation { get; set; } = string.Empty;

    /// <summary>Whether an operator has already acknowledged it.</summary>
    public bool Acknowledged { get; set; }
}

/// <summary>
/// One sensor's recent readings, shaped for the heartbeat strip - a compact
/// sparkline that shows the shape of the signal and where it broke.
/// </summary>
public sealed class HeartbeatStripResponse
{
    /// <summary>Sensor the strip belongs to.</summary>
    public Guid SensorId { get; set; }

    /// <summary>Node address.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Operator-facing label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Full deployment path.</summary>
    public string QualifiedPath { get; set; } = string.Empty;

    /// <summary>Operational class.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Health state - "Online", "Degraded", "Silent" or "Decommissioned".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Most recent readings, oldest first, normalised to 0-1 for drawing.</summary>
    public List<double> Points { get; set; } = [];

    /// <summary>Z-score per point, so the strip can colour each bar individually.</summary>
    public List<double> ZScores { get; set; } = [];

    /// <summary>Latest reading, formatted with its unit.</summary>
    public string LatestReading { get; set; } = string.Empty;

    /// <summary>Seconds since the last packet, or null if the node has never reported.</summary>
    public double? SecondsSinceLastSeen { get; set; }

    /// <summary>Expected seconds between heartbeats, for comparison against the above.</summary>
    public int HeartbeatIntervalSeconds { get; set; }

    /// <summary>Unacknowledged anomalies currently open against this sensor.</summary>
    public int OpenAnomalies { get; set; }
}

/// <summary>
/// The gateway's composite health score and the badges earned against it - the
/// engagement layer that sits on top of the raw telemetry.
/// </summary>
public sealed class GatewayHealthResponse
{
    /// <summary>Composite score from 0 to 100.</summary>
    public int Score { get; set; }

    /// <summary>Band the score falls in - "Healthy", "Watch", "Degraded" or "Critical".</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>Share of registered nodes currently reporting, 0-1.</summary>
    public double UptimeRatio { get; set; }

    /// <summary>Share of recent readings flagged as anomalous, 0-1.</summary>
    public double AnomalyRatio { get; set; }

    /// <summary>Nodes that have missed two or more heartbeats.</summary>
    public int SilentNodes { get; set; }

    /// <summary>Anomalies still awaiting acknowledgement.</summary>
    public int OpenAnomalies { get; set; }

    /// <summary>Consecutive readings accepted without an anomaly.</summary>
    public long CleanIngestStreak { get; set; }

    /// <summary>Badges unlocked at the current state.</summary>
    public List<BadgeResponse> Badges { get; set; } = [];

    /// <summary>One-line summary of what is dragging the score down, or confirming it is fine.</summary>
    public string Headline { get; set; } = string.Empty;
}

/// <summary>One achievement badge in the engagement layer.</summary>
public sealed class BadgeResponse
{
    /// <summary>Badge name, e.g. "Mesh Guardian".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it takes to earn it.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether it is currently earned.</summary>
    public bool Earned { get; set; }

    /// <summary>Progress toward earning it, 0-1.</summary>
    public double Progress { get; set; }

    /// <summary>Emoji shown on the badge.</summary>
    public string Icon { get; set; } = string.Empty;
}

/// <summary>Fleet-wide counters for the statistics panel.</summary>
public sealed class DashboardStatsResponse
{
    /// <summary>Nodes registered.</summary>
    public int RegisteredSensors { get; set; }

    /// <summary>Nodes reporting inside their heartbeat window.</summary>
    public int OnlineSensors { get; set; }

    /// <summary>Nodes that have missed one heartbeat.</summary>
    public int DegradedSensors { get; set; }

    /// <summary>Nodes that have missed two or more.</summary>
    public int SilentSensors { get; set; }

    /// <summary>Readings accepted since the gateway started.</summary>
    public long TotalReadings { get; set; }

    /// <summary>Historical batches held across every sensor.</summary>
    public int TotalBatches { get; set; }

    /// <summary>Live elements across every ingestion ring buffer.</summary>
    public int BufferOccupancy { get; set; }

    /// <summary>Combined ring-buffer capacity.</summary>
    public int BufferCapacity { get; set; }

    /// <summary>Readings overwritten by ring buffers - the back-pressure signal.</summary>
    public long BufferOverwritten { get; set; }

    /// <summary>Files attached across every sensor profile.</summary>
    public int Attachments { get; set; }

    /// <summary>Anomalies raised since the gateway started.</summary>
    public int TotalAnomalies { get; set; }

    /// <summary>Anomalies still awaiting acknowledgement.</summary>
    public int OpenAnomalies { get; set; }

    /// <summary>Aggregate power draw across every power sensor, via the overloaded + operator.</summary>
    public string AggregatePowerDraw { get; set; } = string.Empty;

    /// <summary>Nodes per category, keyed by category name.</summary>
    public Dictionary<string, int> SensorsByCategory { get; set; } = [];

    /// <summary>Zone labels, in heat-grid row order.</summary>
    public List<string> HeatZones { get; set; } = [];

    /// <summary>The zone-by-hour grid, with nulls for hours that saw no readings.</summary>
    public List<List<double?>> HeatRows { get; set; } = [];

    /// <summary>Comparisons the last binary search used.</summary>
    public int LastBinarySearchComparisons { get; set; }

    /// <summary>Comparisons the last linear search used.</summary>
    public int LastLinearSearchComparisons { get; set; }

    /// <summary>When these figures were computed.</summary>
    public DateTimeOffset GeneratedAtUtc { get; set; }
}
