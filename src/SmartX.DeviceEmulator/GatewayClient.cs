using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartX.Contracts.Stream;
using SmartX.Contracts.Topology;

namespace SmartX.DeviceEmulator;

/// <summary>
/// The emulator's HTTP link to the gateway - the same four calls a real node's
/// firmware would make: provision, publish, collect commands, acknowledge.
/// </summary>
public sealed class GatewayClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The gateway origin.</summary>
    public Uri? BaseAddress => http.BaseAddress;

    /// <summary>Whether the gateway answers its liveness probe.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("api/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// When the running gateway started, from its liveness probe, or null when it
    /// cannot be reached. A different value from last time means the gateway has
    /// restarted with a fresh history and the fleet must be provisioned again.
    /// </summary>
    public async Task<DateTimeOffset?> StartedAtAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("api/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var health = await response.Content.ReadFromJsonAsync<HealthProbe>(Json, cancellationToken);
            return health?.StartedAtUtc;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The fleet the gateway has provisioned for the emulator.</summary>
    public Task<DeviceManifestResponse?> ManifestAsync(CancellationToken cancellationToken)
        => http.GetFromJsonAsync<DeviceManifestResponse>("api/devices/manifest", Json, cancellationToken);

    /// <summary>Publishes a frame of readings.</summary>
    public async Task<UplinkEnvelopeResponse?> PublishAsync(UplinkEnvelopeRequest envelope, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/devices/uplink", envelope, Json, cancellationToken);

        // 422 and 503 still carry the receipts explaining what happened.
        return await response.Content.ReadFromJsonAsync<UplinkEnvelopeResponse>(Json, cancellationToken);
    }

    /// <summary>Posts the fleet's neighbour tables, from which the gateway refines its mesh graph.</summary>
    public async Task<NeighbourEnvelopeResponse?> ReportNeighboursAsync(NeighbourEnvelopeRequest envelope, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/devices/neighbours", envelope, Json, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<NeighbourEnvelopeResponse>(Json, cancellationToken)
            : null;
    }

    /// <summary>Collects the commands waiting for the given nodes.</summary>
    public async Task<DownlinkResponse?> CollectAsync(IReadOnlyCollection<string> macs, CancellationToken cancellationToken)
    {
        if (macs.Count == 0)
        {
            return new DownlinkResponse();
        }

        var query = string.Join('&', macs.Select(mac => $"mac={Uri.EscapeDataString(mac)}"));
        return await http.GetFromJsonAsync<DownlinkResponse>($"api/devices/downlink?{query}", Json, cancellationToken);
    }

    private sealed record HealthProbe(DateTimeOffset StartedAtUtc);

    /// <summary>Confirms a command was applied.</summary>
    public async Task<bool> AcknowledgeAsync(Guid commandId, bool appliedState, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/devices/downlink/{commandId}/ack", new CommandAckRequest { AppliedState = appliedState }, Json, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
