using SmartX.Core.Devices;
using SmartX.Core.Mesh;

namespace SmartX.Core.Tests.Mesh;

/// <summary>Builds small, hand-checkable graphs for the mesh tests.</summary>
internal static class MeshTestGraphs
{
    /// <summary>The MAC for test vertex <paramref name="index"/>; 0 is the gateway.</summary>
    public static MacKey Mac(int index) => MacKey.FromValue(0xA4CF12000000UL + (ulong)index);

    /// <summary>
    /// A graph whose link costs are set exactly, by choosing the RSSI that gives
    /// each cost. Vertex 0 is the gateway.
    /// </summary>
    public static MeshGraph Weighted(int vertices, params (int A, int B, double Cost)[] links)
    {
        var graph = new MeshGraph();

        for (var i = 0; i < vertices; i++)
        {
            graph.AddVertex(new MeshVertex(Mac(i), i == 0 ? "Gateway" : $"Node {i}", i, 0, isGateway: i == 0));
        }

        foreach (var (a, b, cost) in links)
        {
            graph.Connect(Mac(a), Mac(b), RssiForCost(cost));
        }

        return graph;
    }

    /// <summary>
    /// A random geometric graph: nodes scattered over a square, linked wherever
    /// the path-loss model says they can hear each other. This is the shape a
    /// real mesh deployment has, which makes it the honest test input.
    /// </summary>
    public static MeshGraph RandomGeometric(int vertices, double sideMetres, int seed)
    {
        var random = new Random(seed);
        var graph = new MeshGraph();
        var placed = new List<MeshVertex>(vertices);

        for (var i = 0; i < vertices; i++)
        {
            var vertex = new MeshVertex(Mac(i), $"Node {i}", random.NextDouble() * sideMetres, random.NextDouble() * sideMetres, isGateway: i == 0);
            graph.AddVertex(vertex);
            placed.Add(vertex);
        }

        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                var distance = LinkCostModel.Distance(placed[i].X, placed[i].Y, placed[j].X, placed[j].Y);
                var rssi = LinkCostModel.RssiAt(distance);

                if (LinkCostModel.IsUsable(rssi))
                {
                    graph.Connect(placed[i].Mac, placed[j].Mac, rssi);
                }
            }
        }

        return graph;
    }

    /// <summary>The RSSI whose ETX cost is <paramref name="cost"/> - the model inverted.</summary>
    public static double RssiForCost(double cost)
    {
        // cost = 1 / ratio, ratio = 1 / (1 + e^-(rssi - mid) / slope)
        // => rssi = mid - slope * ln(cost - 1)
        var rssi = LinkCostModel.HalfDeliveryRssiDbm - (LinkCostModel.DeliverySlopeDb * Math.Log(cost - 1));
        return Math.Round(rssi, 1);
    }
}
