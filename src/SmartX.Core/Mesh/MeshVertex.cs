using SmartX.Core.Devices;

namespace SmartX.Core.Mesh;

/// <summary>A node in the mesh graph: an ESP32 sensor or actuator, or the central gateway.</summary>
public sealed class MeshVertex
{
    /// <summary>Creates a vertex.</summary>
    public MeshVertex(MacKey mac, string name, double x, double y, bool isGateway = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Mac = mac;
        Name = name;
        X = x;
        Y = y;
        IsGateway = isGateway;
    }

    /// <summary>The node's address, which is also its vertex key.</summary>
    public MacKey Mac { get; }

    /// <summary>Operator-facing label.</summary>
    public string Name { get; set; }

    /// <summary>East-west position on the site plan, in metres.</summary>
    public double X { get; set; }

    /// <summary>North-south position on the site plan, in metres.</summary>
    public double Y { get; set; }

    /// <summary>True for the border router every packet is trying to reach.</summary>
    public bool IsGateway { get; }

    /// <summary>
    /// Whether the node is up. Set from heartbeat health before each calculation;
    /// a down node is still drawn, but no route may pass through it.
    /// </summary>
    public bool IsUp { get; set; } = true;
}

/// <summary>
/// A wireless link between two nodes. Undirected: the graph stores the same link
/// object under both endpoints, so updating it from either side updates both.
/// </summary>
public sealed class MeshLink
{
    /// <summary>Creates a link, ordering the endpoints so each pair has one canonical form.</summary>
    public MeshLink(MacKey a, MacKey b, double rssiDbm)
    {
        if (a == b)
        {
            throw new ArgumentException("A node cannot link to itself.", nameof(b));
        }

        (A, B) = a < b ? (a, b) : (b, a);
        SetRssi(rssiDbm);
    }

    /// <summary>The endpoint with the lower MAC.</summary>
    public MacKey A { get; }

    /// <summary>The endpoint with the higher MAC.</summary>
    public MacKey B { get; }

    /// <summary>Received signal strength, in dBm, smoothed across neighbour reports.</summary>
    public double RssiDbm { get; private set; }

    /// <summary>Expected transmissions per delivered packet: the routing cost. See <see cref="LinkCostModel"/>.</summary>
    public double Cost { get; private set; }

    /// <summary>When a device last reported hearing this link, or null for a surveyed link never reported.</summary>
    public DateTimeOffset? LastHeardUtc { get; private set; }

    /// <summary>Neighbour reports that have mentioned this link.</summary>
    public int Reports { get; private set; }

    /// <summary>The other endpoint.</summary>
    public MacKey Other(MacKey end) => end == A ? B : A;

    /// <summary>
    /// Folds a new RSSI report into the link with an exponentially weighted moving
    /// average. Radio readings jitter by several dB from one report to the next; the
    /// average stops that jitter from flipping the spanning tree back and forth
    /// between two near-equal routes.
    /// </summary>
    /// <param name="rssiDbm">The reported signal strength.</param>
    /// <param name="heardUtc">When it was reported.</param>
    /// <param name="weight">Weight of the new report, 0 to 1.</param>
    public void Report(double rssiDbm, DateTimeOffset heardUtc, double weight = 0.3)
    {
        SetRssi(Reports == 0 && LastHeardUtc is null ? rssiDbm : (weight * rssiDbm) + ((1 - weight) * RssiDbm));
        LastHeardUtc = heardUtc;
        Reports++;
    }

    private void SetRssi(double rssiDbm)
    {
        RssiDbm = Math.Round(rssiDbm, 1);
        Cost = LinkCostModel.Cost(RssiDbm);
    }
}
