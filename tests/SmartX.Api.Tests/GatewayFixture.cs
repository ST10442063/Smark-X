using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Stream;

namespace SmartX.Api.Tests;

/// <summary>
/// Boots the gateway in-process. By default with no seeded fleet, so each test
/// registers exactly the devices it needs and nothing else is publishing; the
/// topology tests ask for the seeded site, because its layout is what they check.
/// </summary>
public sealed class GatewayFactory(int processingMs = 0, bool seed = false) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Gateway:SeedOnStartup", seed ? "true" : "false");

        // A fixed throwaway key, so the tests never touch the key file on disk.
        builder.UseSetting("Gateway:AttachmentEncryptionKey", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Gateway:UploadRoot", Path.Combine(Path.GetTempPath(), "smartx-tests", "attachments"));
        builder.UseSetting("Stream:SimulatedProcessingMs", processingMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Stream:SeedOperatorHistory", "false");
        builder.UseSetting("Stream:CommandTimeoutSeconds", "2");
    }
}

/// <summary>HTTP helpers shared by the tests.</summary>
public static class Gateway
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Registers a node through the Part 1 registration endpoint.</summary>
    public static async Task RegisterAsync(HttpClient http, string mac, string name, string category, string unit, int heartbeatSeconds = 60)
    {
        var response = await http.PostAsJsonAsync("api/sensors", new RegisterSensorRequest
        {
            MacAddress = mac,
            DisplayName = name,
            Category = category,
            Unit = unit,
            Facility = "Facility A",
            Zone = "Zone 1",
            SubZone = "Sub-Zone A",
            NodeId = "NODE-T1",
            FirmwareVersion = "1.4.2",
            HeartbeatIntervalSeconds = heartbeatSeconds
        }, Json);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Publishes one frame through the device uplink.</summary>
    public static async Task<UplinkEnvelopeResponse> PublishAsync(HttpClient http, params UplinkPacketRequest[] packets)
    {
        var response = await http.PostAsJsonAsync("api/devices/uplink", new UplinkEnvelopeRequest { GatewayId = "tests", Packets = [.. packets] }, Json);
        return (await response.Content.ReadFromJsonAsync<UplinkEnvelopeResponse>(Json))!;
    }

    /// <summary>A packet with the given value, captured now.</summary>
    public static UplinkPacketRequest Packet(string mac, string payloadType, string value) => new()
    {
        MacAddress = mac,
        PayloadType = payloadType,
        Value = value,
        TimestampUtc = DateTimeOffset.UtcNow
    };

    public static async Task<T> GetAsync<T>(HttpClient http, string path)
        => (await http.GetFromJsonAsync<T>(path, Json))!;

    /// <summary>The live state of one device.</summary>
    public static async Task<DeviceLiveResponse> DeviceAsync(HttpClient http, string mac)
    {
        var grid = await GetAsync<DeviceFilterResponse>(http, $"api/stream/devices?q={Uri.EscapeDataString(mac)}");
        return grid.Devices.Single(device => device.MacAddress == mac);
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds. The dispatch worker processes
    /// packets in the background, so tests wait for the outcome rather than sleeping
    /// for a guessed interval.
    /// </summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting until {because}.");
    }
}
