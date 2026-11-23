using System.ComponentModel.DataAnnotations;
using SmartX.Contracts.Validation;

namespace SmartX.Contracts.Topology;

/// <summary>One entry of a device's neighbour table: a node it can hear, and how well.</summary>
public sealed class NeighbourEntryRequest
{
    /// <summary>The neighbour's address.</summary>
    [Required]
    [MacAddress]
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Signal strength the device hears the neighbour at, in dBm.</summary>
    [Range(-120d, 0d, ErrorMessage = "RSSI must be between -120 and 0 dBm.")]
    public double RssiDbm { get; set; }
}

/// <summary>
/// A device's neighbour table, as mesh nodes advertise it - the role of a Zigbee
/// Link Status message or a Thread MLE advertisement.
/// </summary>
public sealed class NeighbourReportRequest
{
    /// <summary>The reporting device.</summary>
    [Required]
    [MacAddress]
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Neighbours it hears.</summary>
    [Required]
    [MaxLength(64, ErrorMessage = "A neighbour table may list at most 64 neighbours.")]
    public List<NeighbourEntryRequest> Neighbours { get; set; } = [];
}

/// <summary>Neighbour tables forwarded together by a border router.</summary>
public sealed class NeighbourEnvelopeRequest
{
    /// <summary>The forwarding border router.</summary>
    [StringLength(64)]
    public string? GatewayId { get; set; }

    /// <summary>The tables.</summary>
    [Required]
    [MinLength(1, ErrorMessage = "An envelope needs at least one report.")]
    [MaxLength(1000, ErrorMessage = "An envelope may carry at most 1000 reports.")]
    public List<NeighbourReportRequest> Reports { get; set; } = [];
}

/// <summary>What the gateway did with a batch of neighbour tables.</summary>
public sealed class NeighbourEnvelopeResponse
{
    /// <summary>Reports from known devices.</summary>
    public int ReportsAccepted { get; set; }

    /// <summary>Existing links whose signal strength was updated.</summary>
    public int LinksUpdated { get; set; }

    /// <summary>Links heard for the first time and added to the graph.</summary>
    public int LinksDiscovered { get; set; }

    /// <summary>Entries ignored: an unknown device, or a new link too weak to form.</summary>
    public int EntriesIgnored { get; set; }
}
