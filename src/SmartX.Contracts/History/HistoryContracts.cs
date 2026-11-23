namespace SmartX.Contracts.History;

/// <summary>One reading in a sensor's history log.</summary>
public sealed class HistoryReadingResponse
{
    /// <summary>When the reading was taken.</summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>The reading.</summary>
    public double Value { get; set; }

    /// <summary>Magnitude and unit rendered for display, e.g. "46.2 %".</summary>
    public string Formatted { get; set; } = string.Empty;
}

/// <summary>Count, minimum, maximum and mean of a window of readings.</summary>
public sealed class HistorySummaryResponse
{
    public int Count { get; set; }

    public double Minimum { get; set; }

    public double Maximum { get; set; }

    public double Mean { get; set; }
}

/// <summary>
/// What a query against the AVL tree cost, so the console can show the work the
/// tree saved compared with scanning every reading.
/// </summary>
public sealed class TreeQueryCostResponse
{
    /// <summary>Tree nodes the query touched.</summary>
    public int NodesVisited { get; set; }

    /// <summary>Readings in the tree.</summary>
    public int TreeSize { get; set; }

    /// <summary>Height of the tree.</summary>
    public int TreeHeight { get; set; }

    /// <summary>Readings an unordered list would have had to read for the same answer.</summary>
    public int LinearScanEquivalent { get; set; }

    /// <summary>Time the query took inside the gateway, in microseconds.</summary>
    public double Microseconds { get; set; }
}

/// <summary>One page of a sensor's history in a time window, plus a summary of the whole window.</summary>
public sealed class HistoryPageResponse
{
    public Guid SensorId { get; set; }

    public string MacAddress { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Unit symbol, e.g. "%".</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Start of the window, inclusive.</summary>
    public DateTimeOffset FromUtc { get; set; }

    /// <summary>End of the window, exclusive.</summary>
    public DateTimeOffset ToUtc { get; set; }

    /// <summary>1-based page number.</summary>
    public int Page { get; set; }

    public int PageSize { get; set; }

    /// <summary>Readings in the whole window, across every page.</summary>
    public int TotalInWindow { get; set; }

    public int TotalPages { get; set; }

    /// <summary>True when pages run from the newest reading backwards.</summary>
    public bool NewestFirst { get; set; }

    /// <summary>Count, min, max and mean of the whole window, from the tree's subtree aggregates.</summary>
    public HistorySummaryResponse Summary { get; set; } = new();

    /// <summary>The readings on this page, in the requested order.</summary>
    public List<HistoryReadingResponse> Readings { get; set; } = [];

    /// <summary>What finding this page cost.</summary>
    public TreeQueryCostResponse PageCost { get; set; } = new();

    /// <summary>What summarising the window cost.</summary>
    public TreeQueryCostResponse SummaryCost { get; set; } = new();
}

/// <summary>One slice of a history chart.</summary>
public sealed class HistoryBucketResponse
{
    public DateTimeOffset StartUtc { get; set; }

    /// <summary>Readings in the slice; 0 marks a gap.</summary>
    public int Count { get; set; }

    public double Minimum { get; set; }

    public double Maximum { get; set; }

    public double Mean { get; set; }
}

/// <summary>A window of history cut into equal slices, each summarised by the tree, for charting.</summary>
public sealed class HistorySeriesResponse
{
    public string MacAddress { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public DateTimeOffset FromUtc { get; set; }

    public DateTimeOffset ToUtc { get; set; }

    /// <summary>Width of each slice, in seconds.</summary>
    public double BucketSeconds { get; set; }

    /// <summary>The slices, oldest first.</summary>
    public List<HistoryBucketResponse> Buckets { get; set; } = [];

    /// <summary>What building every slice cost, summed.</summary>
    public TreeQueryCostResponse Cost { get; set; } = new();
}

/// <summary>What a sensor's history tree holds, so the console can offer sensible windows.</summary>
public sealed class HistoryBoundsResponse
{
    public Guid SensorId { get; set; }

    public string MacAddress { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    /// <summary>Oldest reading held, or null when the sensor has never reported.</summary>
    public DateTimeOffset? OldestUtc { get; set; }

    /// <summary>Newest reading held.</summary>
    public DateTimeOffset? NewestUtc { get; set; }

    /// <summary>Readings held.</summary>
    public int Count { get; set; }

    /// <summary>Height of the AVL tree.</summary>
    public int TreeHeight { get; set; }

    /// <summary>Readings inserted over the tree's lifetime.</summary>
    public long Inserted { get; set; }

    /// <summary>Readings trimmed by the retention window.</summary>
    public long Trimmed { get; set; }

    /// <summary>Rotations performed keeping the tree balanced.</summary>
    public long Rotations { get; set; }

    /// <summary>History kept behind the newest reading, in hours.</summary>
    public double RetentionHours { get; set; }
}
