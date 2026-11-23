using SmartX.Core.Devices;
using SmartX.Core.Streaming;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// A packet waiting in the dispatch queue.
/// <para>
/// The payload is already a closed <c>TelemetryPacket&lt;T&gt;</c> by the time it
/// is queued - the generic wrapper from Part 1 is built once, at the door, and it is
/// that strongly-typed packet that travels through the <c>Queue&lt;T&gt;</c> or the
/// priority heap. Triage is decided at the door too, so the worker never has to
/// classify anything twice.
/// </para>
/// </summary>
/// <param name="Mac">The device, as its registry key.</param>
/// <param name="SensorId">The device's Part 1 sensor key.</param>
/// <param name="Packet">The typed reading.</param>
/// <param name="Triage">Which lane it was given and why.</param>
/// <param name="ReceivedUtc">When the gateway received it.</param>
public sealed record QueuedPacket(
    MacKey Mac,
    Guid SensorId,
    ITelemetryPacket Packet,
    TriageResult Triage,
    DateTimeOffset ReceivedUtc);
