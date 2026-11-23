using SmartX.Core.Mesh;
using static SmartX.Core.Tests.Mesh.MeshTestGraphs;

namespace SmartX.Core.Tests.Mesh;

/// <summary>Covers the adjacency-list graph and the radio cost model behind its edge weights.</summary>
public class MeshGraphTests
{
    [Fact]
    public void Connect_StoresOneLinkUnderBothEndpoints()
    {
        var graph = Weighted(3);

        var link = graph.Connect(Mac(1), Mac(2), -70);

        Assert.Equal(1, graph.LinkCount);
        Assert.Same(link, graph.LinkBetween(Mac(1), Mac(2)));
        Assert.Same(link, graph.LinkBetween(Mac(2), Mac(1)));
        Assert.Equal(Mac(2), link.Other(Mac(1)));
        Assert.Single(graph.Links);
    }

    [Fact]
    public void Connect_Twice_KeepsOneLink()
    {
        var graph = Weighted(3);

        var first = graph.Connect(Mac(1), Mac(2), -70);
        var second = graph.Connect(Mac(2), Mac(1), -80);

        Assert.Same(first, second);
        Assert.Equal(1, graph.LinkCount);
    }

    [Fact]
    public void Connect_UnknownVertex_Throws()
    {
        var graph = Weighted(2);

        Assert.Throws<KeyNotFoundException>(() => graph.Connect(Mac(1), Mac(9), -70));
    }

    [Fact]
    public void SelfLink_IsRejected()
    {
        var graph = Weighted(2);

        Assert.Throws<ArgumentException>(() => graph.Connect(Mac(1), Mac(1), -70));
    }

    [Fact]
    public void RemoveVertex_DropsEveryIncidentLink()
    {
        var graph = Weighted(4, (0, 1, 1.5), (1, 2, 1.5), (1, 3, 1.5), (2, 3, 1.5));

        Assert.True(graph.RemoveVertex(Mac(1)));

        Assert.Equal(3, graph.VertexCount);
        Assert.Equal(1, graph.LinkCount);
        Assert.Null(graph.LinkBetween(Mac(0), Mac(1)));
        Assert.Equal(0, graph.DegreeOf(Mac(0)));
    }

    [Fact]
    public void SecondGateway_IsRejected()
    {
        var graph = Weighted(2);

        Assert.Throws<InvalidOperationException>(() => graph.AddVertex(new MeshVertex(Mac(7), "Rogue", 0, 0, isGateway: true)));
    }

    [Fact]
    public void LiveNeighbours_SkipDownVertices()
    {
        var graph = Weighted(4, (0, 1, 1.5), (0, 2, 1.5), (0, 3, 1.5));
        graph.Find(Mac(2))!.IsUp = false;

        var live = graph.LiveNeighbours(Mac(0)).Select(pair => pair.Neighbour).ToList();

        Assert.Equal([Mac(1), Mac(3)], live);
        Assert.Empty(graph.LiveNeighbours(Mac(2)));
    }

    [Fact]
    public void Report_SmoothsSignalWithAMovingAverage()
    {
        var graph = Weighted(2);
        var link = graph.Connect(Mac(0), Mac(1), -70);
        var now = DateTimeOffset.UtcNow;

        link.Report(-80, now);
        Assert.Equal(-80, link.RssiDbm);

        link.Report(-90, now.AddSeconds(30));

        // 0.3 x -90 + 0.7 x -80: one bad reading moves the link, it does not flip it.
        Assert.Equal(-83, link.RssiDbm);
        Assert.Equal(2, link.Reports);
        Assert.Equal(now.AddSeconds(30), link.LastHeardUtc);
    }

    [Fact]
    public void Cost_RisesAsSignalWeakens()
    {
        double[] signals = [-60, -75, -80, -84, -87, -89, -90];
        var costs = signals.Select(LinkCostModel.Cost).ToList();

        Assert.Equal(costs.Order(), costs);
        Assert.InRange(costs[0], 1.0, 1.001);
        Assert.Equal(2.0, LinkCostModel.Cost(LinkCostModel.HalfDeliveryRssiDbm), precision: 3);
        Assert.True(costs[^1] > 5);
    }

    [Fact]
    public void RssiAt_FallsWithDistanceAndRespectsTheFloor()
    {
        Assert.Equal(LinkCostModel.ReferenceRssiDbm, LinkCostModel.RssiAt(1));
        Assert.True(LinkCostModel.RssiAt(10) > LinkCostModel.RssiAt(40));

        var range = LinkCostModel.MaxRangeMetres();
        Assert.InRange(range, 70, 72);
        Assert.True(LinkCostModel.IsUsable(LinkCostModel.RssiAt(range - 0.5)));
        Assert.False(LinkCostModel.IsUsable(LinkCostModel.RssiAt(range + 0.5)));
    }
}
