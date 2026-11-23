using System.Globalization;
using SmartX.Contracts.Stream;
using SmartX.Core.Devices;
using SmartX.Core.Models;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// Test injections for the console's Test tools panel and the README walkthrough.
/// <para>
/// Both build an ordinary uplink frame and pass it through
/// <see cref="UplinkService"/> - the same door the device emulator uses. Nothing
/// here chooses a lane: a simulated moisture crash goes to the priority lane
/// because 7% is below the 20% floor, not because a test said so.
/// </para>
/// </summary>
public sealed class StreamTestTools(CommandStreamStore store, UplinkService uplink)
{
    /// <summary>Injects one critical packet.</summary>
    /// <returns>Null when no suitable device is registered.</returns>
    public TestInjectionResponse? InjectCritical(SimulateCriticalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bands = store.Options.Bands;

        var packet = store.Read(state =>
        {
            DeviceState? device = request.Kind switch
            {
                "MoistureCrash" => Pick(state, request.MacAddress, SensorCategory.Environmental, "Soil Moisture Probe 01", "Moisture"),
                "LinkLoss" => Pick(state, request.MacAddress, SensorCategory.Connectivity, null, null),
                _ => Pick(state, request.MacAddress, SensorCategory.PowerConsumption, "Lighting Rail Meter 13", "Lighting")
            };

            if (device is null)
            {
                return null;
            }

            var (payloadType, value) = request.Kind switch
            {
                "MoistureCrash" => ("float", "7.0"),
                "LinkLoss" => ("int", "-94"),
                _ => ("int", Math.Round(Math.Max(bands.PowerCeilingWatts + 300d, (device.Baseline ?? 820d) * 3.4d))
                    .ToString(CultureInfo.InvariantCulture))
            };

            return new UplinkPacketRequest
            {
                MacAddress = device.Mac.ToString(),
                PayloadType = payloadType,
                Value = value,
                SequenceNumber = device.PacketsObserved + 1,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        });

        if (packet is null)
        {
            return null;
        }

        var response = uplink.Accept(new UplinkEnvelopeRequest { GatewayId = "test-tools", Packets = [packet] });
        var receipt = response.Receipts[0];

        return new TestInjectionResponse
        {
            Message = receipt.Accepted
                ? $"Injected {packet.Value} for {packet.MacAddress}: triaged into the {receipt.Lane} lane " +
                  $"ahead of {receipt.StandardWaiting} waiting packet{(receipt.StandardWaiting == 1 ? string.Empty : "s")}."
                : $"Refused: {receipt.Reason}",
            Uplink = response
        };
    }

    /// <summary>
    /// Floods the standard lane with routine readings close to each device's
    /// current value, so they are unremarkable to every detector - the point is the
    /// backlog, not the data.
    /// </summary>
    public TestInjectionResponse Flood(int count)
    {
        var random = new Random();
        var now = DateTimeOffset.UtcNow;

        var packets = store.Read(state =>
        {
            var live = state.Devices.All
                .Where(device => device.LatestValue is not null)
                .OrderBy(device => device.Mac)
                .ToList();

            var built = new List<UplinkPacketRequest>(count);
            if (live.Count == 0)
            {
                return built;
            }

            for (var i = 0; i < count; i++)
            {
                var device = live[i % live.Count];
                var latest = device.LatestValue!.Value;

                var (payloadType, value) = device.Category switch
                {
                    SensorCategory.Actuator => ("bool", latest > 0.5d ? "true" : "false"),
                    SensorCategory.PowerConsumption or SensorCategory.Connectivity
                        => ("int", ((int)Math.Round(latest + random.Next(-2, 3))).ToString(CultureInfo.InvariantCulture)),
                    _ => ("float", (latest + ((random.NextDouble() - 0.5d) * 0.4d)).ToString("0.###", CultureInfo.InvariantCulture))
                };

                built.Add(new UplinkPacketRequest
                {
                    MacAddress = device.Mac.ToString(),
                    PayloadType = payloadType,
                    Value = value,
                    SequenceNumber = i,
                    TimestampUtc = now
                });
            }

            return built;
        });

        if (packets.Count == 0)
        {
            return new TestInjectionResponse { Message = "No device has reported yet, so there is nothing realistic to flood with." };
        }

        var response = uplink.Accept(new UplinkEnvelopeRequest { GatewayId = "test-tools", Packets = packets });

        return new TestInjectionResponse
        {
            Message = $"Queued {response.Accepted} routine packets in the standard lane" +
                      (response.Rejected > 0 ? $"; {response.Rejected} were shed because the lane was full." : ".") +
                      $" At {store.Options.SimulatedProcessingMs} ms each the backlog takes a while to drain - inject a critical alert now to watch it jump the queue.",
            Uplink = response
        };
    }

    private static DeviceState? Pick(StreamState state, string? mac, SensorCategory category, string? preferred, string? nameHint)
    {
        if (!string.IsNullOrWhiteSpace(mac))
        {
            return MacKey.TryParse(mac, out var key) && state.Devices.TryGet(key, out var named) && named.Category == category
                ? named
                : null;
        }

        var candidates = state.Devices.Query(new DeviceQuery(category), out _);

        return candidates.FirstOrDefault(device => string.Equals(device.DisplayName, preferred, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(device => nameHint is not null && device.DisplayName.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }
}
