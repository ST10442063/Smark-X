using SmartX.Core.Models;

namespace SmartX.Core.Telemetry;

/// <summary>
/// Non-generic view over a telemetry packet.
/// <para>
/// The mesh publishes heterogeneous payloads - a float for soil moisture, an int
/// for wattage, a bool for a valve state - and the ingestion pipeline has to hold
/// them together in one buffer. A collection cannot be typed as
/// <c>TelemetryPacket&lt;T&gt;</c> when T varies per element, so this interface is
/// the common shape they are stored under.
/// </para>
/// <para>
/// Everything on this interface is metadata or a pre-projected <see cref="NumericValue"/>.
/// The payload itself is reached through the strongly-typed
/// <c>TelemetryPacket&lt;T&gt;.Value</c> property, so ordinary processing never boxes.
/// </para>
/// </summary>
public interface ITelemetryPacket
{
    /// <summary>Sensor the packet came from.</summary>
    Guid SensorId { get; }

    /// <summary>Normalised MAC address of the publishing node.</summary>
    string MacAddress { get; }

    /// <summary>Unit the payload is expressed in.</summary>
    TelemetryUnit Unit { get; }

    /// <summary>Device-side capture time.</summary>
    DateTimeOffset TimestampUtc { get; }

    /// <summary>Monotonic per-sensor sequence number, used to detect dropped packets.</summary>
    long SequenceNumber { get; }

    /// <summary>The closed payload type, e.g. <c>typeof(float)</c>. Useful for diagnostics.</summary>
    Type PayloadType { get; }

    /// <summary>
    /// Payload projected to a double for statistics and charting. Computed through a
    /// cached strongly-typed delegate, so this does not box either.
    /// </summary>
    double NumericValue { get; }

    /// <summary>
    /// The payload as <see cref="object"/>. This is the one boxing path and it exists
    /// solely for JSON serialisation at the API boundary. It is declared here and
    /// implemented explicitly so it cannot be reached by accident from typed code.
    /// </summary>
    object BoxedValue { get; }
}
