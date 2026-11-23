using System.Globalization;
using SmartX.Contracts.Stream;

namespace SmartX.DeviceEmulator;

/// <summary>What a virtual node is doing beyond its normal behaviour.</summary>
public enum DeviceMode
{
    /// <summary>Reporting normally.</summary>
    Normal = 0,

    /// <summary>A moisture probe whose bay has dried out. Stays low until irrigation starts.</summary>
    Crashed = 1,

    /// <summary>A power meter drawing far above normal for a few ticks.</summary>
    Spiking = 2
}

/// <summary>
/// One emulated ESP32 node: its identity from the gateway's manifest, its current
/// reading, and - for an actuator - its relay.
/// <para>
/// Readings revert towards the value the gateway last held for the node, with a
/// little noise. Starting from that value, rather than from a textbook baseline, is
/// what stops the anomaly detector flagging a step on every node the moment the
/// emulator takes over from the seeded history.
/// </para>
/// </summary>
public sealed class VirtualDevice
{
    /// <summary>Creates a node from its manifest entry.</summary>
    public VirtualDevice(ManifestDeviceResponse manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        MacAddress = manifest.MacAddress;
        DisplayName = manifest.DisplayName;
        Category = manifest.Category;
        PayloadType = manifest.PayloadType;
        Zone = manifest.Zone;
        X = manifest.X;
        Y = manifest.Y;

        Baseline = manifest.LastValue ?? Category switch
        {
            "PowerConsumption" => 820d,
            "Connectivity" => -58d,
            "Actuator" => 0d,
            _ => 45d
        };

        Value = Baseline;
        RelayOn = IsActuator && Baseline > 0.5d;
    }

    public string MacAddress { get; }

    public string DisplayName { get; }

    public string Category { get; }

    public string PayloadType { get; }

    public string Zone { get; }

    /// <summary>East-west position on the site plan, in metres, if the gateway supplied one.</summary>
    public double? X { get; }

    /// <summary>North-south position on the site plan, in metres.</summary>
    public double? Y { get; }

    /// <summary>The level this node's readings settle back to.</summary>
    public double Baseline { get; private set; }

    /// <summary>The current reading.</summary>
    public double Value { get; set; }

    /// <summary>The relay, for an actuator.</summary>
    public bool RelayOn { get; set; }

    /// <summary>False while the operator has taken this node offline with the D key.</summary>
    public bool Online { get; set; } = true;

    /// <summary>Any incident in progress.</summary>
    public DeviceMode Mode { get; set; }

    /// <summary>Ticks left in a power spike.</summary>
    public int SpikeTicks { get; set; }

    /// <summary>Per-node packet counter.</summary>
    public long Sequence { get; set; }

    public bool IsActuator => Category == "Actuator";

    public bool IsMoistureProbe => Category == "Environmental" && DisplayName.Contains("Moisture", StringComparison.OrdinalIgnoreCase);

    public bool IsPowerMeter => Category == "PowerConsumption";

    /// <summary>Whether this relay waters a bay - a pump or a valve.</summary>
    public bool Irrigates => IsActuator
                             && (DisplayName.Contains("Pump", StringComparison.OrdinalIgnoreCase)
                                 || DisplayName.Contains("Valve", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether this relay feeds lighting.</summary>
    public bool IsLightingRelay => IsActuator && DisplayName.Contains("Light", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this meter measures lighting.</summary>
    public bool IsLightingMeter => IsPowerMeter && DisplayName.Contains("Lighting", StringComparison.OrdinalIgnoreCase);

    /// <summary>The reading as the wire payload for its type.</summary>
    public string Payload() => PayloadType switch
    {
        "bool" => RelayOn ? "true" : "false",
        "int" => ((int)Math.Round(Value)).ToString(CultureInfo.InvariantCulture),
        _ => Value.ToString("0.###", CultureInfo.InvariantCulture)
    };

    /// <summary>The reading as an operator would read it.</summary>
    public string Describe() => Category switch
    {
        "Actuator" => RelayOn ? "ON" : "OFF",
        "PowerConsumption" => $"{Value:0} W",
        "Connectivity" => $"{Value:0} dBm",
        _ => $"{Value:0.0}%"
    };

    /// <summary>Builds the uplink packet for the current reading.</summary>
    public UplinkPacketRequest ToPacket(DateTimeOffset now) => new()
    {
        MacAddress = MacAddress,
        PayloadType = PayloadType,
        Value = Payload(),
        SequenceNumber = ++Sequence,
        TimestampUtc = now
    };
}
