using SmartX.Core.Models;

namespace SmartX.Api.Services;

/// <summary>
/// Synthesises plausible readings for a node so the pipeline can be driven under
/// load without physical hardware attached.
/// <para>
/// The values are shaped per category rather than drawn from a flat random range.
/// Uniform noise would make the anomaly detector meaningless - with no underlying
/// signal, every reading is as surprising as every other, and a z-score against a
/// rolling mean has nothing to say. So each category gets a baseline, a slow
/// diurnal swing, and a little jitter, which is what gives the detector a normal
/// to measure a genuine spike against.
/// </para>
/// </summary>
public static class TelemetrySimulator
{
    /// <summary>The payload type a category publishes.</summary>
    public static string PayloadTypeFor(SensorCategory category) => category switch
    {
        SensorCategory.PowerConsumption => "int",
        SensorCategory.Actuator => "bool",
        SensorCategory.Connectivity => "int",
        _ => "float"
    };

    /// <summary>The unit a category publishes in.</summary>
    public static TelemetryUnit UnitFor(SensorCategory category) => category switch
    {
        SensorCategory.PowerConsumption => TelemetryUnit.Watt,
        SensorCategory.Actuator => TelemetryUnit.State,
        SensorCategory.Connectivity => TelemetryUnit.Dbm,
        _ => TelemetryUnit.Percent
    };

    /// <summary>
    /// A reading for <paramref name="category"/> at step <paramref name="step"/>.
    /// </summary>
    /// <param name="category">Operational class, which sets the baseline and swing.</param>
    /// <param name="step">Position in the sequence, driving the diurnal component.</param>
    /// <param name="random">Jitter source. Seed it for a reproducible run.</param>
    /// <param name="seed">Per-sensor offset, so two nodes of the same category do not move in lockstep.</param>
    public static double Next(SensorCategory category, int step, Random random, double seed = 0d)
    {
        ArgumentNullException.ThrowIfNull(random);

        // A slow swing over roughly 240 steps, standing in for a daily cycle.
        var phase = ((step * 2d * Math.PI) / 240d) + seed;
        var jitter = (random.NextDouble() - 0.5d) * 2d;

        return category switch
        {
            // Soil moisture drifts down between irrigation cycles.
            SensorCategory.Environmental =>
                Clamp(46d + (Math.Sin(phase) * 7d) + (jitter * 1.6d), 0d, 100d),

            // Power draw steps up during the working day.
            SensorCategory.PowerConsumption =>
                Math.Round(Clamp(820d + (Math.Sin(phase) * 180d) + (jitter * 40d), 0d, 4000d)),

            // A relay is open or closed; it follows the irrigation cycle.
            SensorCategory.Actuator =>
                Math.Sin(phase) > 0.35d ? 1d : 0d,

            // Link quality in dBm - closer to zero is better, so this stays negative.
            SensorCategory.Connectivity =>
                Math.Round(Clamp(-58d + (Math.Sin(phase) * 6d) + (jitter * 3d), -95d, -30d)),

            _ => Clamp(50d + (jitter * 5d), 0d, 100d)
        };
    }

    /// <summary>
    /// Pushes a value far outside its nominal band, so the detector has a genuine
    /// spike or crash to catch. Environmental readings crash downward - a moisture
    /// probe failing means the crop is drying out - while everything else spikes up.
    /// </summary>
    public static double Distort(SensorCategory category, double nominal) => category switch
    {
        SensorCategory.Environmental => Clamp(nominal * 0.15d, 0d, 100d),
        SensorCategory.PowerConsumption => Math.Round(nominal * 3.4d),
        SensorCategory.Actuator => nominal > 0.5d ? 0d : 1d,
        SensorCategory.Connectivity => -94d,
        _ => nominal * 3d
    };

    /// <summary>Renders a value as the wire payload for its type.</summary>
    public static string ToPayload(string payloadType, double value) => payloadType switch
    {
        "int" => ((int)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "bool" => value > 0.5d ? "true" : "false",
        _ => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>Holds a value inside the physical range its category can report.</summary>
    public static double Bound(SensorCategory category, double value) => category switch
    {
        SensorCategory.Environmental => Clamp(value, 0d, 100d),
        SensorCategory.PowerConsumption => Math.Round(Clamp(value, 0d, 4000d)),
        SensorCategory.Connectivity => Math.Round(Clamp(value, -95d, -30d)),
        _ => value
    };

    private static double Clamp(double value, double low, double high) => Math.Clamp(value, low, high);
}
