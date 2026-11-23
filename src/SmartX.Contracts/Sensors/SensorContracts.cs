using System.ComponentModel.DataAnnotations;
using SmartX.Contracts.Validation;

namespace SmartX.Contracts.Sensors;

/// <summary>
/// Registers a node with the gateway.
/// <para>
/// Every rule here is enforced during model binding, so a malformed registration
/// is rejected with a field-level message before any gateway state is touched.
/// The Blazor client validates this same type in the browser, which is what makes
/// client-side and server-side validation agree by construction rather than by
/// discipline.
/// </para>
/// </summary>
public sealed class RegisterSensorRequest
{
    /// <summary>Device MAC address, in any form the firmware reports.</summary>
    [Required(ErrorMessage = "A MAC address is required to identify the device.")]
    [MacAddress]
    [Display(Name = "MAC address")]
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Operator-facing label, e.g. "Hydro Bay 3 - Moisture".</summary>
    [Required(ErrorMessage = "Give the sensor a name operators will recognise.")]
    [StringLength(80, MinimumLength = 3)]
    [Display(Name = "Display name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Operational class of the node.</summary>
    [Required(ErrorMessage = "Select a sensor category.")]
    [AllowedValuesFrom("Categories")]
    public string Category { get; set; } = "Environmental";

    /// <summary>Unit this node publishes in.</summary>
    [Required(ErrorMessage = "Select the unit this sensor reports in.")]
    [AllowedValuesFrom("Units")]
    public string Unit { get; set; } = "Percent";

    /// <summary>Top-level site, e.g. "Facility A".</summary>
    [Required(ErrorMessage = "A facility is required.")]
    [StringLength(60)]
    public string Facility { get; set; } = string.Empty;

    /// <summary>Zone within the facility, e.g. "Zone 1".</summary>
    [Required(ErrorMessage = "A zone is required.")]
    [StringLength(60)]
    public string Zone { get; set; } = string.Empty;

    /// <summary>
    /// Optional sub-zone, e.g. "Sub-Zone B".
    /// <para>
    /// Declared nullable on purpose. Under an enabled nullable context, ASP.NET adds
    /// an implicit Required rule to every non-nullable reference property, so a
    /// genuinely optional field typed as plain string is rejected when left blank -
    /// which is exactly what happened here. Making the type nullable is how the
    /// framework is told this one really is optional.
    /// </para>
    /// </summary>
    [StringLength(60)]
    [Display(Name = "Sub-zone")]
    public string? SubZone { get; set; }

    /// <summary>Node label, e.g. "NODE-07".</summary>
    [Required(ErrorMessage = "A node identifier is required.")]
    [StringLength(40)]
    [Display(Name = "Node ID")]
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Reported firmware build.</summary>
    [StringLength(20)]
    [RegularExpression(@"^\d+\.\d+\.\d+$", ErrorMessage = "Firmware version must look like 1.4.2.")]
    [Display(Name = "Firmware version")]
    public string FirmwareVersion { get; set; } = "1.0.0";

    /// <summary>Expected seconds between heartbeats. Exceeding it marks the node silent.</summary>
    [Range(1, 3600, ErrorMessage = "Heartbeat interval must be between 1 and 3600 seconds.")]
    [Display(Name = "Heartbeat interval (seconds)")]
    public int HeartbeatIntervalSeconds { get; set; } = 30;
}

/// <summary>A registered node as the API reports it.</summary>
public sealed class SensorResponse
{
    /// <summary>Gateway-assigned key.</summary>
    public Guid Id { get; set; }

    /// <summary>Normalised MAC address, upper-case colon form.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Operator-facing label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Operational class.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Unit this node publishes in.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Health state, derived from heartbeat freshness.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Top-level site.</summary>
    public string Facility { get; set; } = string.Empty;

    /// <summary>Zone within the facility.</summary>
    public string Zone { get; set; } = string.Empty;

    /// <summary>Sub-zone, if any.</summary>
    public string SubZone { get; set; } = string.Empty;

    /// <summary>Node label.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Full deployment path, e.g. "Facility A / Zone 1 / Sub-Zone B / NODE-07".</summary>
    public string QualifiedPath { get; set; } = string.Empty;

    /// <summary>Reported firmware build.</summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>Expected seconds between heartbeats.</summary>
    public int HeartbeatIntervalSeconds { get; set; }

    /// <summary>When registration was accepted.</summary>
    public DateTimeOffset RegisteredAtUtc { get; set; }

    /// <summary>Most recent accepted packet, if any.</summary>
    public DateTimeOffset? LastSeenUtc { get; set; }

    /// <summary>Seconds since the last packet, or null if the node has never reported.</summary>
    public double? SecondsSinceLastSeen { get; set; }

    /// <summary>Packets accepted from this node.</summary>
    public long PacketsReceived { get; set; }

    /// <summary>Files attached to this sensor profile.</summary>
    public int AttachmentCount { get; set; }

    /// <summary>Latest reading, formatted with its unit symbol.</summary>
    public string? LatestReading { get; set; }
}

/// <summary>A page of sensors, plus the search cost that produced it.</summary>
public sealed class SensorSearchResponse
{
    /// <summary>The matching sensors.</summary>
    public List<SensorResponse> Sensors { get; set; } = [];

    /// <summary>How many sensors are registered in total.</summary>
    public int TotalRegistered { get; set; }

    /// <summary>Records the scan had to look at. Shown in the client to make cost visible.</summary>
    public int ComparisonsUsed { get; set; }

    /// <summary>Which strategy answered the query - "binary" or "linear".</summary>
    public string Strategy { get; set; } = "linear";
}
