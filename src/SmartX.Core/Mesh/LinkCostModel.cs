namespace SmartX.Core.Mesh;

/// <summary>
/// Turns radio physics into the edge weights the routing algorithms minimise.
/// <para>
/// <b>Signal strength from distance.</b> The log-distance path-loss model:
/// <c>RSSI(d) = RSSI(1 m) - 10 n log10(d)</c>. An ESP32 at 0 dBm transmit power
/// reads about -40 dBm at one metre, and <c>n</c> = 2.7 is a typical exponent for
/// an indoor space full of plants, racks and water (free space would be 2).
/// </para>
/// <para>
/// <b>Cost from signal strength.</b> A weak link does not fail outright; it drops
/// a growing share of packets, and every dropped packet is sent again. The cost
/// used here is the link's <b>ETX</b> - its expected transmission count, 1 / the
/// packet delivery ratio - which is the metric mesh protocols such as Zigbee and
/// Thread derive their link costs from. Because each transmission costs the radio
/// roughly the same energy, ETX is directly proportional to the energy spent
/// moving a packet across the link, which is what makes the minimum spanning tree
/// over these weights the most power-efficient way to connect the mesh.
/// </para>
/// <para>
/// The delivery ratio is modelled as a logistic curve of RSSI: near-perfect above
/// about -80 dBm, half the packets lost at -87 dBm, and no usable link below the
/// -90 dBm floor - the same floor the Part 2 link-loss alert uses.
/// </para>
/// </summary>
public static class LinkCostModel
{
    /// <summary>Received signal at one metre, in dBm.</summary>
    public const double ReferenceRssiDbm = -40d;

    /// <summary>Path-loss exponent for a cluttered indoor growing space.</summary>
    public const double PathLossExponent = 2.7d;

    /// <summary>RSSI at which half of all packets are lost.</summary>
    public const double HalfDeliveryRssiDbm = -87d;

    /// <summary>How sharply delivery falls away around <see cref="HalfDeliveryRssiDbm"/>, in dB.</summary>
    public const double DeliverySlopeDb = 2d;

    /// <summary>Weakest signal a link can form at; matches <c>Stream:Bands:LinkFloorDbm</c>.</summary>
    public const double DefaultFloorDbm = -90d;

    /// <summary>Signal strength expected at <paramref name="distanceMetres"/>.</summary>
    public static double RssiAt(double distanceMetres)
        => ReferenceRssiDbm - (10d * PathLossExponent * Math.Log10(Math.Max(1d, distanceMetres)));

    /// <summary>Fraction of packets that arrive at this signal strength, 0 to 1.</summary>
    public static double DeliveryRatio(double rssiDbm)
        => 1d / (1d + Math.Exp(-(rssiDbm - HalfDeliveryRssiDbm) / DeliverySlopeDb));

    /// <summary>
    /// Expected transmissions per delivered packet - the link's routing cost.
    /// 1.0 is a perfect link; every unit above it is a retransmission's worth of energy.
    /// </summary>
    public static double Cost(double rssiDbm) => Math.Round(1d / DeliveryRatio(rssiDbm), 3);

    /// <summary>Whether a link can form at this signal strength.</summary>
    public static bool IsUsable(double rssiDbm, double floorDbm = DefaultFloorDbm) => rssiDbm >= floorDbm;

    /// <summary>The longest link the floor allows, in metres - about 71 m at the defaults.</summary>
    public static double MaxRangeMetres(double floorDbm = DefaultFloorDbm)
        => Math.Pow(10d, (ReferenceRssiDbm - floorDbm) / (10d * PathLossExponent));

    /// <summary>Straight-line distance between two positions on the site plan, in metres.</summary>
    public static double Distance(double x1, double y1, double x2, double y2)
        => Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
}
