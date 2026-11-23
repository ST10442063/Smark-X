namespace SmartX.Contracts.Topology;

/// <summary>A node named by address and label, for lists and route hops.</summary>
public sealed class NodeRefResponse
{
    public string Mac { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>One vertex of the mesh, as the topology map draws it.</summary>
public sealed class TopologyNodeResponse
{
    public string Mac { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>"Gateway", or the sensor category, e.g. "Environmental".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Sensor key, for linking to the sensor's own page. Null for the gateway.</summary>
    public Guid? SensorId { get; set; }

    public string Facility { get; set; } = string.Empty;

    /// <summary>Full deployment path, e.g. "Facility A / Zone 1 / Sub-Zone B / NODE-08".</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>East-west position on the site plan, in metres.</summary>
    public double X { get; set; }

    /// <summary>North-south position on the site plan, in metres.</summary>
    public double Y { get; set; }

    /// <summary>"Gateway", "Online", "Degraded", "Silent", "Decommissioned" or "SimulatedFailure".</summary>
    public string Status { get; set; } = string.Empty;

    public bool IsGateway { get; set; }

    /// <summary>False when the node is down and nothing may route through it.</summary>
    public bool IsUp { get; set; }

    /// <summary>True when an operator has failed this node from the console.</summary>
    public bool SimulatedFailure { get; set; }

    /// <summary>True when the node has a live path to the gateway.</summary>
    public bool IsReachable { get; set; }

    /// <summary>True when the node is up but has no path to the gateway.</summary>
    public bool IsStranded { get; set; }

    /// <summary>True when the node is an articulation point: losing it would split the mesh.</summary>
    public bool IsCriticalRelay { get; set; }

    /// <summary>Fewest hops to the gateway, from breadth-first search. Null when unreachable.</summary>
    public int? Hops { get; set; }

    /// <summary>Hops to the gateway along the spanning tree.</summary>
    public int? TreeDepth { get; set; }

    /// <summary>The node's next hop towards the gateway in the spanning tree.</summary>
    public string? NextHop { get; set; }

    /// <summary>ETX to the gateway along the spanning tree.</summary>
    public double? PathCost { get; set; }

    /// <summary>Links the node has.</summary>
    public int Degree { get; set; }

    /// <summary>Nodes whose tree route passes through this one.</summary>
    public int Downstream { get; set; }
}

/// <summary>One wireless link of the mesh.</summary>
public sealed class TopologyLinkResponse
{
    public string A { get; set; } = string.Empty;

    public string B { get; set; } = string.Empty;

    /// <summary>Received signal strength, in dBm.</summary>
    public double RssiDbm { get; set; }

    /// <summary>Expected transmissions per delivered packet: the link's routing cost.</summary>
    public double Cost { get; set; }

    public double DistanceMetres { get; set; }

    /// <summary>True when the link is part of the minimum spanning tree.</summary>
    public bool InTree { get; set; }

    /// <summary>True when both ends are up.</summary>
    public bool IsLive { get; set; }

    /// <summary>When a device last reported hearing the link. Null for a surveyed link.</summary>
    public DateTimeOffset? LastHeardUtc { get; set; }

    /// <summary>Neighbour reports that mentioned the link.</summary>
    public int Reports { get; set; }
}

/// <summary>A facility's outline on the site plan.</summary>
public sealed class TopologyAreaResponse
{
    public string Name { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

/// <summary>Headline figures for the mesh.</summary>
public sealed class TopologyStatsResponse
{
    /// <summary>"Prim" or "Kruskal".</summary>
    public string Algorithm { get; set; } = string.Empty;

    public int Nodes { get; set; }

    public int Links { get; set; }

    public int LiveNodes { get; set; }

    public int DownNodes { get; set; }

    public int StrandedNodes { get; set; }

    /// <summary>Nodes with a live path to the gateway, including the gateway.</summary>
    public int Reachable { get; set; }

    public int CriticalRelays { get; set; }

    /// <summary>Total ETX of the live spanning tree.</summary>
    public double TreeCost { get; set; }

    /// <summary>Total ETX of the spanning tree with every node that has joined the mesh up.</summary>
    public double BaselineTreeCost { get; set; }

    /// <summary>Extra ETX per round the rerouted nodes pay on their new uplinks.</summary>
    public double DetourCost { get; set; }

    /// <summary>Most hops any reachable node is from the gateway.</summary>
    public int MaxHops { get; set; }

    /// <summary>Main-loop iterations of the spanning-tree algorithm.</summary>
    public long AlgorithmSteps { get; set; }

    /// <summary>Links breadth-first search examined.</summary>
    public int LinksExamined { get; set; }

    /// <summary>Time to compute the whole snapshot, in microseconds.</summary>
    public double ComputeMicroseconds { get; set; }
}

/// <summary>A node whose route changed because the mesh healed.</summary>
public sealed class RerouteResponse
{
    public NodeRefResponse Node { get; set; } = new();

    /// <summary>Its next hop with every node up.</summary>
    public NodeRefResponse From { get; set; } = new();

    /// <summary>Where it hops now.</summary>
    public NodeRefResponse To { get; set; } = new();

    /// <summary>ETX of the node's uplink to its baseline next hop.</summary>
    public double BaselineLinkCost { get; set; }

    /// <summary>ETX of the node's uplink to its current next hop.</summary>
    public double CurrentLinkCost { get; set; }

    /// <summary>Extra ETX per round the node now spends on its uplink.</summary>
    public double CostDelta { get; set; }

    /// <summary>True when the old next hop is down; false when the tree re-optimised around a failure elsewhere.</summary>
    public bool NextHopFailed { get; set; }
}

/// <summary>A node with no path to the gateway.</summary>
public sealed class StrandedNodeResponse
{
    public NodeRefResponse Node { get; set; } = new();

    /// <summary>The down nodes on its normal route.</summary>
    public List<NodeRefResponse> BlockedBy { get; set; } = [];
}

/// <summary>A down node and what its failure did.</summary>
public sealed class FailedNodeResponse
{
    public NodeRefResponse Node { get; set; } = new();

    /// <summary>"Silent" when its heartbeat stopped, "SimulatedFailure" when an operator failed it.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Nodes that normally route through it.</summary>
    public int Dependents { get; set; }

    /// <summary>Of those, the ones that found another way.</summary>
    public List<NodeRefResponse> Rerouted { get; set; } = [];

    /// <summary>Of those, the ones left with no route.</summary>
    public List<NodeRefResponse> Stranded { get; set; } = [];
}

/// <summary>How the mesh has healed around what is down.</summary>
public sealed class HealingResponse
{
    public bool IsHealthy { get; set; }

    public List<RerouteResponse> Reroutes { get; set; } = [];

    public List<StrandedNodeResponse> Stranded { get; set; } = [];

    public List<FailedNodeResponse> Failed { get; set; } = [];
}

/// <summary>The whole mesh at one instant: what the topology map draws.</summary>
public sealed class TopologySnapshotResponse
{
    public DateTimeOffset GeneratedAtUtc { get; set; }

    public string GatewayMac { get; set; } = string.Empty;

    /// <summary>Site plan width, in metres.</summary>
    public double Width { get; set; }

    /// <summary>Site plan height, in metres.</summary>
    public double Height { get; set; }

    public List<TopologyAreaResponse> Areas { get; set; } = [];

    public List<TopologyNodeResponse> Nodes { get; set; } = [];

    public List<TopologyLinkResponse> Links { get; set; } = [];

    public TopologyStatsResponse Stats { get; set; } = new();

    public HealingResponse Healing { get; set; } = new();
}

/// <summary>One hop of a traced route.</summary>
public sealed class RouteHopResponse
{
    public NodeRefResponse From { get; set; } = new();

    public NodeRefResponse To { get; set; } = new();

    public double RssiDbm { get; set; }

    public double Cost { get; set; }

    /// <summary>ETX from the traced node to the end of this hop.</summary>
    public double CumulativeCost { get; set; }
}

/// <summary>A node downstream of another in the routing tree.</summary>
public sealed class DownstreamNodeResponse
{
    public NodeRefResponse Node { get; set; } = new();

    /// <summary>Hops below the traced node.</summary>
    public int Depth { get; set; }
}

/// <summary>One node's routes to the gateway, and what depends on it.</summary>
public sealed class RouteTraceResponse
{
    public NodeRefResponse Node { get; set; } = new();

    public string Status { get; set; } = string.Empty;

    public string Algorithm { get; set; } = string.Empty;

    public bool IsDown { get; set; }

    public bool IsReachable { get; set; }

    public bool IsCriticalRelay { get; set; }

    /// <summary>The spanning-tree route: the power-efficient path.</summary>
    public List<RouteHopResponse> TreeRoute { get; set; } = [];

    public double TreeCost { get; set; }

    /// <summary>The breadth-first fewest-hops route, for comparison.</summary>
    public List<RouteHopResponse> FewestHopsRoute { get; set; } = [];

    public double FewestHopsCost { get; set; }

    /// <summary>Next hop with every node up.</summary>
    public NodeRefResponse? BaselineNextHop { get; set; }

    /// <summary>Next hop now.</summary>
    public NodeRefResponse? CurrentNextHop { get; set; }

    /// <summary>Down nodes on the normal route, when the node is cut off.</summary>
    public List<NodeRefResponse> BlockedBy { get; set; } = [];

    /// <summary>The subnet that routes through this node, found by depth-first search of the tree.</summary>
    public List<DownstreamNodeResponse> Downstream { get; set; } = [];
}

/// <summary>One ring of a breadth-first search.</summary>
public sealed class ReachabilityLayerResponse
{
    /// <summary>Hops from the search's source.</summary>
    public int Hops { get; set; }

    public List<NodeRefResponse> Nodes { get; set; } = [];
}

/// <summary>Every node reachable from one source, ring by ring.</summary>
public sealed class ReachabilityResponse
{
    public NodeRefResponse Source { get; set; } = new();

    public List<ReachabilityLayerResponse> Layers { get; set; } = [];

    /// <summary>Nodes reached, including the source.</summary>
    public int Reached { get; set; }

    /// <summary>Nodes up but not reachable from the source.</summary>
    public List<NodeRefResponse> Unreachable { get; set; } = [];

    public List<NodeRefResponse> Down { get; set; } = [];

    public int LinksExamined { get; set; }
}

/// <summary>The result of failing or restoring a node from the console.</summary>
public sealed class SimulatedFailureResponse
{
    public NodeRefResponse Node { get; set; } = new();

    /// <summary>Whether the node is now failed by the operator.</summary>
    public bool SimulatedFailure { get; set; }

    public string Message { get; set; } = string.Empty;
}
