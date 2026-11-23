using SmartX.Core.Models;

namespace SmartX.Core.Telemetry;

/// <summary>
/// Reusable generic wrapper carrying one reading of any value type published by
/// the mesh - <c>TelemetryPacket&lt;float&gt;</c> for soil moisture,
/// <c>TelemetryPacket&lt;int&gt;</c> for power wattage,
/// <c>TelemetryPacket&lt;bool&gt;</c> for a smart-switch trigger.
/// <para>
/// <b>Why generic rather than a payload of type <see cref="object"/>.</b> An
/// <c>object Value</c> field would force every float to be boxed on ingest and
/// unboxed on every read - two heap round-trips per packet, plus a cast that the
/// compiler cannot verify. Making the payload a type parameter constrained to
/// <c>struct</c> keeps the value in a strongly-typed field, so it travels inline
/// with the packet, the compiler checks every access, and the GC never sees it.
/// </para>
/// <para>
/// Comparisons go through <see cref="Comparer{T}"/> and
/// <see cref="EqualityComparer{T}"/> rather than <c>Object.Equals</c> for the same
/// reason: those resolve to the type's own <see cref="IComparable{T}"/> and
/// <see cref="IEquatable{T}"/> implementations, which take the value directly.
/// </para>
/// </summary>
/// <typeparam name="T">
/// The payload type. Constrained to <c>struct</c> so a packet can never carry a
/// null payload, and to <see cref="IComparable{T}"/>/<see cref="IEquatable{T}"/>
/// so ordering and equality are available without boxing.
/// </typeparam>
public sealed class TelemetryPacket<T> : ITelemetryPacket
    where T : struct, IComparable<T>, IEquatable<T>
{
    /// <summary>Sensor the packet came from.</summary>
    public required Guid SensorId { get; init; }

    /// <summary>Normalised MAC address of the publishing node.</summary>
    public required string MacAddress { get; init; }

    /// <summary>
    /// The reading itself, stored in a field of the exact payload type.
    /// This is the property ordinary processing should read.
    /// </summary>
    public required T Value { get; init; }

    /// <summary>Unit the payload is expressed in.</summary>
    public required TelemetryUnit Unit { get; init; }

    /// <summary>Device-side capture time.</summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Monotonic per-sensor sequence number, used to detect dropped packets.</summary>
    public long SequenceNumber { get; init; }

    /// <inheritdoc />
    public Type PayloadType => typeof(T);

    /// <inheritdoc />
    public double NumericValue => TelemetryValueConverter<T>.ToDouble(Value);

    /// <summary>
    /// Explicit interface implementation. Boxing the payload is only reachable by
    /// first casting the packet to <see cref="ITelemetryPacket"/>, which the
    /// serialiser does once at the API boundary and nothing else does at all.
    /// </summary>
    object ITelemetryPacket.BoxedValue => Value;

    /// <summary>
    /// Orders this packet's payload against another's using the payload type's own
    /// <see cref="IComparable{T}"/> implementation.
    /// </summary>
    public int CompareValueTo(TelemetryPacket<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Comparer<T>.Default.Compare(Value, other.Value);
    }

    /// <summary>
    /// Compares payloads using the payload type's own <see cref="IEquatable{T}"/>
    /// implementation, so no boxing occurs even for custom structs.
    /// </summary>
    public bool HasSameValueAs(TelemetryPacket<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return EqualityComparer<T>.Default.Equals(Value, other.Value);
    }

    /// <summary>
    /// Projects the packet into a <see cref="SensorReading"/> - the unit-aware struct
    /// the aggregation operators work on.
    /// </summary>
    public SensorReading ToReading() => new(NumericValue, Unit, TimestampUtc);

    /// <summary>
    /// Aggregates two packets from the same fleet directly, so the combined draw of
    /// two smart meters reads as <c>var load = meterPacketA + meterPacketB;</c>.
    /// <para>
    /// The result is a <see cref="SensorReading"/> rather than another packet: a sum
    /// of two nodes' output belongs to neither node, so handing back something that
    /// still claimed a single <c>SensorId</c> and sequence number would be a lie.
    /// </para>
    /// </summary>
    /// <exception cref="UnitMismatchException">The packets carry irreconcilable units.</exception>
    public static SensorReading operator +(TelemetryPacket<T> left, TelemetryPacket<T> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.ToReading() + right.ToReading();
    }

    /// <summary>
    /// Deltas two packets, e.g. the drop between consecutive moisture samples.
    /// </summary>
    /// <exception cref="UnitMismatchException">The packets carry irreconcilable units.</exception>
    public static SensorReading operator -(TelemetryPacket<T> left, TelemetryPacket<T> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.ToReading() - right.ToReading();
    }

    /// <summary>
    /// Lets a packet stand in wherever a <see cref="SensorReading"/> is expected, so
    /// threshold checks read as <c>if (packet &gt; criticalThreshold)</c>.
    /// </summary>
    public static implicit operator SensorReading(TelemetryPacket<T> packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return packet.ToReading();
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"[{TimestampUtc:HH:mm:ss}] {MacAddress} #{SequenceNumber} = {Value} ({typeof(T).Name}/{Unit})";
}

/// <summary>
/// Factory helpers for <see cref="TelemetryPacket{T}"/>. These exist so callers can
/// write <c>TelemetryPacket.Create(sensor, 21.4f, TelemetryUnit.Celsius)</c> and let
/// the compiler infer the type argument, instead of restating it.
/// </summary>
public static class TelemetryPacket
{
    /// <summary>Creates a packet, inferring the payload type from <paramref name="value"/>.</summary>
    public static TelemetryPacket<T> Create<T>(
        Guid sensorId,
        string macAddress,
        T value,
        TelemetryUnit unit,
        long sequenceNumber = 0,
        DateTimeOffset? timestampUtc = null)
        where T : struct, IComparable<T>, IEquatable<T>
        => new()
        {
            SensorId = sensorId,
            MacAddress = macAddress,
            Value = value,
            Unit = unit,
            SequenceNumber = sequenceNumber,
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow
        };

    /// <summary>Creates a packet for a registered sensor, taking its id, MAC and unit.</summary>
    public static TelemetryPacket<T> Create<T>(
        SensorRecord sensor,
        T value,
        long sequenceNumber = 0,
        DateTimeOffset? timestampUtc = null)
        where T : struct, IComparable<T>, IEquatable<T>
    {
        ArgumentNullException.ThrowIfNull(sensor);
        return Create(sensor.Id, sensor.MacAddress, value, sensor.Unit, sequenceNumber, timestampUtc);
    }
}
