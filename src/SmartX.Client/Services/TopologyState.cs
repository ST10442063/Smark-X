namespace SmartX.Client.Services;

/// <summary>A preset window for the history explorer.</summary>
public enum HistoryPreset
{
    LastHour,
    LastTwoHours,
    LastSixHours,
    LastDay,
    Custom
}

/// <summary>
/// What the Network Topology pages remember while the operator is elsewhere: the
/// selected node, the spanning-tree algorithm, the map's view options and the
/// history window. Scoped, which in WebAssembly means one per tab, so moving to
/// the Command Stream and back lands on exactly the node and window that were open.
/// </summary>
public sealed class TopologyState
{
    /// <summary>The node selected on the map, by MAC.</summary>
    public string? SelectedMac { get; set; }

    /// <summary>"Prim" or "Kruskal".</summary>
    public string Algorithm { get; set; } = "Prim";

    /// <summary>Draw every live link, not just the spanning tree.</summary>
    public bool ShowAllLinks { get; set; } = true;

    /// <summary>Label every node on the map, not just the selected one and the gateway.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>The history explorer's window.</summary>
    public HistoryPreset Preset { get; set; } = HistoryPreset.LastTwoHours;

    /// <summary>Custom window start, in the browser's local time.</summary>
    public DateTime? CustomFromLocal { get; set; }

    /// <summary>Custom window end, in the browser's local time.</summary>
    public DateTime? CustomToLocal { get; set; }

    /// <summary>History log page, 1-based.</summary>
    public int Page { get; set; } = 1;

    /// <summary>History log reads newest first.</summary>
    public bool NewestFirst { get; set; } = true;

    /// <summary>
    /// The window to query. Presets are measured back from the newest reading the
    /// tree holds rather than from now, so a node that went quiet still shows its
    /// last hours instead of an empty chart.
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset To) Window(DateTimeOffset newestUtc)
    {
        var end = newestUtc.AddSeconds(1);

        return Preset switch
        {
            HistoryPreset.LastHour => (end.AddHours(-1), end),
            HistoryPreset.LastSixHours => (end.AddHours(-6), end),
            HistoryPreset.LastDay => (end.AddDays(-1), end),
            HistoryPreset.Custom when CustomFromLocal is { } from && CustomToLocal is { } to && from < to
                => (ToUtc(from), ToUtc(to)),
            _ => (end.AddHours(-2), end)
        };
    }

    /// <summary>Reads a value typed into a local-time input as the instant it names.</summary>
    public static DateTimeOffset ToUtc(DateTime local)
        => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
}
