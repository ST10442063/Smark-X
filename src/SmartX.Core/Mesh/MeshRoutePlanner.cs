using SmartX.Core.Devices;

namespace SmartX.Core.Mesh;

/// <summary>One hop of a route: a packet crossing one link towards the gateway.</summary>
/// <param name="From">The node sending.</param>
/// <param name="To">The node receiving - one hop closer to the gateway.</param>
/// <param name="RssiDbm">Signal strength of the link.</param>
/// <param name="Cost">ETX of the link.</param>
/// <param name="CumulativeCost">ETX from the route's start to the end of this hop.</param>
public readonly record struct RouteHop(MacKey From, MacKey To, double RssiDbm, double Cost, double CumulativeCost);

/// <summary>Everything the console shows when an operator traces one node's route.</summary>
public sealed class RouteTrace
{
    /// <summary>The traced node.</summary>
    public required MacKey Node { get; init; }

    /// <summary>True when the node itself is down.</summary>
    public required bool IsDown { get; init; }

    /// <summary>True when the node has a live route to the gateway.</summary>
    public required bool IsReachable { get; init; }

    /// <summary>The route the spanning tree uses: the power-efficient backbone path.</summary>
    public required IReadOnlyList<RouteHop> TreeRoute { get; init; }

    /// <summary>The fewest-hops route breadth-first search found, for comparison.</summary>
    public required IReadOnlyList<RouteHop> FewestHopsRoute { get; init; }

    /// <summary>The next hop the node used with every node up.</summary>
    public MacKey? BaselineNextHop { get; init; }

    /// <summary>The next hop the node uses now.</summary>
    public MacKey? CurrentNextHop { get; init; }

    /// <summary>Down nodes on the node's normal route - why it is cut off, when it is.</summary>
    public required IReadOnlyList<MacKey> BlockedBy { get; init; }

    /// <summary>ETX of the tree route.</summary>
    public double TreeCost => TreeRoute.Count == 0 ? 0 : TreeRoute[^1].CumulativeCost;

    /// <summary>ETX of the fewest-hops route.</summary>
    public double FewestHopsCost => FewestHopsRoute.Count == 0 ? 0 : FewestHopsRoute[^1].CumulativeCost;
}

/// <summary>A node whose route changed because the mesh healed around a failure.</summary>
/// <param name="Node">The node.</param>
/// <param name="BaselineNextHop">Its next hop with every node up.</param>
/// <param name="CurrentNextHop">Its next hop now: where it hops instead.</param>
/// <param name="BaselineLinkCost">ETX of its uplink to the baseline next hop.</param>
/// <param name="CurrentLinkCost">ETX of its uplink to the current next hop.</param>
/// <param name="NextHopFailed">True when the old next hop is itself down, rather than the tree re-optimising around a failure elsewhere.</param>
/// <remarks>
/// The cost compared is the node's own uplink, not its whole path. In the
/// aggregation model the spanning tree is optimal for, each node transmits once a
/// round to its parent, so its uplink ETX is its energy per round, and the tree's
/// total cost is the sum of every node's uplink. Whole-path costs would also swing
/// with every rewiring further up the tree, hiding what each detour costs.
/// </remarks>
public sealed record Reroute(
    MacKey Node,
    MacKey BaselineNextHop,
    MacKey CurrentNextHop,
    double BaselineLinkCost,
    double CurrentLinkCost,
    bool NextHopFailed)
{
    /// <summary>Extra ETX per round the node now spends; negative if its new uplink happens to be better.</summary>
    public double CostDelta => Math.Round(CurrentLinkCost - BaselineLinkCost, 3);
}

/// <summary>A node that is up but has no live route to the gateway.</summary>
/// <param name="Node">The node.</param>
/// <param name="BlockedBy">The down nodes on its normal route.</param>
public sealed record StrandedNode(MacKey Node, IReadOnlyList<MacKey> BlockedBy);

/// <summary>A down node and what its failure did to the nodes that routed through it.</summary>
/// <param name="Node">The down node.</param>
/// <param name="Dependents">Nodes whose normal route passed through it.</param>
/// <param name="Rerouted">Of those, the ones that found another way.</param>
/// <param name="Stranded">Of those, the ones that did not.</param>
public sealed record FailedNode(
    MacKey Node,
    IReadOnlyList<MacKey> Dependents,
    IReadOnlyList<MacKey> Rerouted,
    IReadOnlyList<MacKey> Stranded);

/// <summary>How the mesh has healed: the live routing tree compared with the one it has with every node up.</summary>
public sealed class HealingReport
{
    /// <summary>Nodes routing differently from normal.</summary>
    public required IReadOnlyList<Reroute> Reroutes { get; init; }

    /// <summary>Nodes up but cut off.</summary>
    public required IReadOnlyList<StrandedNode> Stranded { get; init; }

    /// <summary>Down nodes, with the effect of each.</summary>
    public required IReadOnlyList<FailedNode> Failed { get; init; }

    /// <summary>Total spanning-tree cost with every node up.</summary>
    public required double BaselineTreeCost { get; init; }

    /// <summary>Total spanning-tree cost now.</summary>
    public required double CurrentTreeCost { get; init; }

    /// <summary>
    /// Extra ETX per round summed over every rerouted node's uplink: the energy
    /// price of the detours. Unlike the tree totals, this is not flattered by down nodes simply
    /// dropping out of the tree.
    /// </summary>
    public double DetourCost => Math.Round(Reroutes.Sum(reroute => reroute.CostDelta), 3);

    /// <summary>True when nothing is down, nothing is stranded and nothing has moved.</summary>
    public bool IsHealthy => Reroutes.Count == 0 && Stranded.Count == 0 && Failed.Count == 0;
}

/// <summary>
/// Reads the traversal and spanning-tree results as routing answers: the route a
/// given node takes, and how the mesh has healed around whatever is down.
/// <para>
/// Healing is measured against a <i>baseline</i> tree - the same spanning tree
/// computed as if every node that has joined the mesh were up. Comparing the two tells the operator, for
/// each failure, which nodes found another next hop (and what the detour costs in
/// extra transmissions) and which were left with no way home at all.
/// </para>
/// </summary>
public static class MeshRoutePlanner
{
    /// <summary>Traces one node's routes to the gateway.</summary>
    public static RouteTrace Trace(
        MeshGraph graph,
        SpanningTreeResult baseline,
        SpanningTreeResult live,
        ReachabilityResult reach,
        MacKey node)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(reach);

        return new RouteTrace
        {
            Node = node,
            IsDown = !graph.IsUp(node),
            IsReachable = reach.Reached(node),
            TreeRoute = Hops(graph, live.PathFrom(node)),
            FewestHopsRoute = Hops(graph, reach.PathFrom(node)),
            BaselineNextHop = baseline.Parents.TryGetValue(node, out var before) ? before : null,
            CurrentNextHop = live.Parents.TryGetValue(node, out var now) ? now : null,
            BlockedBy = reach.Reached(node) ? [] : DownOnRoute(graph, baseline, node)
        };
    }

    /// <summary>Compares the live tree with the baseline tree.</summary>
    public static HealingReport Heal(
        MeshGraph graph,
        SpanningTreeResult baseline,
        SpanningTreeResult live,
        ReachabilityResult reach)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(reach);

        var reroutes = new List<Reroute>();
        foreach (var (node, current) in live.Parents.OrderBy(pair => pair.Key))
        {
            if (baseline.Parents.TryGetValue(node, out var normal) && normal != current)
            {
                reroutes.Add(new Reroute(
                    node,
                    normal,
                    current,
                    graph.LinkBetween(node, normal)!.Cost,
                    graph.LinkBetween(node, current)!.Cost,
                    NextHopFailed: !graph.IsUp(normal)));
            }
        }

        var stranded = reach.Stranded
            .Order()
            .Select(node => new StrandedNode(node, DownOnRoute(graph, baseline, node)))
            .ToList();

        var failed = new List<FailedNode>();
        foreach (var node in reach.Down.Order())
        {
            var dependents = MeshTraversal.Downstream(baseline.Parents, node)
                .Select(entry => entry.Mac)
                .Where(graph.IsUp)
                .ToList();

            failed.Add(new FailedNode(
                node,
                dependents,
                dependents.Where(reach.Reached).ToList(),
                dependents.Where(mac => !reach.Reached(mac)).ToList()));
        }

        return new HealingReport
        {
            Reroutes = reroutes,
            Stranded = stranded,
            Failed = failed,
            BaselineTreeCost = baseline.TotalCost,
            CurrentTreeCost = live.TotalCost
        };
    }

    /// <summary>Turns a node-to-gateway path into hops carrying each link's signal and cost.</summary>
    private static List<RouteHop> Hops(MeshGraph graph, List<MacKey> path)
    {
        var hops = new List<RouteHop>(Math.Max(0, path.Count - 1));
        var cumulative = 0d;

        for (var i = 0; i + 1 < path.Count; i++)
        {
            var link = graph.LinkBetween(path[i], path[i + 1])
                ?? throw new InvalidOperationException($"No link between {path[i]} and {path[i + 1]}.");

            cumulative = Math.Round(cumulative + link.Cost, 3);
            hops.Add(new RouteHop(path[i], path[i + 1], link.RssiDbm, link.Cost, cumulative));
        }

        return hops;
    }

    /// <summary>The down nodes on a node's baseline route, nearest first.</summary>
    private static List<MacKey> DownOnRoute(MeshGraph graph, SpanningTreeResult baseline, MacKey node)
        => baseline.PathFrom(node).Skip(1).Where(mac => !graph.IsUp(mac)).ToList();
}
