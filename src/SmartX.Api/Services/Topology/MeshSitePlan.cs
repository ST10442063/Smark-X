namespace SmartX.Api.Services.Topology;

/// <summary>A facility's outline on the site plan, in metres.</summary>
/// <param name="Name">Facility name, as sensors report it in their location.</param>
/// <param name="X">West edge.</param>
/// <param name="Y">South edge.</param>
/// <param name="Width">East-west extent.</param>
/// <param name="Height">North-south extent.</param>
public sealed record SiteArea(string Name, double X, double Y, double Width, double Height)
{
    /// <summary>The middle of the area, where newly registered nodes are placed around.</summary>
    public (double X, double Y) Centre => (X + (Width / 2), Y + (Height / 2));
}

/// <summary>
/// Where every node physically sits: the site survey the mesh graph is built from.
/// <para>
/// The seeded fleet is placed by hand, the way an installer would mark nodes on a
/// floor plan, and the placement is deliberate. Link quality is derived from the
/// distances, so the layout decides the graph, and this one is laid out to show
/// the two behaviours the final PoE is about:
/// </para>
/// <list type="bullet">
/// <item><b>Self-healing.</b> Facility B is reached through Mesh Repeater 08 at the
/// east edge of Facility A. There is a fallback - Canopy Humidity 02 can just about
/// hear Pump Feed Meter 04 across the gap - but it is weak. Fail the repeater and
/// Facilities B and C reroute over that fallback at a visibly higher ETX.</item>
/// <item><b>A single point of failure.</b> Irrigation Valve 06 sits at the far end of
/// Facility C and only Pump Feed Meter 12 is in range of it. Meter 12 is therefore
/// an articulation point: fail it and the valve is stranded.</item>
/// </list>
/// <para>
/// Nodes registered from the console are placed automatically on a spiral around
/// their facility's centre (or the gateway, for a facility not on the plan), so
/// they join the mesh wherever they are in range of something.
/// </para>
/// </summary>
public static class MeshSitePlan
{
    /// <summary>The border router's MAC: the root every route leads to.</summary>
    public const string GatewayMac = "A4:CF:12:FE:00:01";

    /// <summary>The border router's label.</summary>
    public const string GatewayName = "Central Gateway (border-router-01)";

    /// <summary>Site plan width, in metres.</summary>
    public const double Width = 250;

    /// <summary>Site plan height, in metres.</summary>
    public const double Height = 100;

    /// <summary>The border router's position: the west side of Facility A, where the uplink enters.</summary>
    public static readonly (double X, double Y) GatewayPosition = (10, 50);

    /// <summary>The three facilities.</summary>
    public static readonly IReadOnlyList<SiteArea> Areas =
    [
        new("Facility A", 0, 0, 95, 100),
        new("Facility B", 102, 0, 66, 100),
        new("Facility C", 175, 0, 72, 100)
    ];

    /// <summary>
    /// Surveyed positions of the seeded nodes, keyed by the node id the seeder
    /// gives each one (its <c>DeploymentLocation.NodeId</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (double X, double Y)> Surveyed = new Dictionary<string, (double, double)>
    {
        // Facility A, Zone 1, Sub-Zone A - the south-west growing bay.
        ["NODE-13"] = (30, 12),   // Lighting Rail Meter 13
        ["ACT-03"] = (20, 25),    // Grow Light Relay
        ["NODE-01"] = (38, 28),   // Soil Moisture Probe 01
        ["ACT-01"] = (48, 18),    // Water Pump A
        ["NODE-07"] = (50, 32),   // Circulation Fan Relay 07

        // Facility A, Zone 1, Sub-Zone B - the middle, out to the east wall.
        ["NODE-02"] = (52, 47),   // Canopy Humidity 02 - the weak fallback to Facility B
        ["NODE-14"] = (66, 62),   // Irrigation Valve 14 - never reports, always down
        ["NODE-08"] = (85, 50),   // Mesh Repeater 08 - the bridge to Facility B

        // Facility A, Zone 2 - the north bay.
        ["NODE-09"] = (22, 78),   // Soil Moisture Probe 09
        ["NODE-03"] = (40, 86),   // Nutrient EC Probe 03
        ["ACT-02"] = (58, 80),    // Water Pump B

        // Facility B.
        ["NODE-04"] = (115, 40),  // Pump Feed Meter 04 - first hop into B
        ["NODE-10"] = (120, 15),  // Canopy Humidity 10
        ["NODE-05"] = (140, 70),  // Lighting Rail Meter 05
        ["NODE-11"] = (155, 40),  // Nutrient EC Probe 11

        // Facility C.
        ["NODE-12"] = (185, 45),  // Pump Feed Meter 12 - the only relay for valve 06
        ["NODE-06"] = (235, 45)   // Irrigation Valve 06 - at the far end
    };

    /// <summary>The area named <paramref name="facility"/>, or null when it is not on the plan.</summary>
    public static SiteArea? AreaFor(string? facility)
        => Areas.FirstOrDefault(area => string.Equals(area.Name, facility?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A position for the <paramref name="ordinal"/>th node placed automatically in a
    /// facility: a golden-angle spiral, which spreads points evenly without any two
    /// landing on top of each other, clamped inside the facility.
    /// </summary>
    public static (double X, double Y) AutoPlace(string? facility, int ordinal)
    {
        var area = AreaFor(facility);
        var (centreX, centreY) = area?.Centre ?? (GatewayPosition.X + 12, GatewayPosition.Y);

        var angle = ordinal * 137.508 * Math.PI / 180;
        var radius = 8 + (7 * Math.Sqrt(ordinal));
        var x = centreX + (radius * Math.Cos(angle));
        var y = centreY + (radius * Math.Sin(angle));

        if (area is not null)
        {
            x = Math.Clamp(x, area.X + 3, area.X + area.Width - 3);
            y = Math.Clamp(y, area.Y + 3, area.Y + area.Height - 3);
        }
        else
        {
            x = Math.Clamp(x, 3, Width - 3);
            y = Math.Clamp(y, 3, Height - 3);
        }

        return (Math.Round(x, 1), Math.Round(y, 1));
    }
}
