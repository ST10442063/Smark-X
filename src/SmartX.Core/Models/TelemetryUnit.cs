namespace SmartX.Core.Models;

/// <summary>
/// Unit of measure attached to every sensor reading.
/// <para>
/// This exists so that operator overloading can be made type-safe: adding a
/// reading in watts to a reading in degrees Celsius is physically meaningless,
/// so the <c>+</c> operator rejects it rather than silently producing a number.
/// </para>
/// </summary>
public enum TelemetryUnit
{
    /// <summary>Dimensionless count or index.</summary>
    None = 0,

    /// <summary>Degrees Celsius.</summary>
    Celsius = 1,

    /// <summary>Relative percentage, 0-100 (soil moisture, humidity).</summary>
    Percent = 2,

    /// <summary>Watts - instantaneous power draw.</summary>
    Watt = 3,

    /// <summary>Volts.</summary>
    Volt = 4,

    /// <summary>Amperes.</summary>
    Ampere = 5,

    /// <summary>Parts per million (CO2, nutrient concentration).</summary>
    Ppm = 6,

    /// <summary>Received signal strength in dBm.</summary>
    Dbm = 7,

    /// <summary>Discrete actuator state, 0 or 1.</summary>
    State = 8
}
