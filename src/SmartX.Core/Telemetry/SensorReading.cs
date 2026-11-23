using SmartX.Core.Models;

namespace SmartX.Core.Telemetry;

/// <summary>
/// A single unit-aware sensor value.
/// <para>
/// <b>Why the operators exist.</b> Aggregating the load of two smart meters is a
/// thing the gateway does constantly, and written as method calls it reads as
/// <c>SensorReading.Add(SensorReading.Add(m1, m2), m3)</c>. Overloading <c>+</c>
/// lets the same aggregation read as <c>var total = m1 + m2 + m3;</c>, which is
/// how an engineer would write it on paper.
/// </para>
/// <para>
/// The operators carry the unit through the arithmetic rather than dropping to a
/// bare double, so <c>meterA + probeB</c> - watts plus degrees Celsius - is
/// rejected at the point of the mistake instead of producing a number that looks
/// perfectly reasonable on a chart.
/// </para>
/// <para>
/// Declared <c>readonly struct</c> so readings are copied by value with no heap
/// allocation: the ingestion path creates millions of these.
/// </para>
/// </summary>
public readonly struct SensorReading
    : IEquatable<SensorReading>, IComparable<SensorReading>, IFormattable
{
    /// <summary>Creates a reading.</summary>
    /// <param name="value">Scalar magnitude.</param>
    /// <param name="unit">Unit of measure.</param>
    /// <param name="timestampUtc">Capture time. Defaults to now.</param>
    public SensorReading(double value, TelemetryUnit unit, DateTimeOffset? timestampUtc = null)
    {
        Value = value;
        Unit = unit;
        TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Scalar magnitude of the reading.</summary>
    public double Value { get; }

    /// <summary>Unit the magnitude is expressed in.</summary>
    public TelemetryUnit Unit { get; }

    /// <summary>When the reading was captured.</summary>
    public DateTimeOffset TimestampUtc { get; }

    /// <summary>A zero reading with no unit, used as the seed of an aggregation.</summary>
    public static SensorReading Zero => new(0d, TelemetryUnit.None);

    // ---------------------------------------------------------------------
    // Arithmetic operators
    // ---------------------------------------------------------------------

    /// <summary>
    /// Aggregates two readings, e.g. <c>Meter3 = Meter1 + Meter2</c> for the combined
    /// load of two smart meters. The result carries the later of the two timestamps,
    /// because an aggregate is only as fresh as its stalest input is recent.
    /// </summary>
    /// <exception cref="UnitMismatchException">The units cannot be reconciled.</exception>
    public static SensorReading operator +(SensorReading left, SensorReading right)
        => new(left.Value + right.Value,
               ReconcileUnits("+", left.Unit, right.Unit),
               Later(left.TimestampUtc, right.TimestampUtc));

    /// <summary>
    /// Deltas two readings, e.g. how far a moisture probe has fallen since the last
    /// sample. Ordering matters: <c>now - previous</c> is negative on a drop.
    /// </summary>
    /// <exception cref="UnitMismatchException">The units cannot be reconciled.</exception>
    public static SensorReading operator -(SensorReading left, SensorReading right)
        => new(left.Value - right.Value,
               ReconcileUnits("-", left.Unit, right.Unit),
               Later(left.TimestampUtc, right.TimestampUtc));

    /// <summary>Negates a reading, keeping its unit and timestamp.</summary>
    public static SensorReading operator -(SensorReading reading)
        => new(-reading.Value, reading.Unit, reading.TimestampUtc);

    /// <summary>
    /// Scales a reading by a factor - used to convert a per-sample figure into a
    /// per-interval one, or to weight a node's contribution to a zone total.
    /// </summary>
    public static SensorReading operator *(SensorReading reading, double factor)
        => new(reading.Value * factor, reading.Unit, reading.TimestampUtc);

    /// <summary>Scales a reading by a factor.</summary>
    public static SensorReading operator *(double factor, SensorReading reading)
        => reading * factor;

    /// <summary>Divides a reading by a divisor - used to average a zone's nodes.</summary>
    /// <exception cref="DivideByZeroException">The divisor is zero.</exception>
    public static SensorReading operator /(SensorReading reading, double divisor)
        => divisor == 0d
            ? throw new DivideByZeroException("Cannot divide a sensor reading by zero.")
            : new SensorReading(reading.Value / divisor, reading.Unit, reading.TimestampUtc);

    // ---------------------------------------------------------------------
    // Comparison operators - threshold checks read as plain inequalities
    // ---------------------------------------------------------------------

    /// <summary>True when the left reading exceeds the right. Units must match.</summary>
    /// <exception cref="UnitMismatchException">The units cannot be reconciled.</exception>
    public static bool operator >(SensorReading left, SensorReading right)
    {
        ReconcileUnits(">", left.Unit, right.Unit);
        return left.Value > right.Value;
    }

    /// <summary>True when the left reading is below the right. Units must match.</summary>
    /// <exception cref="UnitMismatchException">The units cannot be reconciled.</exception>
    public static bool operator <(SensorReading left, SensorReading right)
    {
        ReconcileUnits("<", left.Unit, right.Unit);
        return left.Value < right.Value;
    }

    /// <summary>True when the left reading is at or above the right. Units must match.</summary>
    /// <exception cref="UnitMismatchException">The units cannot be reconciled.</exception>
    public static bool operator >=(SensorReading left, SensorReading right)
    {
        ReconcileUnits(">=", left.Unit, right.Unit);
        return left.Value >= right.Value;
    }

    /// <summary>True when the left reading is at or below the right. Units must match.</summary>
    /// <exception cref="UnitMismatchException">The units cannot be reconciled.</exception>
    public static bool operator <=(SensorReading left, SensorReading right)
    {
        ReconcileUnits("<=", left.Unit, right.Unit);
        return left.Value <= right.Value;
    }

    /// <summary>
    /// Value and unit equality. Timestamp is deliberately excluded: two nodes
    /// reporting 21.4 C are reporting the same reading whether or not their clocks
    /// agree to the millisecond.
    /// </summary>
    public static bool operator ==(SensorReading left, SensorReading right) => left.Equals(right);

    /// <summary>Inverse of the equality operator.</summary>
    public static bool operator !=(SensorReading left, SensorReading right) => !left.Equals(right);

    // ---------------------------------------------------------------------
    // Conversions
    // ---------------------------------------------------------------------

    /// <summary>
    /// Drops to a bare magnitude. Explicit rather than implicit, because losing the
    /// unit should be a deliberate act at a charting or serialisation boundary and
    /// never something the compiler does silently mid-expression.
    /// </summary>
    public static explicit operator double(SensorReading reading) => reading.Value;

    /// <summary>Promotes a bare magnitude into a unitless reading stamped now.</summary>
    public static implicit operator SensorReading(double value) => new(value, TelemetryUnit.None);

    // ---------------------------------------------------------------------
    // Equality and ordering
    // ---------------------------------------------------------------------

    /// <inheritdoc />
    public bool Equals(SensorReading other) => Unit == other.Unit && Value.Equals(other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SensorReading other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Value, Unit);

    /// <summary>
    /// Total ordering by unit then magnitude.
    /// <para>
    /// This deliberately differs from the <c>&lt;</c> and <c>&gt;</c> operators, which
    /// reject mismatched units. Sorting a mixed-unit list has to be a total order or
    /// <see cref="List{T}.Sort()"/> would throw halfway through, so here mismatched
    /// units group together rather than raising.
    /// </para>
    /// </summary>
    public int CompareTo(SensorReading other)
    {
        var byUnit = Unit.CompareTo(other.Unit);
        return byUnit != 0 ? byUnit : Value.CompareTo(other.Value);
    }

    // ---------------------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------------------

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <summary>
    /// Formats the reading with its unit, e.g. "820 W". A relay state is written as
    /// ON or OFF: "1 state" is the stored representation, not something an operator
    /// should have to translate.
    /// </summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
        => Unit == TelemetryUnit.State
            ? (Value > 0.5d ? "ON" : "OFF")
            : $"{Value.ToString(format ?? "0.##", formatProvider)} {UnitSymbol(Unit)}".TrimEnd();

    /// <summary>Short display symbol for a unit, e.g. "W" for <see cref="TelemetryUnit.Watt"/>.</summary>
    public static string UnitSymbol(TelemetryUnit unit) => unit switch
    {
        TelemetryUnit.Celsius => "°C",
        TelemetryUnit.Percent => "%",
        TelemetryUnit.Watt => "W",
        TelemetryUnit.Volt => "V",
        TelemetryUnit.Ampere => "A",
        TelemetryUnit.Ppm => "ppm",
        TelemetryUnit.Dbm => "dBm",
        TelemetryUnit.State => "state",
        _ => string.Empty
    };

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Decides the unit of a combined result. Identical units pass through, and a
    /// unitless operand adopts its partner's unit so <c>reading + 5</c> still works
    /// through the implicit double conversion. Anything else is a genuine mistake.
    /// </summary>
    private static TelemetryUnit ReconcileUnits(string operation, TelemetryUnit left, TelemetryUnit right)
    {
        if (left == right)
        {
            return left;
        }

        if (left == TelemetryUnit.None)
        {
            return right;
        }

        if (right == TelemetryUnit.None)
        {
            return left;
        }

        throw new UnitMismatchException(operation, left, right);
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;
}
