using System.ComponentModel.DataAnnotations;
using SmartX.Contracts.Validation;

namespace SmartX.Contracts.Stream;

/// <summary>
/// One reading as a device publishes it into the live stream.
/// <para>
/// Same payload rules as the Part 1 ingestion endpoint - the value travels as
/// text with a type discriminator so the gateway can build the right closed
/// <c>TelemetryPacket&lt;T&gt;</c> - plus the device's own alarm flag, for nodes
/// that run an on-board threshold.
/// </para>
/// </summary>
public sealed class UplinkPacketRequest
{
    /// <summary>Publishing node.</summary>
    [Required]
    [MacAddress]
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>"float", "int" or "bool".</summary>
    [Required]
    [RegularExpression("^(float|int|bool)$", ErrorMessage = "Payload type must be float, int or bool.")]
    public string PayloadType { get; set; } = "float";

    /// <summary>The reading, as text.</summary>
    [Required]
    [StringLength(32)]
    public string Value { get; set; } = string.Empty;

    /// <summary>Per-node sequence number.</summary>
    [Range(0, long.MaxValue)]
    public long SequenceNumber { get; set; }

    /// <summary>Capture time on the device. Defaults to arrival time.</summary>
    public DateTimeOffset? TimestampUtc { get; set; }

    /// <summary>True when the device's own threshold tripped.</summary>
    public bool Alarm { get; set; }
}

/// <summary>
/// A frame of readings forwarded by a mesh border router - the node at the root
/// of an ESP-NOW or Thread mesh that relays its neighbours' packets to the gateway
/// in one request instead of one each.
/// </summary>
public sealed class UplinkEnvelopeRequest
{
    /// <summary>Which border router forwarded the frame, for the logs.</summary>
    [StringLength(64)]
    public string? GatewayId { get; set; }

    /// <summary>The readings. Each is triaged and queued on its own.</summary>
    [Required]
    [MinLength(1, ErrorMessage = "An uplink frame needs at least one packet.")]
    [MaxLength(5000, ErrorMessage = "An uplink frame may carry at most 5000 packets.")]
    public List<UplinkPacketRequest> Packets { get; set; } = [];
}

/// <summary>What happened to one packet at the door.</summary>
public sealed class UplinkReceiptResponse
{
    /// <summary>Publishing node.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Whether the packet was queued.</summary>
    public bool Accepted { get; set; }

    /// <summary>"Standard" or "Priority".</summary>
    public string Lane { get; set; } = string.Empty;

    /// <summary>Arrival number across both lanes.</summary>
    public long Sequence { get; set; }

    /// <summary>Position in its lane when queued.</summary>
    public int Position { get; set; }

    /// <summary>Routine packets already waiting - the ones a priority packet jumps.</summary>
    public int StandardWaiting { get; set; }

    /// <summary>For a priority packet, why it was triaged critical; for a refusal, why it was refused.</summary>
    public string? Reason { get; set; }
}

/// <summary>The gateway's answer to an uplink frame.</summary>
public sealed class UplinkEnvelopeResponse
{
    /// <summary>Packets in the frame.</summary>
    public int Received { get; set; }

    /// <summary>Packets queued.</summary>
    public int Accepted { get; set; }

    /// <summary>Packets queued in the priority lane.</summary>
    public int Priority { get; set; }

    /// <summary>Packets refused - unknown device, unreadable value, or a full standard lane.</summary>
    public int Rejected { get; set; }

    /// <summary>The lookup that found each packet's device.</summary>
    public string LookupStrategy { get; set; } = string.Empty;

    /// <summary>Mean time per device lookup, in nanoseconds - flat whatever the fleet size.</summary>
    public double MeanLookupNanoseconds { get; set; }

    /// <summary>Devices in the registry when the frame arrived.</summary>
    public int FleetSize { get; set; }

    /// <summary>One receipt per packet, in frame order.</summary>
    public List<UplinkReceiptResponse> Receipts { get; set; } = [];
}

/// <summary>A device the emulator should drive, as the gateway's provisioning record describes it.</summary>
public sealed class ManifestDeviceResponse
{
    /// <summary>Node address.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Operator-facing label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Operational class.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>"float", "int" or "bool".</summary>
    public string PayloadType { get; set; } = string.Empty;

    /// <summary>Unit the device publishes in.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Facility and zone.</summary>
    public string Zone { get; set; } = string.Empty;

    /// <summary>The last value the gateway holds, so a restarted emulator carries on from it rather than jumping.</summary>
    public double? LastValue { get; set; }

    /// <summary>Expected seconds between packets.</summary>
    public int HeartbeatIntervalSeconds { get; set; }

    /// <summary>East-west position on the site plan, in metres, so the emulator can model radio range.</summary>
    public double? X { get; set; }

    /// <summary>North-south position on the site plan, in metres.</summary>
    public double? Y { get; set; }
}

/// <summary>The fleet the gateway has handed to the device emulator.</summary>
public sealed class DeviceManifestResponse
{
    /// <summary>The border router's MAC, which devices list as a neighbour when in range of it.</summary>
    public string? GatewayMac { get; set; }

    /// <summary>The border router's east-west position on the site plan, in metres.</summary>
    public double? GatewayX { get; set; }

    /// <summary>The border router's north-south position on the site plan, in metres.</summary>
    public double? GatewayY { get; set; }

    /// <summary>Devices to drive.</summary>
    public List<ManifestDeviceResponse> Devices { get; set; } = [];

    /// <summary>Registered devices that are deliberately not handed over, e.g. the silent node.</summary>
    public int Withheld { get; set; }
}

/// <summary>One command a device collects from its downlink mailbox.</summary>
public sealed class DownlinkCommandResponse
{
    /// <summary>The key the device acknowledges against.</summary>
    public Guid CommandId { get; set; }

    /// <summary>Device the command is for.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Relay state to apply.</summary>
    public bool DesiredState { get; set; }

    /// <summary>When the operator issued it.</summary>
    public DateTimeOffset IssuedUtc { get; set; }

    /// <summary>"Operator", "Undo", "Redo" or "Suggestion".</summary>
    public string Origin { get; set; } = string.Empty;
}

/// <summary>Commands collected by a downlink poll.</summary>
public sealed class DownlinkResponse
{
    /// <summary>Commands now marked delivered, oldest first per device.</summary>
    public List<DownlinkCommandResponse> Commands { get; set; } = [];

    /// <summary>How long the device should wait before polling again.</summary>
    public double PollAfterSeconds { get; set; } = 1d;
}

/// <summary>A device confirming it applied a command.</summary>
public sealed class CommandAckRequest
{
    /// <summary>The state the relay is now in.</summary>
    public bool AppliedState { get; set; }
}
