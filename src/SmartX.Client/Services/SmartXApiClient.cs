using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Forms;
using SmartX.Contracts.Attachments;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Deployment;
using SmartX.Contracts.Insights;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Stream;
using SmartX.Contracts.Telemetry;
using SmartX.Contracts.Topology;
using SmartX.Contracts.History;

namespace SmartX.Client.Services;

/// <summary>
/// The console's only connection to the gateway.
/// <para>
/// <b>Everything here is async end to end.</b> Blazor WebAssembly runs on the
/// browser's single UI thread, so a blocking <c>.Result</c> or <c>.Wait()</c> would
/// not merely waste a thread - it would freeze the whole page, or deadlock outright,
/// because the continuation it waits on needs that same thread. Awaiting yields
/// back to the browser, so polling and typing stay responsive during a round trip.
/// </para>
/// <para>
/// <b>Nothing throws.</b> Every method returns an <see cref="ApiResult{T}"/>, so an
/// unreachable gateway renders as a banner and a rejected form renders as
/// field-level messages, rather than either becoming an unhandled error.
/// </para>
/// <para>
/// <b>Reachability is reported centrally.</b> Each call tells
/// <see cref="GatewayStatus"/> whether the gateway answered, which is what drives
/// the offline banner in the layout.
/// </para>
/// </summary>
public sealed class SmartXApiClient(HttpClient http, GatewayStatus status, ILogger<SmartXApiClient> logger)
{
    /// <summary>
    /// Matches the gateway's request depth limit. The serializer default of 64 would
    /// otherwise refuse to send a deep deployment profile at all, so the operator
    /// never saw the gateway's own MAX_DEPTH finding for it.
    /// </summary>
    public const int MaxJsonDepth = 128;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = MaxJsonDepth };

    /// <summary>
    /// The gateway origin, without a trailing slash. Attachment downloads link here
    /// directly, because the gateway - not this client - serves those bytes.
    /// </summary>
    public string BaseUrl => http.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty;

    // -----------------------------------------------------------------
    // Gateway
    // -----------------------------------------------------------------

    /// <summary>
    /// Liveness check against <c>/api/health</c>. Used by the layout to notice when
    /// an unreachable gateway comes back, so the operator does not have to.
    /// </summary>
    public async Task<bool> IsGatewayReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync("api/health", cancellationToken);
            status.Report(reachable: true);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            status.Report(reachable: false);
            return false;
        }
    }

    // -----------------------------------------------------------------
    // Sensors
    // -----------------------------------------------------------------

    /// <summary>Lists registered nodes, optionally filtered by category.</summary>
    public Task<ApiResult<List<SensorResponse>>> ListSensorsAsync(
        string? category = null,
        CancellationToken cancellationToken = default)
        => GetAsync<List<SensorResponse>>(
            string.IsNullOrWhiteSpace(category) ? "api/sensors" : $"api/sensors?category={Uri.EscapeDataString(category)}",
            cancellationToken);

    /// <summary>Fetches one node by its gateway-assigned key.</summary>
    public Task<ApiResult<SensorResponse>> GetSensorAsync(Guid id, CancellationToken cancellationToken = default)
        => GetAsync<SensorResponse>($"api/sensors/{id}", cancellationToken);

    /// <summary>Fetches one node by MAC address, via the gateway binary search.</summary>
    public Task<ApiResult<SensorResponse>> GetSensorByMacAsync(
        string mac,
        CancellationToken cancellationToken = default)
        => GetAsync<SensorResponse>($"api/sensors/by-mac/{Uri.EscapeDataString(mac)}", cancellationToken);

    /// <summary>Free-text search across the registry.</summary>
    public Task<ApiResult<SensorSearchResponse>> SearchSensorsAsync(
        string? query,
        CancellationToken cancellationToken = default)
        => GetAsync<SensorSearchResponse>(
            $"api/sensors/search?q={Uri.EscapeDataString(query ?? string.Empty)}",
            cancellationToken);

    /// <summary>Registers a node.</summary>
    public Task<ApiResult<SensorResponse>> RegisterSensorAsync(
        RegisterSensorRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync<RegisterSensorRequest, SensorResponse>("api/sensors", request, cancellationToken);

    // -----------------------------------------------------------------
    // Telemetry
    // -----------------------------------------------------------------

    /// <summary>Publishes a single reading.</summary>
    public Task<ApiResult<IngestionResultResponse>> IngestAsync(
        TelemetryIngestRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync<TelemetryIngestRequest, IngestionResultResponse>("api/telemetry", request, cancellationToken);

    /// <summary>Publishes a batch of readings.</summary>
    public Task<ApiResult<IngestionResultResponse>> IngestBatchAsync(
        TelemetryBatchRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync<TelemetryBatchRequest, IngestionResultResponse>(
            "api/telemetry/batch", request, cancellationToken);

    /// <summary>Asks the gateway to synthesise readings for a node.</summary>
    public Task<ApiResult<IngestionResultResponse>> GenerateAsync(
        GenerateTelemetryRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync<GenerateTelemetryRequest, IngestionResultResponse>(
            "api/telemetry/generate", request, cancellationToken);

    /// <summary>A node's telemetry history with its aggregates.</summary>
    public Task<ApiResult<TelemetryHistoryResponse>> GetHistoryAsync(
        Guid sensorId,
        int take = 120,
        CancellationToken cancellationToken = default)
        => GetAsync<TelemetryHistoryResponse>($"api/telemetry/{sensorId}?take={take}", cancellationToken);

    // -----------------------------------------------------------------
    // Attachments
    // -----------------------------------------------------------------

    /// <summary>Files attached to a sensor profile.</summary>
    public Task<ApiResult<List<AttachmentResponse>>> ListAttachmentsAsync(
        Guid sensorId,
        CancellationToken cancellationToken = default)
        => GetAsync<List<AttachmentResponse>>($"api/sensors/{sensorId}/attachments", cancellationToken);

    /// <summary>The upload limits the gateway enforces.</summary>
    public Task<ApiResult<AttachmentPolicyResponse>> GetAttachmentPolicyAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<AttachmentPolicyResponse>("api/attachments/policy", cancellationToken);

    /// <summary>
    /// Sends a file chosen in the browser to the gateway as multipart form data.
    /// <para>
    /// The file is read through <see cref="IBrowserFile.OpenReadStream"/> with the
    /// gateway's own size cap, so an oversized file is refused here before a single
    /// byte is sent, rather than uploaded in full only to be rejected.
    /// </para>
    /// </summary>
    public async Task<ApiResult<AttachmentResponse>> UploadAttachmentAsync(
        Guid sensorId,
        IBrowserFile file,
        long maxAllowedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            using var content = new MultipartFormDataContent();
            await using var stream = file.OpenReadStream(maxAllowedBytes, cancellationToken);

            var streamContent = new StreamContent(stream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);

            // The field name must be "file" - it is what the gateway parameter binds to.
            content.Add(streamContent, "file", file.Name);

            using var response = await http.PostAsync(
                $"api/sensors/{sensorId}/attachments", content, cancellationToken);

            return await ReadAsync<AttachmentResponse>(response, cancellationToken);
        }
        catch (IOException error)
        {
            // Raised by OpenReadStream when the file exceeds the cap. That is the
            // operator's choice of file, not a gateway fault, so the banner stays off.
            return ApiResult<AttachmentResponse>.Fail(400, "That file is too large.", error.Message);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return Unreachable<AttachmentResponse>(error, "POST attachment");
        }
    }

    // -----------------------------------------------------------------
    // Dashboard
    // -----------------------------------------------------------------

    /// <summary>Fleet-wide statistics.</summary>
    public Task<ApiResult<DashboardStatsResponse>> GetStatsAsync(CancellationToken cancellationToken = default)
        => GetAsync<DashboardStatsResponse>("api/dashboard/stats", cancellationToken);

    /// <summary>Heartbeat strips, ordered with the nodes needing attention first.</summary>
    public Task<ApiResult<List<HeartbeatStripResponse>>> GetHeartbeatsAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<List<HeartbeatStripResponse>>("api/dashboard/heartbeats", cancellationToken);

    /// <summary>The composite health score and its badges.</summary>
    public Task<ApiResult<GatewayHealthResponse>> GetGatewayHealthAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<GatewayHealthResponse>("api/dashboard/health", cancellationToken);

    /// <summary>The anomaly triage queue.</summary>
    public Task<ApiResult<List<AnomalyResponse>>> GetAnomaliesAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
        => GetAsync<List<AnomalyResponse>>($"api/dashboard/anomalies?limit={limit}", cancellationToken);

    /// <summary>Clears one anomaly and returns the recomputed health score.</summary>
    public Task<ApiResult<GatewayHealthResponse>> AcknowledgeAnomalyAsync(
        Guid anomalyId,
        CancellationToken cancellationToken = default)
        => PostAsync<object, GatewayHealthResponse>(
            $"api/dashboard/anomalies/{anomalyId}/acknowledge", new { }, cancellationToken);

    // -----------------------------------------------------------------
    // Deployment
    // -----------------------------------------------------------------

    /// <summary>A worked example profile to populate the editor with.</summary>
    public Task<ApiResult<ValidateDeploymentRequest>> GetExampleDeploymentAsync(
        CancellationToken cancellationToken = default)
        => GetAsync<ValidateDeploymentRequest>("api/deployment/example", cancellationToken);

    /// <summary>Validates a deployment profile by recursive descent.</summary>
    public Task<ApiResult<ValidateDeploymentResponse>> ValidateDeploymentAsync(
        ValidateDeploymentRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync<ValidateDeploymentRequest, ValidateDeploymentResponse>(
            "api/deployment/validate", request, cancellationToken);

    // -----------------------------------------------------------------
    // Part 2 - command stream
    // -----------------------------------------------------------------

    /// <summary>Everything the Command Stream page needs to draw itself, in one request.</summary>
    public Task<ApiResult<StreamSnapshotResponse>> GetStreamSnapshotAsync(
        StreamFilter filter,
        CancellationToken cancellationToken = default)
        => GetAsync<StreamSnapshotResponse>($"api/stream/snapshot{filter.ToQuery()}", cancellationToken);

    /// <summary>The device grid, filtered by set intersection on the gateway.</summary>
    public Task<ApiResult<DeviceFilterResponse>> FilterDevicesAsync(
        StreamFilter filter,
        CancellationToken cancellationToken = default)
        => GetAsync<DeviceFilterResponse>($"api/stream/devices{filter.ToQuery()}", cancellationToken);

    /// <summary>A device's per-minute timeline.</summary>
    public Task<ApiResult<TimelineResponse>> GetTimelineAsync(
        string mac,
        int minutes,
        CancellationToken cancellationToken = default)
        => GetAsync<TimelineResponse>($"api/stream/devices/{Uri.EscapeDataString(mac)}/timeline?minutes={minutes}", cancellationToken);

    /// <summary>The event history, newest first.</summary>
    public Task<ApiResult<List<StreamEventResponse>>> GetStreamHistoryAsync(
        int take,
        string? kind,
        string? level,
        string? mac,
        DateTimeOffset? fromUtc,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"take={take}" };
        AddIf(query, "kind", kind);
        AddIf(query, "level", level);
        AddIf(query, "mac", mac);
        AddIf(query, "fromUtc", fromUtc?.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

        return GetAsync<List<StreamEventResponse>>($"api/stream/history?{string.Join('&', query)}", cancellationToken);
    }

    /// <summary>Injects a critical packet through the normal uplink.</summary>
    public Task<ApiResult<TestInjectionResponse>> SimulateCriticalAsync(
        string kind,
        CancellationToken cancellationToken = default)
        => PostAsync<SimulateCriticalRequest, TestInjectionResponse>(
            "api/stream/test/critical", new SimulateCriticalRequest { Kind = kind }, cancellationToken);

    /// <summary>Floods the standard lane with routine packets.</summary>
    public Task<ApiResult<TestInjectionResponse>> FloodAsync(int count, CancellationToken cancellationToken = default)
        => PostAsync<FloodRequest, TestInjectionResponse>(
            "api/stream/test/flood", new FloodRequest { Count = count }, cancellationToken);

    /// <summary>Switches one or more actuators.</summary>
    public Task<ApiResult<CommandOutcomeResponse>> IssueCommandAsync(
        IssueCommandRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync<IssueCommandRequest, CommandOutcomeResponse>("api/commands", request, cancellationToken);

    /// <summary>Reverts the most recent command.</summary>
    public Task<ApiResult<CommandOutcomeResponse>> UndoCommandAsync(CancellationToken cancellationToken = default)
        => PostAsync<object, CommandOutcomeResponse>("api/commands/undo", new { }, cancellationToken);

    /// <summary>Re-applies the most recently undone command.</summary>
    public Task<ApiResult<CommandOutcomeResponse>> RedoCommandAsync(CancellationToken cancellationToken = default)
        => PostAsync<object, CommandOutcomeResponse>("api/commands/redo", new { }, cancellationToken);

    /// <summary>Records a search or a device being opened, for the predictive engine.</summary>
    public Task<ApiResult<InsightsResponse>> RecordActivityAsync(
        RecordActivityRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync<RecordActivityRequest, InsightsResponse>("api/insights/activity", request, cancellationToken);

    /// <summary>Accepts a suggestion - for an action, runs its command.</summary>
    public Task<ApiResult<SuggestionAcceptedResponse>> AcceptSuggestionAsync(
        string key,
        CancellationToken cancellationToken = default)
        => PostAsync<SuggestionKeyRequest, SuggestionAcceptedResponse>(
            "api/insights/accept", new SuggestionKeyRequest { Key = key }, cancellationToken);

    /// <summary>Hides a suggestion for a while.</summary>
    public Task<ApiResult<InsightsResponse>> DismissSuggestionAsync(
        string key,
        CancellationToken cancellationToken = default)
        => PostAsync<SuggestionKeyRequest, InsightsResponse>(
            "api/insights/dismiss", new SuggestionKeyRequest { Key = key }, cancellationToken);

    /// <summary>Everything the predictive engine has learned.</summary>
    public Task<ApiResult<List<PatternRuleResponse>>> GetPatternsAsync(CancellationToken cancellationToken = default)
        => GetAsync<List<PatternRuleResponse>>("api/insights/patterns?minSupport=1&minConfidence=0", cancellationToken);

    // -----------------------------------------------------------------
    // Final PoE - topology and history
    // -----------------------------------------------------------------

    /// <summary>The whole mesh: nodes, links, the spanning tree and the healing report.</summary>
    public Task<ApiResult<TopologySnapshotResponse>> GetTopologyAsync(string algorithm, CancellationToken cancellationToken = default)
        => GetAsync<TopologySnapshotResponse>($"api/topology?algorithm={Uri.EscapeDataString(algorithm)}", cancellationToken);

    /// <summary>One node's routes to the gateway and the subnet behind it.</summary>
    public Task<ApiResult<RouteTraceResponse>> TraceRouteAsync(string mac, string algorithm, CancellationToken cancellationToken = default)
        => GetAsync<RouteTraceResponse>($"api/topology/route/{Uri.EscapeDataString(mac)}?algorithm={Uri.EscapeDataString(algorithm)}", cancellationToken);

    /// <summary>Breadth-first search from any node.</summary>
    public Task<ApiResult<ReachabilityResponse>> GetReachabilityAsync(string mac, CancellationToken cancellationToken = default)
        => GetAsync<ReachabilityResponse>($"api/topology/nodes/{Uri.EscapeDataString(mac)}/reachability", cancellationToken);

    /// <summary>Fails a node from the console, or restores it.</summary>
    public Task<ApiResult<SimulatedFailureResponse>> SetSimulatedFailureAsync(string mac, bool failed, CancellationToken cancellationToken = default)
    {
        var path = $"api/topology/nodes/{Uri.EscapeDataString(mac)}/failure";
        return failed
            ? PostAsync<object, SimulatedFailureResponse>(path, new { }, cancellationToken)
            : SendAsync<SimulatedFailureResponse>(HttpMethod.Delete, path, cancellationToken);
    }

    /// <summary>One page of a node's readings in a time window, from its AVL tree.</summary>
    public Task<ApiResult<HistoryPageResponse>> GetHistoryPageAsync(
        string mac,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int page,
        int pageSize,
        bool newestFirst,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}", $"order={(newestFirst ? "newest" : "oldest")}" };
        AddIf(query, "fromUtc", fromUtc?.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        AddIf(query, "toUtc", toUtc?.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        return GetAsync<HistoryPageResponse>($"api/history/{Uri.EscapeDataString(mac)}?{string.Join('&', query)}", cancellationToken);
    }

    /// <summary>A time window cut into chart slices.</summary>
    public Task<ApiResult<HistorySeriesResponse>> GetHistorySeriesAsync(
        string mac,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int buckets,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"buckets={buckets}" };
        AddIf(query, "fromUtc", fromUtc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        AddIf(query, "toUtc", toUtc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        return GetAsync<HistorySeriesResponse>($"api/history/{Uri.EscapeDataString(mac)}/series?{string.Join('&', query)}", cancellationToken);
    }

    /// <summary>The oldest and newest readings a node's tree holds.</summary>
    public Task<ApiResult<HistoryBoundsResponse>> GetHistoryBoundsAsync(string mac, CancellationToken cancellationToken = default)
        => GetAsync<HistoryBoundsResponse>($"api/history/{Uri.EscapeDataString(mac)}/bounds", cancellationToken);

    /// <summary>A node's newest readings, newest first.</summary>
    public Task<ApiResult<List<HistoryReadingResponse>>> GetRecentReadingsAsync(string mac, int count, CancellationToken cancellationToken = default)
        => GetAsync<List<HistoryReadingResponse>>($"api/history/{Uri.EscapeDataString(mac)}/recent?count={count}", cancellationToken);

    private static void AddIf(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    // -----------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------

    private async Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            return await ReadAsync<T>(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up - usually a page the operator navigated away from.
            // That says nothing about the gateway, so the offline banner stays off.
            return ApiResult<T>.Fail(0, "The request was cancelled.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return Unreachable<T>(error, $"GET {path}");
        }
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await http.SendAsync(request, cancellationToken);
            return await ReadAsync<T>(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResult<T>.Fail(0, "The request was cancelled.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return Unreachable<T>(error, $"{method} {path}");
        }
    }

    private async Task<ApiResult<TResponse>> PostAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, body, Json, cancellationToken);
            return await ReadAsync<TResponse>(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResult<TResponse>.Fail(0, "The request was cancelled.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return Unreachable<TResponse>(error, $"POST {path}");
        }
    }

    /// <summary>
    /// Turns a response into a result, unpacking an RFC 7807 problem document when
    /// the gateway refused - including the field-level error dictionary, which is
    /// what lets a rejected form show its messages beside the right inputs.
    /// </summary>
    private async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Any HTTP response at all, even a 500, proves the gateway is up.
        status.Report(reachable: true);

        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);

            return value is null
                ? ApiResult<T>.Fail((int)response.StatusCode, "The gateway returned an empty response.")
                : ApiResult<T>.Ok(value, (int)response.StatusCode);
        }

        var code = (int)response.StatusCode;

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDocument>(Json, cancellationToken);

            if (problem is not null)
            {
                return ApiResult<T>.Fail(
                    code,
                    problem.Title ?? response.ReasonPhrase ?? "The gateway refused the request.",
                    problem.Detail,
                    problem.Errors);
            }
        }
        catch (JsonException)
        {
            // Not a problem document. Fall through to the status-only result below;
            // there is nothing more useful to show the operator either way.
        }

        return ApiResult<T>.Fail(code, response.ReasonPhrase ?? $"The gateway returned {code}.");
    }

    private ApiResult<T> Unreachable<T>(Exception error, string operation)
    {
        logger.LogWarning(error, "Gateway call failed: {Operation}.", operation);
        status.Report(reachable: false);

        return ApiResult<T>.Unreachable(
            "Could not reach the Smart-X gateway. Confirm the API is running and that " +
            "SmartX:ApiBaseUrl in wwwroot/appsettings.json points at it.");
    }

    /// <summary>The subset of RFC 7807 this client reads back.</summary>
    private sealed class ProblemDocument
    {
        public string? Title { get; set; }

        public string? Detail { get; set; }

        public Dictionary<string, string[]>? Errors { get; set; }
    }
}
