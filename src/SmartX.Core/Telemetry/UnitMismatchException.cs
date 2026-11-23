using SmartX.Core.Models;

namespace SmartX.Core.Telemetry;

/// <summary>
/// Thrown when an arithmetic or comparison operator is applied to two readings
/// whose units cannot be reconciled - for example adding a wattage reading to a
/// temperature reading.
/// <para>
/// Failing loudly here is deliberate. A gateway that silently returned 43.2 for
/// "21.4 degrees plus 21.8 watts" would feed a plausible-looking but meaningless
/// number into the dashboard, and nobody would ever notice.
/// </para>
/// </summary>
public sealed class UnitMismatchException : InvalidOperationException
{
    /// <summary>Unit on the left of the operator.</summary>
    public TelemetryUnit Left { get; }

    /// <summary>Unit on the right of the operator.</summary>
    public TelemetryUnit Right { get; }

    /// <summary>The operator that was attempted, e.g. "+".</summary>
    public string Operation { get; }

    /// <summary>Creates the exception for a specific operator and unit pair.</summary>
    public UnitMismatchException(string operation, TelemetryUnit left, TelemetryUnit right)
        : base($"Cannot apply '{operation}' to readings in {left} and {right}. " +
               "Sensor readings may only be combined when their units match.")
    {
        Operation = operation;
        Left = left;
        Right = right;
    }
}
