namespace SmartX.Contracts;

/// <summary>
/// The vocabulary the wire format uses for sensor categories, units and statuses.
/// <para>
/// These are plain strings rather than enums shared from the domain library, and
/// that is deliberate. A DTO assembly with no project references is what keeps the
/// Blazor client honest: it can describe a request and read a response, but it has no
/// way to reach the gateway's data structures except over HTTP. It also means
/// adding a domain enum member cannot silently change the shape of the published
/// API - the mapping has to be updated on purpose.
/// </para>
/// </summary>
public static class SensorVocabulary
{
    /// <summary>Sensor categories accepted by the registration endpoint.</summary>
    public static readonly IReadOnlyList<string> Categories =
    [
        "Environmental",
        "PowerConsumption",
        "Actuator",
        "Connectivity"
    ];

    /// <summary>Telemetry units accepted by the ingestion endpoints.</summary>
    public static readonly IReadOnlyList<string> Units =
    [
        "None",
        "Celsius",
        "Percent",
        "Watt",
        "Volt",
        "Ampere",
        "Ppm",
        "Dbm",
        "State"
    ];

    /// <summary>Health states a sensor can report.</summary>
    public static readonly IReadOnlyList<string> Statuses =
    [
        "Online",
        "Degraded",
        "Silent",
        "Decommissioned"
    ];

    /// <summary>Deployment tiers used by the configuration validator.</summary>
    public static readonly IReadOnlyList<string> NodeKinds =
    [
        "Facility",
        "Zone",
        "SubZone",
        "Device"
    ];

    /// <summary>The unit a category is expected to publish in, used to pre-fill the UI.</summary>
    public static string DefaultUnitFor(string? category) => category switch
    {
        "PowerConsumption" => "Watt",
        "Actuator" => "State",
        "Connectivity" => "Dbm",
        _ => "Percent"
    };

    /// <summary>A friendlier label for a category, for display in the client.</summary>
    public static string DisplayName(string? category) => category switch
    {
        "PowerConsumption" => "Power Consumption",
        "Environmental" => "Environmental",
        "Actuator" => "Actuator",
        "Connectivity" => "Connectivity",
        _ => category ?? string.Empty
    };
}
