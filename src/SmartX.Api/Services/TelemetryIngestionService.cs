using System.Globalization;
using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>Why an ingestion attempt was refused.</summary>
public enum IngestionFailure
{
    /// <summary>Nothing was refused.</summary>
    None = 0,

    /// <summary>No node is registered under that MAC address.</summary>
    UnknownSensor = 1,

    /// <summary>A payload could not be parsed as the declared type.</summary>
    UnparseablePayload = 2
}

/// <summary>Outcome of an ingestion attempt.</summary>
/// <param name="Failure">Why it was refused, or None.</param>
/// <param name="Sensor">The sensor, when it was found.</param>
/// <param name="Packets">The packets built, when parsing succeeded.</param>
/// <param name="Message">Explanation suitable for returning to the caller.</param>
public readonly record struct IngestionOutcome(
    IngestionFailure Failure,
    SensorRecord? Sensor,
    IReadOnlyList<ITelemetryPacket> Packets,
    string Message)
{
    /// <summary>True when the packets were built and can be accepted.</summary>
    public bool Succeeded => Failure == IngestionFailure.None;
}

/// <summary>
/// Turns wire payloads into strongly-typed telemetry packets.
/// <para>
/// This is where the generic design earns its keep. The request carries the value
/// as text plus a type discriminator, and this service switches on that
/// discriminator once, at the boundary, to construct the right closed generic:
/// <c>TelemetryPacket&lt;float&gt;</c> for a moisture probe,
/// <c>TelemetryPacket&lt;int&gt;</c> for a meter, <c>TelemetryPacket&lt;bool&gt;</c>
/// for a relay. Past this point the payload is in a strongly-typed field and
/// nothing boxes it again.
/// </para>
/// <para>
/// The alternative - parsing everything to double on arrival - would be simpler
/// and would quietly throw away the distinction between a relay that is on and a
/// meter reading of 1. The discriminator is what keeps a bool a bool all the way
/// through the pipeline.
/// </para>
/// </summary>
public sealed class TelemetryIngestionService(GatewayStore store)
{
    /// <summary>Builds a single packet from a wire payload.</summary>
    /// <param name="macAddress">Publishing node.</param>
    /// <param name="payloadType">"float", "int" or "bool".</param>
    /// <param name="rawValue">The value as text.</param>
    /// <param name="sequenceNumber">Per-sensor sequence number.</param>
    /// <param name="timestampUtc">Capture time, or null for now.</param>
    public IngestionOutcome BuildSingle(
        string macAddress,
        string payloadType,
        string rawValue,
        long sequenceNumber,
        DateTimeOffset? timestampUtc)
    {
        var (sensor, _) = store.FindByMac(macAddress);
        if (sensor is null)
        {
            return UnknownSensor(macAddress);
        }

        if (!TryBuildPacket(sensor, payloadType, rawValue, sequenceNumber,
                            timestampUtc ?? DateTimeOffset.UtcNow, out var packet))
        {
            return Unparseable(rawValue, payloadType);
        }

        return new IngestionOutcome(IngestionFailure.None, sensor, [packet], "Reading accepted.");
    }

    /// <summary>
    /// Builds a batch from a list of wire payloads, spacing their timestamps by
    /// <paramref name="intervalSeconds"/> from <paramref name="startedAtUtc"/>.
    /// </summary>
    public IngestionOutcome BuildBatch(
        string macAddress,
        string payloadType,
        IReadOnlyList<string> rawValues,
        DateTimeOffset? startedAtUtc,
        double intervalSeconds)
    {
        var (sensor, _) = store.FindByMac(macAddress);
        if (sensor is null)
        {
            return UnknownSensor(macAddress);
        }

        // With no explicit start, treat the batch as having just been flushed:
        // work backwards from now so the newest reading lands at the present.
        var start = startedAtUtc
                    ?? DateTimeOffset.UtcNow.AddSeconds(-intervalSeconds * (rawValues.Count - 1));

        var packets = new List<ITelemetryPacket>(rawValues.Count);

        for (var i = 0; i < rawValues.Count; i++)
        {
            var timestamp = start.AddSeconds(intervalSeconds * i);

            if (!TryBuildPacket(sensor, payloadType, rawValues[i], i, timestamp, out var packet))
            {
                return Unparseable(rawValues[i], payloadType);
            }

            packets.Add(packet);
        }

        return new IngestionOutcome(
            IngestionFailure.None, sensor, packets, $"{packets.Count} readings accepted.");
    }

    /// <summary>
    /// Synthesises a batch for a node, optionally distorting one reading so the
    /// anomaly detector has something to catch.
    /// </summary>
    public IngestionOutcome BuildGenerated(string macAddress, int count, bool injectAnomaly)
    {
        var (sensor, _) = store.FindByMac(macAddress);
        if (sensor is null)
        {
            return UnknownSensor(macAddress);
        }

        var payloadType = TelemetrySimulator.PayloadTypeFor(sensor.Category);
        var random = new Random();
        var packets = new List<ITelemetryPacket>(count);

        // Place the distorted reading near the end, so the readings before it have
        // established the rolling mean the detector will measure it against.
        var anomalyIndex = injectAnomaly ? Math.Max(0, count - 2) : -1;
        var start = DateTimeOffset.UtcNow.AddSeconds(-count);

        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = TelemetrySimulator.Next(sensor.Category, i, random, sensor.Id.GetHashCode() % 10);
        }

        AnchorToRecentLevel(sensor, values);

        for (var i = 0; i < count; i++)
        {
            var value = values[i];

            if (i == anomalyIndex)
            {
                value = TelemetrySimulator.Distort(sensor.Category, value);
            }

            var payload = TelemetrySimulator.ToPayload(payloadType, value);

            if (!TryBuildPacket(sensor, payloadType, payload, i, start.AddSeconds(i), out var packet))
            {
                return Unparseable(payload, payloadType);
            }

            packets.Add(packet);
        }

        return new IngestionOutcome(
            IngestionFailure.None,
            sensor,
            packets,
            injectAnomaly
                ? $"{count} readings generated, including one deliberate anomaly."
                : $"{count} readings generated.");
    }

    /// <summary>
    /// Shifts a synthesised run so it starts where the node actually is.
    /// <para>
    /// The waveform has its own textbook baseline - 820 W for a meter - but a live node
    /// fed by the device emulator sits wherever its own history put it, perhaps 630 W.
    /// Starting the run at the textbook level would be a step the rolling z-score
    /// rightly flags, again and again until its window caught up, burying the single
    /// anomaly the operator asked for under several they did not. Anchoring the first
    /// few generated readings to the recent mean keeps the run's shape and removes
    /// the step. Actuators report a state, not a level, and are left alone.
    /// </para>
    /// </summary>
    private void AnchorToRecentLevel(SensorRecord sensor, double[] values)
    {
        if (values.Length == 0 || sensor.Category == SensorCategory.Actuator)
        {
            return;
        }

        var recent = store.RecentReadings(sensor.Id, 20);
        if (recent.Count == 0)
        {
            return;
        }

        var level = recent.Average(reading => reading.Value);
        var lead = values.Take(Math.Min(5, values.Length)).Average();
        var shift = level - lead;

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = TelemetrySimulator.Bound(sensor.Category, values[i] + shift);
        }
    }

    /// <summary>
    /// Constructs the closed generic named by <paramref name="payloadType"/>.
    /// <para>
    /// The switch is the whole point: each arm calls the same generic factory with
    /// a different concrete type, so the compiler emits three distinct closed types
    /// and the payload is never widened to a common representation.
    /// </para>
    /// </summary>
    private static bool TryBuildPacket(
        SensorRecord sensor,
        string payloadType,
        string rawValue,
        long sequenceNumber,
        DateTimeOffset timestampUtc,
        out ITelemetryPacket packet)
        => TryBuildPacket(sensor.Id, sensor.MacAddress, sensor.Unit, payloadType, rawValue, sequenceNumber, timestampUtc, out packet);

    /// <summary>
    /// The same discriminator switch, for callers that hold a device's key, address
    /// and unit rather than its <see cref="SensorRecord"/> - the Part 2 uplink, which
    /// resolves devices from its own dictionary. Public so both ingestion paths build
    /// packets with exactly one piece of parsing code.
    /// </summary>
    public static bool TryBuildPacket(
        Guid sensorId,
        string macAddress,
        TelemetryUnit unit,
        string payloadType,
        string rawValue,
        long sequenceNumber,
        DateTimeOffset timestampUtc,
        out ITelemetryPacket packet)
    {
        switch (payloadType?.ToLowerInvariant())
        {
            case "float":
                if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var asFloat))
                {
                    packet = TelemetryPacket.Create(sensorId, macAddress, asFloat, unit, sequenceNumber, timestampUtc);
                    return true;
                }

                break;

            case "int":
                if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asInt))
                {
                    packet = TelemetryPacket.Create(sensorId, macAddress, asInt, unit, sequenceNumber, timestampUtc);
                    return true;
                }

                break;

            case "bool":
                // Accept the forms a microcontroller is likely to publish, not just
                // the two the framework parser recognises.
                var normalised = rawValue?.Trim().ToLowerInvariant();
                if (normalised is "true" or "1" or "on" or "open")
                {
                    packet = TelemetryPacket.Create(sensorId, macAddress, true, unit, sequenceNumber, timestampUtc);
                    return true;
                }

                if (normalised is "false" or "0" or "off" or "closed")
                {
                    packet = TelemetryPacket.Create(sensorId, macAddress, false, unit, sequenceNumber, timestampUtc);
                    return true;
                }

                break;
        }

        packet = null!;
        return false;
    }

    private static IngestionOutcome UnknownSensor(string macAddress) => new(
        IngestionFailure.UnknownSensor,
        null,
        [],
        $"No sensor is registered under MAC address '{macAddress}'. Register it before publishing telemetry.");

    private static IngestionOutcome Unparseable(string rawValue, string payloadType) => new(
        IngestionFailure.UnparseablePayload,
        null,
        [],
        $"'{rawValue}' could not be read as {payloadType switch { "int" => "an integer", "bool" => "true or false", _ => "a number" }}.");
}
