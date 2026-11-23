using System.ComponentModel.DataAnnotations;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Insights;

namespace SmartX.Contracts.Stream;

/// <summary>
/// Names shared by the gateway's SignalR hub and the console, so a renamed event
/// is a compile error on both sides rather than a silent disconnect.
/// </summary>
public static class StreamHubContract
{
    /// <summary>Hub route, relative to the gateway origin.</summary>
    public const string Path = "/hubs/stream";

    /// <summary>A critical packet was processed. Sent the moment it happens, never batched.</summary>
    public const string CriticalAlert = "CriticalAlert";

    /// <summary>A batch of processed packets.</summary>
    public const string PacketsProcessed = "PacketsProcessed";

    /// <summary>Queue depths and counters.</summary>
    public const string QueueStats = "QueueStats";

    /// <summary>Devices whose live state changed.</summary>
    public const string DevicesChanged = "DevicesChanged";

    /// <summary>New entries in the event history.</summary>
    public const string EventsLogged = "EventsLogged";

    /// <summary>The command stacks changed.</summary>
    public const string CommandsChanged = "CommandsChanged";

    /// <summary>The suggestions changed.</summary>
    public const string SuggestionsChanged = "SuggestionsChanged";
}

/// <summary>Queue depths, throughput and the bypass record.</summary>
public sealed class QueueStatsResponse
{
    public int StandardDepth { get; set; }

    public int PriorityDepth { get; set; }

    public int StandardCapacity { get; set; }

    /// <summary>Deepest the standard lane has been.</summary>
    public int StandardHighWater { get; set; }

    public long StandardProcessed { get; set; }

    public long PriorityProcessed { get; set; }

    /// <summary>Routine packets refused because the standard lane was full.</summary>
    public long StandardShed { get; set; }

    /// <summary>Priority packets that left ahead of at least one waiting routine packet.</summary>
    public long Bypasses { get; set; }

    /// <summary>Routine packets jumped, across every bypass.</summary>
    public long PacketsOvertaken { get; set; }

    public double StandardMeanWaitMs { get; set; }

    public double PriorityMeanWaitMs { get; set; }

    /// <summary>How long the most recent priority packet waited.</summary>
    public double? LastPriorityWaitMs { get; set; }

    /// <summary>How many routine packets the most recent priority packet jumped.</summary>
    public int? LastPriorityOvertook { get; set; }

    /// <summary>Packets processed per second, over the last few seconds.</summary>
    public double ThroughputPerSecond { get; set; }

    /// <summary>The configured per-packet processing cost, in milliseconds.</summary>
    public int ProcessingBudgetMs { get; set; }

    /// <summary>Seconds since any device last published, or null if none has.</summary>
    public double? SecondsSinceLastUplink { get; set; }

    /// <summary>Alert conditions active now.</summary>
    public int AlertsActive { get; set; }

    /// <summary>Repeat alerts the alert set absorbed.</summary>
    public long AlertDuplicatesSuppressed { get; set; }

    /// <summary>Nodes known to be down.</summary>
    public int NodesDown { get; set; }

    /// <summary>Repeat disconnects the outage set absorbed.</summary>
    public long OutageDuplicatesSuppressed { get; set; }
}

/// <summary>One packet as it left the queue.</summary>
public sealed class ProcessedPacketResponse
{
    public long Sequence { get; set; }

    public string MacAddress { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"Standard" or "Priority".</summary>
    public string Lane { get; set; } = string.Empty;

    /// <summary>The reading, formatted with its unit.</summary>
    public string Reading { get; set; } = string.Empty;

    public double WaitedMs { get; set; }

    /// <summary>Routine packets this one jumped.</summary>
    public int Overtook { get; set; }

    public DateTimeOffset ProcessedAtUtc { get; set; }

    /// <summary>Why triage marked it critical, if it did.</summary>
    public string? Reason { get; set; }
}

/// <summary>A critical packet, pushed to every console the moment it is processed.</summary>
public sealed class CriticalAlertResponse
{
    public string MacAddress { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"CriticalLow", "PowerSpike", "LinkLoss" or "DeviceAlarm".</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>"Critical" or "High".</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>The reading, formatted with its unit.</summary>
    public string Reading { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>False when the same condition was already active - the alert set absorbed it.</summary>
    public bool IsNew { get; set; }

    public double WaitedMs { get; set; }

    public int Overtook { get; set; }

    public DateTimeOffset DetectedAtUtc { get; set; }
}

/// <summary>A device's live state for the Command Stream grid.</summary>
public sealed class DeviceLiveResponse
{
    public string MacAddress { get; set; } = string.Empty;

    public Guid SensorId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string QualifiedPath { get; set; } = string.Empty;

    public string Zone { get; set; } = string.Empty;

    /// <summary>Unit symbol, e.g. "W".</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>"Online", "Degraded" or "Silent".</summary>
    public string Status { get; set; } = string.Empty;

    public double? LatestValue { get; set; }

    public string LatestReading { get; set; } = string.Empty;

    public double? SecondsSinceLastSeen { get; set; }

    public bool IsActuator { get; set; }

    /// <summary>The state an operator last asked for.</summary>
    public bool? DesiredState { get; set; }

    /// <summary>The state the device last reported.</summary>
    public bool? ReportedState { get; set; }

    /// <summary>True while a command has not yet been confirmed.</summary>
    public bool AwaitingConfirmation { get; set; }

    /// <summary>Commands waiting in the device's downlink mailbox.</summary>
    public int PendingCommands { get; set; }

    /// <summary>Active alert codes.</summary>
    public List<string> Alerts { get; set; } = [];

    /// <summary>"Critical", "Warning", "Disconnected" or "Clear".</summary>
    public string AlertState { get; set; } = "Clear";

    /// <summary>Recent per-minute means, oldest first, for a sparkline.</summary>
    public List<double> Recent { get; set; } = [];

    /// <summary>True when the predictive engine is pointing at this device.</summary>
    public bool Flagged { get; set; }

    /// <summary>The engine's reason, when flagged.</summary>
    public string? FlagReason { get; set; }
}

/// <summary>A filtered device list, with what the filter cost.</summary>
public sealed class DeviceFilterResponse
{
    public List<DeviceLiveResponse> Devices { get; set; } = [];

    public int FleetSize { get; set; }

    /// <summary>Devices the text match had to examine after the set intersection.</summary>
    public int CandidatesExamined { get; set; }

    /// <summary>How the filter was evaluated, in words.</summary>
    public string Strategy { get; set; } = string.Empty;

    /// <summary>Devices per category, for the filter chips.</summary>
    public Dictionary<string, int> CategoryCounts { get; set; } = [];

    /// <summary>Devices per alert state, for the filter chips.</summary>
    public Dictionary<string, int> AlertCounts { get; set; } = [];
}

/// <summary>One minute of a device's timeline.</summary>
public sealed class TimelinePointResponse
{
    public DateTimeOffset StartUtc { get; set; }

    public double Minimum { get; set; }

    public double Maximum { get; set; }

    public double Mean { get; set; }

    public int Count { get; set; }

    /// <summary>Critical readings in the minute.</summary>
    public int Critical { get; set; }
}

/// <summary>A device's timeline, oldest first, straight from its sorted dictionary.</summary>
public sealed class TimelineResponse
{
    public string MacAddress { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public int ResolutionSeconds { get; set; }

    public List<TimelinePointResponse> Points { get; set; } = [];

    /// <summary>Buckets held in the device's sorted dictionary.</summary>
    public int BucketsHeld { get; set; }

    /// <summary>Readings that arrived out of order and were slotted into an earlier bucket.</summary>
    public long LateArrivals { get; set; }
}

/// <summary>One entry in the command stream history.</summary>
public sealed class StreamEventResponse
{
    public long Sequence { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>e.g. "AlertRaised", "CommandIssued", "NodeDown".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>"Info", "Warning" or "Critical".</summary>
    public string Level { get; set; } = string.Empty;

    public string? MacAddress { get; set; }

    public string? DisplayName { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}

/// <summary>Everything the Command Stream page needs for its first paint, in one request.</summary>
public sealed class StreamSnapshotResponse
{
    public QueueStatsResponse Queue { get; set; } = new();

    public DeviceFilterResponse Devices { get; set; } = new();

    public List<StreamEventResponse> Events { get; set; } = [];

    public List<ProcessedPacketResponse> RecentPackets { get; set; } = [];

    public CommandHistoryResponse Commands { get; set; } = new();

    public InsightsResponse Insights { get; set; } = new();
}

/// <summary>Asks the gateway to inject a critical packet through the normal triage path.</summary>
public sealed class SimulateCriticalRequest
{
    /// <summary>"PowerSpike", "MoistureCrash" or "LinkLoss".</summary>
    [Required]
    [RegularExpression("^(PowerSpike|MoistureCrash|LinkLoss)$", ErrorMessage = "Kind must be PowerSpike, MoistureCrash or LinkLoss.")]
    public string Kind { get; set; } = "PowerSpike";

    /// <summary>Which device; a suitable live one is chosen when omitted.</summary>
    public string? MacAddress { get; set; }
}

/// <summary>Asks the gateway to flood the standard lane with routine packets.</summary>
public sealed class FloodRequest
{
    [Range(1, 5000)]
    public int Count { get; set; } = 500;
}

/// <summary>What a test injection queued.</summary>
public sealed class TestInjectionResponse
{
    public string Message { get; set; } = string.Empty;

    public UplinkEnvelopeResponse Uplink { get; set; } = new();
}
