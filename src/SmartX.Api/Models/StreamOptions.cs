using System.ComponentModel.DataAnnotations;
using SmartX.Core.Insights;
using SmartX.Core.Streaming;

namespace SmartX.Api.Models;

/// <summary>
/// Tuning for the real-time command stream, bound from the <c>Stream</c> section of
/// appsettings.json.
/// </summary>
public sealed class StreamOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Stream";

    /// <summary>
    /// Milliseconds of work the dispatch worker spends on each packet.
    /// <para>
    /// A gateway on a Raspberry Pi-class board validates, persists and scores each
    /// packet in tens of milliseconds, not microseconds. On a development laptop the
    /// queue would otherwise drain the instant anything entered it, and the bypass
    /// the priority lane exists for could never be seen. This budget models the
    /// constrained edge device. Set it to 0 to run at full speed.
    /// </para>
    /// </summary>
    [Range(0, 1000)]
    public int SimulatedProcessingMs { get; set; } = 20;

    /// <summary>Routine packets held before new ones are shed.</summary>
    [Range(10, 1_000_000)]
    public int StandardLaneCapacity { get; set; } = 10_000;

    /// <summary>Seconds a device has to acknowledge a command before it is marked timed out.</summary>
    [Range(1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Seconds between health sweeps for disconnected nodes and stale commands.</summary>
    [Range(1, 300)]
    public int SweepIntervalSeconds { get; set; } = 5;

    /// <summary>Milliseconds between batched pushes to the console.</summary>
    [Range(100, 10_000)]
    public int BroadcastIntervalMs { get; set; } = 500;

    /// <summary>Events the history keeps before the oldest are evicted.</summary>
    [Range(100, 1_000_000)]
    public int EventLogCapacity { get; set; } = 5000;

    /// <summary>How many commands undo can reach back through.</summary>
    [Range(1, 10_000)]
    public int UndoDepth { get; set; } = 100;

    /// <summary>How close together two events must be for the pattern miner to link them.</summary>
    [Range(1, 1440)]
    public int PatternWindowMinutes { get; set; } = 10;

    /// <summary>Minutes a dismissed suggestion stays hidden.</summary>
    [Range(1, 1440)]
    public int DismissMinutes { get; set; } = 30;

    /// <summary>Whether to load demonstration operator history into the pattern miner at startup.</summary>
    public bool SeedOperatorHistory { get; set; } = true;

    /// <summary>The safety limits triage measures packets against.</summary>
    public CriticalBands Bands { get; set; } = new();

    /// <summary>Tuning for the predictive engine.</summary>
    public AdvisorSettings Advisor { get; set; } = new();
}
