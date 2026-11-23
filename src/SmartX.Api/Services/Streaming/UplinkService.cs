using System.Diagnostics;
using SmartX.Contracts.Stream;
using SmartX.Core.Devices;
using SmartX.Core.Models;
using SmartX.Core.Streaming;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// The door every live packet comes through: find the device, build the typed
/// packet, triage it, and put it in the right lane.
/// <para>
/// <b>The lookup.</b> Each packet names its device by MAC address. The address is
/// parsed once into a <see cref="MacKey"/> and looked up in the stream's
/// <c>Dictionary&lt;MacKey, DeviceState&gt;</c> - one hash, one bucket, one
/// comparison. The time each lookup takes is measured and returned, so the claim
/// that it stays flat as the fleet grows can be checked against a real response.
/// </para>
/// <para>
/// <b>Triage.</b> The device's running baseline comes back from the same lookup,
/// so deciding the lane needs no history at all. Critical packets go to the
/// priority heap; everything else goes to the back of the FIFO queue. Nothing is
/// processed here - the request returns as soon as every packet has a place in a
/// lane, which is what keeps the uplink fast while the worker catches up behind it.
/// </para>
/// </summary>
public sealed class UplinkService(
    CommandStreamStore stream,
    DualLaneDispatchQueue<QueuedPacket> queue)
{
    /// <summary>How the device lookup is described in responses.</summary>
    public const string LookupStrategy = "Dictionary<MacKey, DeviceState> - O(1)";

    /// <summary>Triages and queues every packet in a frame.</summary>
    public UplinkEnvelopeResponse Accept(UplinkEnvelopeRequest envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var now = DateTimeOffset.UtcNow;
        var bands = stream.Options.Bands;
        var receipts = new List<UplinkReceiptResponse>(envelope.Packets.Count);

        // Resolve every packet's device under one acquisition of the lock, timing
        // just the dictionary lookups inside it.
        var (devices, lookupTicks, fleetSize) = stream.Read(state =>
        {
            var found = new DeviceSnapshot?[envelope.Packets.Count];
            var ticks = 0L;

            for (var i = 0; i < envelope.Packets.Count; i++)
            {
                if (!MacKey.TryParse(envelope.Packets[i].MacAddress, out var mac))
                {
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                var hit = state.Devices.TryGet(mac, out var device);
                ticks += Stopwatch.GetTimestamp() - started;

                if (hit)
                {
                    found[i] = new DeviceSnapshot(mac, device.SensorId, device.Category, device.Unit, device.Baseline);
                }
            }

            return (found, ticks, state.Devices.Count);
        });

        var priority = 0;

        for (var i = 0; i < envelope.Packets.Count; i++)
        {
            var request = envelope.Packets[i];

            if (devices[i] is not { } device)
            {
                receipts.Add(Refused(request, "No device is registered under this address."));
                continue;
            }

            if (!TelemetryIngestionService.TryBuildPacket(
                    device.SensorId,
                    device.Mac.ToString(),
                    device.Unit,
                    request.PayloadType,
                    request.Value,
                    request.SequenceNumber,
                    request.TimestampUtc ?? now,
                    out var packet))
            {
                receipts.Add(Refused(request, $"'{request.Value}' could not be read as {request.PayloadType}."));
                continue;
            }

            var triage = CriticalBandClassifier.Classify(
                device.Category, device.Unit, packet.NumericValue, device.Baseline, request.Alarm, bands);

            var item = new QueuedPacket(device.Mac, device.SensorId, packet, triage, now);
            var ticket = triage.IsUrgent
                ? queue.EnqueuePriority(item, triage.Severity)
                : queue.Enqueue(item);

            if (ticket.Accepted && triage.IsUrgent)
            {
                priority++;
            }

            receipts.Add(new UplinkReceiptResponse
            {
                MacAddress = device.Mac.ToString(),
                Accepted = ticket.Accepted,
                Lane = ticket.Lane.ToString(),
                Sequence = ticket.Sequence,
                Position = ticket.Position,
                StandardWaiting = ticket.StandardWaiting,
                Reason = ticket.Accepted
                    ? triage.Reason
                    : "The standard lane is full; routine telemetry is being shed. Retry shortly."
            });
        }

        var lookups = devices.Count(device => device is not null);

        return new UplinkEnvelopeResponse
        {
            Received = envelope.Packets.Count,
            Accepted = receipts.Count(receipt => receipt.Accepted),
            Priority = priority,
            Rejected = receipts.Count(receipt => !receipt.Accepted),
            LookupStrategy = LookupStrategy,
            MeanLookupNanoseconds = lookups == 0
                ? 0d
                : Math.Round(lookupTicks * (1_000_000_000d / Stopwatch.Frequency) / lookups, 1),
            FleetSize = fleetSize,
            Receipts = receipts
        };
    }

    private static UplinkReceiptResponse Refused(UplinkPacketRequest request, string reason) => new()
    {
        MacAddress = request.MacAddress,
        Accepted = false,
        Lane = nameof(DispatchLane.Standard),
        Reason = reason
    };

    private readonly record struct DeviceSnapshot(
        MacKey Mac,
        Guid SensorId,
        SensorCategory Category,
        TelemetryUnit Unit,
        double? Baseline);
}
