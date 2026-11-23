using System.Diagnostics;
using SmartX.Contracts.History;
using SmartX.Core.History;
using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services.History;

/// <summary>
/// Answers the topology page's history questions from each sensor's AVL tree:
/// a page of a time window, a summary of the window, and the window cut into
/// chart slices.
/// <para>
/// Every query runs inside <see cref="GatewayStore.ReadTimeIndex{T}"/>, so it
/// holds the store lock for exactly as long as the tree walk takes - O(log n) for
/// the summary and O(log n + page size) for the page - and returns plain response
/// objects that are safe to serialise after the lock is released.
/// </para>
/// </summary>
public sealed class HistoryQueryService(GatewayStore store)
{
    /// <summary>The default window when the caller names none: the last two hours of data held.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(2);

    /// <summary>Finds a sensor by MAC, or null.</summary>
    public SensorRecord? Find(string mac) => store.FindByMac(mac).Sensor;

    /// <summary>
    /// Resolves an open-ended window against what the tree holds: a missing end is
    /// just after the newest reading, and a missing start is two hours before the end.
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset To) ResolveWindow(SensorRecord sensor, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        var newest = store.ReadTimeIndex(sensor.Id, tree => tree?.NewestUtc) ?? DateTimeOffset.UtcNow;

        // One tick past the newest reading, because the window's end is exclusive.
        var to = toUtc ?? newest.AddTicks(1);
        var from = fromUtc ?? to - DefaultWindow;
        return (from, to);
    }

    /// <summary>One page of the window, plus its O(log n) summary.</summary>
    public HistoryPageResponse Page(
        SensorRecord sensor,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int page,
        int pageSize,
        bool newestFirst)
    {
        var skip = (page - 1) * pageSize;

        return store.ReadTimeIndex(sensor.Id, tree =>
        {
            var response = new HistoryPageResponse
            {
                SensorId = sensor.Id,
                MacAddress = sensor.MacAddress,
                DisplayName = sensor.DisplayName,
                Unit = SensorReading.UnitSymbol(sensor.Unit),
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Page = page,
                PageSize = pageSize,
                NewestFirst = newestFirst
            };

            if (tree is null)
            {
                return response;
            }

            var clock = Stopwatch.StartNew();
            var summary = tree.Summarise(fromUtc, toUtc, out var summaryCost);
            var summaryMicros = clock.Elapsed.TotalMicroseconds;

            clock.Restart();
            var readings = tree.Page(fromUtc, toUtc, skip, pageSize, newestFirst, out var total, out var pageCost);
            var pageMicros = clock.Elapsed.TotalMicroseconds;

            response.TotalInWindow = total;
            response.TotalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
            response.Summary = ToSummary(summary);
            response.SummaryCost = ToCost(summaryCost, summaryMicros);
            response.PageCost = ToCost(pageCost, pageMicros);
            response.Readings = readings.Select(point => ToReading(point, sensor.Unit)).ToList();
            return response;
        });
    }

    /// <summary>
    /// The window cut into <paramref name="buckets"/> equal slices. Each slice is one
    /// O(log n) <see cref="TelemetryAvlTree.Summarise"/>, so the chart costs
    /// O(buckets x log n) however many readings the window holds.
    /// </summary>
    public HistorySeriesResponse Series(SensorRecord sensor, DateTimeOffset fromUtc, DateTimeOffset toUtc, int buckets)
    {
        var width = (toUtc - fromUtc) / buckets;

        return store.ReadTimeIndex(sensor.Id, tree =>
        {
            var response = new HistorySeriesResponse
            {
                MacAddress = sensor.MacAddress,
                DisplayName = sensor.DisplayName,
                Unit = SensorReading.UnitSymbol(sensor.Unit),
                FromUtc = fromUtc,
                ToUtc = toUtc,
                BucketSeconds = width.TotalSeconds
            };

            if (tree is null)
            {
                return response;
            }

            var visited = 0;
            var clock = Stopwatch.StartNew();

            for (var i = 0; i < buckets; i++)
            {
                var start = fromUtc + (width * i);
                var end = i == buckets - 1 ? toUtc : start + width;
                var slice = tree.Summarise(start, end, out var cost);
                visited += cost.NodesVisited;

                response.Buckets.Add(new HistoryBucketResponse
                {
                    StartUtc = start,
                    Count = slice.Count,
                    Minimum = slice.Minimum,
                    Maximum = slice.Maximum,
                    Mean = slice.Mean
                });
            }

            response.Cost = ToCost(new QueryCost(visited, tree.Count, tree.Height), clock.Elapsed.TotalMicroseconds);
            return response;
        });
    }

    /// <summary>What the sensor's tree holds.</summary>
    public HistoryBoundsResponse Bounds(SensorRecord sensor)
    {
        var (_, _, retention) = store.TimeIndexState();

        return store.ReadTimeIndex(sensor.Id, tree => new HistoryBoundsResponse
        {
            SensorId = sensor.Id,
            MacAddress = sensor.MacAddress,
            DisplayName = sensor.DisplayName,
            Unit = SensorReading.UnitSymbol(sensor.Unit),
            OldestUtc = tree?.OldestUtc,
            NewestUtc = tree?.NewestUtc,
            Count = tree?.Count ?? 0,
            TreeHeight = tree?.Height ?? 0,
            Inserted = tree?.Inserted ?? 0,
            Trimmed = tree?.Trimmed ?? 0,
            Rotations = tree?.Rotations ?? 0,
            RetentionHours = retention.TotalHours
        });
    }

    /// <summary>The newest <paramref name="count"/> readings, newest first - the topology page's recent log.</summary>
    public List<HistoryReadingResponse> Recent(SensorRecord sensor, int count)
        => store.ReadTimeIndex(sensor.Id, tree => tree is null
            ? []
            : tree.Page(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, count, newestFirst: true, out _, out _)
                .Select(point => ToReading(point, sensor.Unit))
                .ToList());

    private static HistoryReadingResponse ToReading(HistoryPoint point, TelemetryUnit unit) => new()
    {
        TimestampUtc = point.TimestampUtc,
        Value = point.Value,
        Formatted = new SensorReading(point.Value, unit, point.TimestampUtc).ToString()
    };

    private static HistorySummaryResponse ToSummary(RangeSummary summary) => new()
    {
        Count = summary.Count,
        Minimum = summary.Minimum,
        Maximum = summary.Maximum,
        Mean = summary.Mean
    };

    private static TreeQueryCostResponse ToCost(QueryCost cost, double micros) => new()
    {
        NodesVisited = cost.NodesVisited,
        TreeSize = cost.TreeSize,
        TreeHeight = cost.TreeHeight,
        LinearScanEquivalent = cost.LinearScanEquivalent,
        Microseconds = Math.Round(micros, 1)
    };
}
