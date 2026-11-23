using SmartX.Core.Devices;
using SmartX.Core.Mesh;
using static SmartX.Core.Tests.Mesh.MeshTestGraphs;

namespace SmartX.Core.Tests.Mesh;

/// <summary>
/// Covers route tracing and the self-healing report on a small site shaped like
/// the real one: a cheap bridge between two halves with a weaker fallback, and a
/// node at the edge hanging off a single relay.
/// </summary>
public class MeshRoutePlannerTests
{
    // 0 gateway - 1 - 2 bridge - 3 - 4 relay - 5 edge node
    //              \_____________/  fallback 1-3, costlier than going through 2
    private static MeshGraph Site() => Weighted(6,
        (0, 1, 1.1), (1, 2, 1.1), (2, 3, 1.1), (1, 3, 3.0), (3, 4, 1.2), (4, 5, 1.3));

    [Fact]
    public void Trace_FollowsTheTreeToTheGateway()
    {
        var (graph, baseline, live, reach) = Solve(Site());

        var trace = MeshRoutePlanner.Trace(graph, baseline, live, reach, Mac(5));

        Assert.True(trace.IsReachable);
        Assert.False(trace.IsDown);
        Assert.Equal([Mac(5), Mac(4), Mac(3), Mac(2), Mac(1)], trace.TreeRoute.Select(hop => hop.From));
        Assert.Equal(Mac(0), trace.TreeRoute[^1].To);
        Assert.Equal(live.PathCost[Mac(5)], trace.TreeCost, precision: 6);
        Assert.Equal(Mac(4), trace.CurrentNextHop);
        Assert.Empty(trace.BlockedBy);
    }

    [Fact]
    public void Trace_FewestHops_CanDifferFromTheCheapestTree()
    {
        var (graph, baseline, live, reach) = Solve(Site());

        var trace = MeshRoutePlanner.Trace(graph, baseline, live, reach, Mac(3));

        // BFS takes the fallback because it is one hop shorter; the tree avoids it
        // because the two-hop detour through the bridge costs fewer transmissions.
        Assert.Equal(2, trace.FewestHopsRoute.Count);
        Assert.Equal(3, trace.TreeRoute.Count);
        Assert.True(trace.TreeCost < trace.FewestHopsCost);
    }

    [Fact]
    public void BridgeDown_ReroutesOverTheFallbackAtACost()
    {
        var graph = Site();
        graph.Find(Mac(2))!.IsUp = false;
        var (_, baseline, live, reach) = Solve(graph);

        var report = MeshRoutePlanner.Heal(graph, baseline, live, reach);

        var reroute = Assert.Single(report.Reroutes);
        Assert.Equal(Mac(3), reroute.Node);
        Assert.Equal(Mac(2), reroute.BaselineNextHop);
        Assert.Equal(Mac(1), reroute.CurrentNextHop);
        Assert.True(reroute.NextHopFailed);
        Assert.True(reroute.CostDelta > 0);
        Assert.Empty(report.Stranded);

        var failed = Assert.Single(report.Failed);
        Assert.Equal([Mac(3), Mac(4), Mac(5)], failed.Dependents);
        Assert.Equal(failed.Dependents, failed.Rerouted);

        // Nodes downstream of 3 keep their next hop but inherit the detour's cost.
        Assert.True(report.DetourCost > 0);
        Assert.True(live.PathCost[Mac(5)] > baseline.PathCost[Mac(5)]);
    }

    [Fact]
    public void RelayDown_StrandsTheNodeBehindIt()
    {
        var graph = Site();
        graph.Find(Mac(4))!.IsUp = false;
        var (_, baseline, live, reach) = Solve(graph);

        var report = MeshRoutePlanner.Heal(graph, baseline, live, reach);

        var stranded = Assert.Single(report.Stranded);
        Assert.Equal(Mac(5), stranded.Node);
        Assert.Equal([Mac(4)], stranded.BlockedBy);
        Assert.Equal([Mac(5)], Assert.Single(report.Failed).Stranded);

        var trace = MeshRoutePlanner.Trace(graph, baseline, live, reach, Mac(5));
        Assert.False(trace.IsReachable);
        Assert.Empty(trace.TreeRoute);
        Assert.Equal([Mac(4)], trace.BlockedBy);
    }

    [Fact]
    public void EverythingUp_IsHealthy()
    {
        var (graph, baseline, live, reach) = Solve(Site());

        var report = MeshRoutePlanner.Heal(graph, baseline, live, reach);

        Assert.True(report.IsHealthy);
        Assert.Equal(report.BaselineTreeCost, report.CurrentTreeCost);
        Assert.Equal(0, report.DetourCost);
    }

    [Fact]
    public void Trace_Gateway_HasAnEmptyRoute()
    {
        var (graph, baseline, live, reach) = Solve(Site());

        var trace = MeshRoutePlanner.Trace(graph, baseline, live, reach, Mac(0));

        Assert.True(trace.IsReachable);
        Assert.Empty(trace.TreeRoute);
        Assert.Equal(0, trace.TreeCost);
    }

    private static (MeshGraph, SpanningTreeResult Baseline, SpanningTreeResult Live, ReachabilityResult Reach) Solve(MeshGraph graph)
    {
        var baseline = PrimMst.Build(graph, Mac(0), _ => true);
        var live = PrimMst.Build(graph, Mac(0));
        var reach = MeshTraversal.BreadthFirst(graph, Mac(0));
        return (graph, baseline, live, reach);
    }
}
