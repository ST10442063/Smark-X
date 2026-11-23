using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Tests.Telemetry;

/// <summary>
/// Covers the overloaded operators - both that they compute the right answer and,
/// just as importantly, that they refuse to compute a meaningless one.
/// </summary>
public class SensorReadingOperatorTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 3, 1, 8, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Addition_AggregatesTwoSmartMeters()
    {
        var meter1 = new SensorReading(1450d, TelemetryUnit.Watt, Earlier);
        var meter2 = new SensorReading(880d, TelemetryUnit.Watt, Later);

        var meter3 = meter1 + meter2;

        Assert.Equal(2330d, meter3.Value);
        Assert.Equal(TelemetryUnit.Watt, meter3.Unit);
    }

    [Fact]
    public void Addition_CarriesTheLaterTimestamp()
    {
        var stale = new SensorReading(10d, TelemetryUnit.Watt, Earlier);
        var fresh = new SensorReading(10d, TelemetryUnit.Watt, Later);

        // An aggregate is only as fresh as its most recent input.
        Assert.Equal(Later, (stale + fresh).TimestampUtc);
        Assert.Equal(Later, (fresh + stale).TimestampUtc);
    }

    [Fact]
    public void Addition_RejectsIrreconcilableUnits()
    {
        var watts = new SensorReading(1450d, TelemetryUnit.Watt);
        var celsius = new SensorReading(21.4d, TelemetryUnit.Celsius);

        var error = Assert.Throws<UnitMismatchException>(() => watts + celsius);

        Assert.Equal("+", error.Operation);
        Assert.Equal(TelemetryUnit.Watt, error.Left);
        Assert.Equal(TelemetryUnit.Celsius, error.Right);
    }

    [Fact]
    public void Addition_LetsAUnitlessOperandAdoptTheOtherUnit()
    {
        var watts = new SensorReading(100d, TelemetryUnit.Watt);

        // 5 promotes through the implicit double conversion as unitless.
        var result = watts + 5d;

        Assert.Equal(105d, result.Value);
        Assert.Equal(TelemetryUnit.Watt, result.Unit);
    }

    [Fact]
    public void Subtraction_ProducesANegativeDeltaOnAMoistureDrop()
    {
        var previous = new SensorReading(38.0d, TelemetryUnit.Percent, Earlier);
        var current = new SensorReading(19.5d, TelemetryUnit.Percent, Later);

        var delta = current - previous;

        Assert.Equal(-18.5d, delta.Value, precision: 5);
        Assert.Equal(TelemetryUnit.Percent, delta.Unit);
    }

    [Fact]
    public void UnaryNegation_KeepsUnitAndTimestamp()
    {
        var reading = new SensorReading(12.5d, TelemetryUnit.Celsius, Earlier);

        var negated = -reading;

        Assert.Equal(-12.5d, negated.Value);
        Assert.Equal(TelemetryUnit.Celsius, negated.Unit);
        Assert.Equal(Earlier, negated.TimestampUtc);
    }

    [Fact]
    public void Multiplication_ScalesFromEitherSide()
    {
        var reading = new SensorReading(20d, TelemetryUnit.Watt);

        Assert.Equal(60d, (reading * 3d).Value);
        Assert.Equal(60d, (3d * reading).Value);
    }

    [Fact]
    public void Division_AveragesAZoneAndRejectsZero()
    {
        var zoneTotal = new SensorReading(120d, TelemetryUnit.Watt);

        Assert.Equal(40d, (zoneTotal / 3d).Value);
        Assert.Throws<DivideByZeroException>(() => zoneTotal / 0d);
    }

    [Fact]
    public void ComparisonOperators_ReadAsPlainThresholdChecks()
    {
        var reading = new SensorReading(19.5d, TelemetryUnit.Percent);
        var critical = new SensorReading(20.0d, TelemetryUnit.Percent);

        Assert.True(reading < critical);
        Assert.True(reading <= critical);
        Assert.False(reading > critical);
        Assert.False(reading >= critical);
        Assert.True(critical >= new SensorReading(20.0d, TelemetryUnit.Percent));
    }

    [Fact]
    public void ComparisonOperators_RejectIrreconcilableUnits()
    {
        var watts = new SensorReading(1450d, TelemetryUnit.Watt);
        var celsius = new SensorReading(21.4d, TelemetryUnit.Celsius);

        Assert.Throws<UnitMismatchException>(() => watts > celsius);
        Assert.Throws<UnitMismatchException>(() => watts < celsius);
        Assert.Throws<UnitMismatchException>(() => watts >= celsius);
        Assert.Throws<UnitMismatchException>(() => watts <= celsius);
    }

    [Fact]
    public void Equality_ComparesValueAndUnitButIgnoresTimestamp()
    {
        var morning = new SensorReading(21.4d, TelemetryUnit.Celsius, Earlier);
        var afternoon = new SensorReading(21.4d, TelemetryUnit.Celsius, Later);
        var differentUnit = new SensorReading(21.4d, TelemetryUnit.Percent, Earlier);

        Assert.True(morning == afternoon);
        Assert.False(morning != afternoon);
        Assert.True(morning != differentUnit);
        Assert.Equal(morning.GetHashCode(), afternoon.GetHashCode());
    }

    [Fact]
    public void Conversions_AreExplicitOutAndImplicitIn()
    {
        var reading = new SensorReading(42.5d, TelemetryUnit.Watt);

        // Losing the unit must be deliberate, so this direction is explicit.
        Assert.Equal(42.5d, (double)reading);

        // Promoting a bare number is always safe, so this direction is implicit.
        SensorReading promoted = 42.5d;
        Assert.Equal(TelemetryUnit.None, promoted.Unit);
    }

    [Fact]
    public void CompareTo_StaysTotalSoAMixedUnitListCanSort()
    {
        var readings = new List<SensorReading>
        {
            new(1450d, TelemetryUnit.Watt),
            new(21.4d, TelemetryUnit.Celsius),
            new(880d, TelemetryUnit.Watt),
            new(18.2d, TelemetryUnit.Celsius)
        };

        // Sorting must not throw even though < and > would reject these pairs.
        readings.Sort();

        Assert.Equal(
            [TelemetryUnit.Celsius, TelemetryUnit.Celsius, TelemetryUnit.Watt, TelemetryUnit.Watt],
            readings.Select(r => r.Unit));
        Assert.Equal([18.2d, 21.4d, 880d, 1450d], readings.Select(r => r.Value));
    }

    [Fact]
    public void AggregatingAFleet_ReadsAsASum()
    {
        SensorReading[] fleet =
        [
            new(1450d, TelemetryUnit.Watt),
            new(880d, TelemetryUnit.Watt),
            new(220d, TelemetryUnit.Watt)
        ];

        var total = fleet.Aggregate(SensorReading.Zero, (running, next) => running + next);

        Assert.Equal(2550d, total.Value);
        Assert.Equal(TelemetryUnit.Watt, total.Unit);
    }

    [Fact]
    public void ToString_RendersMagnitudeWithUnitSymbol()
    {
        Assert.Equal("1450 W", new SensorReading(1450d, TelemetryUnit.Watt).ToString());
        Assert.Equal("19.5 %", new SensorReading(19.5d, TelemetryUnit.Percent).ToString());
        Assert.Equal("7", new SensorReading(7d, TelemetryUnit.None).ToString());
    }

    [Fact]
    public void PacketOperators_AggregateAndCompareWithoutUnwrapping()
    {
        var sensorId = Guid.NewGuid();
        var meterA = TelemetryPacket.Create(sensorId, "A4:CF:12:9B:0E:77", 1450, TelemetryUnit.Watt);
        var meterB = TelemetryPacket.Create(sensorId, "A4:CF:12:9B:0E:78", 880, TelemetryUnit.Watt);

        var combined = meterA + meterB;
        Assert.Equal(2330d, combined.Value);
        Assert.Equal(TelemetryUnit.Watt, combined.Unit);

        Assert.Equal(570d, (meterA - meterB).Value);

        // The implicit conversion lets a packet be compared against a threshold directly.
        var threshold = new SensorReading(2000d, TelemetryUnit.Watt);
        Assert.True(meterA < threshold);
    }
}
