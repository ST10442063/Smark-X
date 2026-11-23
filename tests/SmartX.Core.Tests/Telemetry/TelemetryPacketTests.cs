using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Tests.Telemetry;

/// <summary>
/// Covers the generic packet wrapper: that disparate payload types round-trip
/// with their exact type intact, and that the numeric projection works for each
/// of them without going through <see cref="Convert"/>.
/// </summary>
public class TelemetryPacketTests
{
    private static readonly Guid SensorId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Mac = "A4:CF:12:9B:0E:77";

    [Fact]
    public void FloatPayload_KeepsItsExactType()
    {
        var packet = TelemetryPacket.Create(SensorId, Mac, 42.5f, TelemetryUnit.Percent);

        Assert.Equal(typeof(float), packet.PayloadType);
        Assert.Equal(42.5f, packet.Value);
        Assert.Equal(42.5d, packet.NumericValue, precision: 5);
    }

    [Fact]
    public void IntPayload_KeepsItsExactType()
    {
        var packet = TelemetryPacket.Create(SensorId, Mac, 1450, TelemetryUnit.Watt);

        Assert.Equal(typeof(int), packet.PayloadType);
        Assert.Equal(1450, packet.Value);
        Assert.Equal(1450d, packet.NumericValue);
    }

    [Theory]
    [InlineData(true, 1d)]
    [InlineData(false, 0d)]
    public void BoolPayload_ChartsAsOneOrZero(bool state, double expected)
    {
        var packet = TelemetryPacket.Create(SensorId, Mac, state, TelemetryUnit.State);

        Assert.Equal(typeof(bool), packet.PayloadType);
        Assert.Equal(state, packet.Value);
        Assert.Equal(expected, packet.NumericValue);
    }

    [Fact]
    public void BoxedValue_IsReachableOnlyThroughTheInterface()
    {
        var packet = TelemetryPacket.Create(SensorId, Mac, 7.25f, TelemetryUnit.Celsius);

        // The typed Value property is what ordinary code uses - no boxing.
        // BoxedValue is an explicit interface implementation, so reaching it
        // requires the deliberate cast that only the serialiser performs.
        object boxed = ((ITelemetryPacket)packet).BoxedValue;

        Assert.IsType<float>(boxed);
        Assert.Equal(7.25f, (float)boxed);
    }

    [Fact]
    public void HeterogeneousPackets_ShareOneCollectionThroughTheInterface()
    {
        List<ITelemetryPacket> buffer =
        [
            TelemetryPacket.Create(SensorId, Mac, 31.8f, TelemetryUnit.Percent),
            TelemetryPacket.Create(SensorId, Mac, 1450, TelemetryUnit.Watt),
            TelemetryPacket.Create(SensorId, Mac, true, TelemetryUnit.State)
        ];

        Assert.Equal([typeof(float), typeof(int), typeof(bool)], buffer.Select(p => p.PayloadType));
        Assert.Equal(1482.8d, buffer.Sum(p => p.NumericValue), precision: 3);
    }

    [Fact]
    public void CompareValueTo_UsesThePayloadTypesOwnComparer()
    {
        var low = TelemetryPacket.Create(SensorId, Mac, 12.0f, TelemetryUnit.Percent);
        var high = TelemetryPacket.Create(SensorId, Mac, 48.0f, TelemetryUnit.Percent);

        Assert.True(low.CompareValueTo(high) < 0);
        Assert.True(high.CompareValueTo(low) > 0);
        Assert.Equal(0, low.CompareValueTo(low));
    }

    [Fact]
    public void HasSameValueAs_ComparesPayloadsNotReferences()
    {
        var first = TelemetryPacket.Create(SensorId, Mac, 19, TelemetryUnit.Watt);
        var second = TelemetryPacket.Create(SensorId, Mac, 19, TelemetryUnit.Watt);
        var third = TelemetryPacket.Create(SensorId, Mac, 20, TelemetryUnit.Watt);

        Assert.True(first.HasSameValueAs(second));
        Assert.False(first.HasSameValueAs(third));
    }

    [Fact]
    public void CreateFromSensorRecord_InheritsIdMacAndUnit()
    {
        var sensor = new SensorRecord
        {
            MacAddress = Mac,
            DisplayName = "Hydro Bay 3",
            Category = SensorCategory.Environmental,
            Location = new DeploymentLocation("Facility A", "Zone 1", "Sub-Zone B", "NODE-07"),
            Unit = TelemetryUnit.Percent
        };

        var packet = TelemetryPacket.Create(sensor, 55.5f);

        Assert.Equal(sensor.Id, packet.SensorId);
        Assert.Equal(Mac, packet.MacAddress);
        Assert.Equal(TelemetryUnit.Percent, packet.Unit);
    }
}
