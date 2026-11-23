using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using SmartX.Contracts.History;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.Tests;

/// <summary>
/// Drives the history endpoints end to end: readings go in through the Part 1
/// batch endpoint, land in the gateway's AVL trees, and come back out of
/// time-window queries exactly as a full scan would return them.
/// </summary>
public class HistoryApiTests
{
    private const string Probe = "A4:CF:12:AA:00:01";

    // 12:00 to 16:00 on a fixed day, one reading a minute.
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Window_FourteenToSixteen_ReturnsOnlyThoseReadings()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await SeedAsync(http);

        var page = await Gateway.GetAsync<HistoryPageResponse>(http,
            $"api/history/{Probe}?fromUtc={Iso(Noon.AddHours(2))}&toUtc={Iso(Noon.AddHours(4))}&pageSize=500&order=oldest");

        Assert.Equal(120, page.TotalInWindow);
        Assert.Equal(120, page.Readings.Count);
        Assert.Equal(Noon.AddHours(2), page.Readings[0].TimestampUtc);
        Assert.Equal(Noon.AddHours(4).AddMinutes(-1), page.Readings[^1].TimestampUtc);
        Assert.Equal(120, page.Summary.Count);

        // Values are the minute index, so the window 120..239 has a known mean.
        Assert.Equal(120, page.Summary.Minimum);
        Assert.Equal(239, page.Summary.Maximum);
        Assert.Equal(179.5, page.Summary.Mean, tolerance: 1e-9);

        // The summary was read from subtree aggregates, not from the 120 readings.
        Assert.Equal(240, page.SummaryCost.TreeSize);
        Assert.True(page.SummaryCost.NodesVisited < 3 * page.SummaryCost.TreeHeight);
    }

    [Fact]
    public async Task Paging_NewestFirst_WalksTheWindowWithoutGaps()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await SeedAsync(http);

        var seen = new List<DateTimeOffset>();
        HistoryPageResponse page;
        var number = 1;

        do
        {
            page = await Gateway.GetAsync<HistoryPageResponse>(http,
                $"api/history/{Probe}?fromUtc={Iso(Noon)}&toUtc={Iso(Noon.AddHours(1))}&page={number++}&pageSize=25");
            seen.AddRange(page.Readings.Select(reading => reading.TimestampUtc));
        }
        while (page.Readings.Count > 0);

        Assert.Equal(60, seen.Count);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(seen.OrderDescending(), seen);
        Assert.Equal(Noon.AddMinutes(59), seen[0]);
    }

    [Fact]
    public async Task DefaultWindow_IsTheLastTwoHoursHeld()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await SeedAsync(http);

        var page = await Gateway.GetAsync<HistoryPageResponse>(http, $"api/history/{Probe}?pageSize=5");

        Assert.Equal(120, page.TotalInWindow);
        Assert.Equal(Noon.AddMinutes(239), page.Readings[0].TimestampUtc);
    }

    [Fact]
    public async Task Series_SlicesTheWindowAndCountsEveryReadingOnce()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await SeedAsync(http);

        var series = await Gateway.GetAsync<HistorySeriesResponse>(http,
            $"api/history/{Probe}/series?fromUtc={Iso(Noon)}&toUtc={Iso(Noon.AddHours(4))}&buckets=16");

        Assert.Equal(16, series.Buckets.Count);
        Assert.Equal(900, series.BucketSeconds);
        Assert.All(series.Buckets, bucket => Assert.Equal(15, bucket.Count));
        Assert.Equal(240, series.Buckets.Sum(bucket => bucket.Count));
    }

    [Fact]
    public async Task Bounds_DescribeTheTree()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await SeedAsync(http);

        var bounds = await Gateway.GetAsync<HistoryBoundsResponse>(http, $"api/history/{Probe}/bounds");

        Assert.Equal(240, bounds.Count);
        Assert.Equal(Noon, bounds.OldestUtc);
        Assert.Equal(Noon.AddMinutes(239), bounds.NewestUtc);
        Assert.InRange(bounds.TreeHeight, 8, 10);
        Assert.Equal(48, bounds.RetentionHours);
    }

    [Fact]
    public async Task Recent_ReturnsTheNewestReadingsFirst()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await SeedAsync(http);

        var recent = await Gateway.GetAsync<List<HistoryReadingResponse>>(http, $"api/history/{Probe}/recent?count=3");

        Assert.Equal([239d, 238d, 237d], recent.Select(reading => reading.Value));
        Assert.EndsWith("%", recent[0].Formatted);
    }

    [Fact]
    public async Task InvertedWindow_IsRejected()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await SeedAsync(http);

        var response = await http.GetAsync($"api/history/{Probe}?fromUtc={Iso(Noon.AddHours(2))}&toUtc={Iso(Noon)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownNode_IsNotFound()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();

        var response = await http.GetAsync("api/history/A4:CF:12:FF:FF:FF");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task SeedAsync(HttpClient http)
    {
        await Gateway.RegisterAsync(http, Probe, "History Probe", "Environmental", "Percent");

        var response = await http.PostAsJsonAsync("api/telemetry/batch", new TelemetryBatchRequest
        {
            MacAddress = Probe,
            PayloadType = "float",
            Values = Enumerable.Range(0, 240).Select(minute => minute.ToString(CultureInfo.InvariantCulture)).ToList(),
            StartedAtUtc = Noon,
            IntervalSeconds = 60
        }, Gateway.Json);

        response.EnsureSuccessStatusCode();
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("o", CultureInfo.InvariantCulture));
}
