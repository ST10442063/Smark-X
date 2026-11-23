using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartX.Api.Models;
using SmartX.Contracts.Topology;
using SmartX.Core.Devices;
using SmartX.Core.Mesh;
using SmartX.Core.Models;

namespace SmartX.Api.Services.Topology;

/// <summary>
/// The gateway's model of the physical mesh, and the routing answers drawn from it.
/// <para>
/// <b>Building the graph.</b> Every registered sensor becomes a vertex at its
/// surveyed position (<see cref="MeshSitePlan"/>), linked to every other node the
/// path-loss model says it can hear. Devices then refine those links with their
/// own neighbour reports, so the weights converge on what the radios actually see.
/// </para>
/// <para>
/// <b>Health.</b> A vertex is up while its sensor's heartbeat is fresh (Online or
/// Degraded) and an operator has not failed it from the console. Silence for two
/// heartbeat windows - the Part 1 rule - takes it down, and every route through it
/// is recalculated on the next request. That is the mesh "healing itself".
/// </para>
/// <para>
/// <b>Computation.</b> Each request recomputes from the current graph: the live
/// spanning tree (Prim or Kruskal), a baseline tree with every node that has
/// joined the mesh up, to measure healing against, a breadth-first search from the gateway, and the articulation points.
/// All are O(E log V) or better, so the whole snapshot takes microseconds at this
/// scale and stays well under a millisecond for thousands of nodes - recomputing
/// is simpler and safer than keeping a cached tree in step with every change.
/// </para>
/// <para>
/// <b>Locking.</b> The graph is guarded by this service's own lock. Sensor
/// snapshots are read from <see cref="GatewayStore"/> before that lock is taken,
/// and the registration event is raised outside the store's lock, so the two locks
/// are never held together and cannot deadlock.
/// </para>
/// </summary>
public sealed class MeshTopologyService : IDisposable
{
    private readonly object _gate = new();
    private readonly MeshGraph _graph = new();
    private readonly GatewayStore _store;
    private readonly ILogger<MeshTopologyService> _logger;
    private readonly double _floorDbm;
    private readonly MacKey _gatewayMac;

    private readonly Dictionary<MacKey, NodeInfo> _nodes = [];
    private readonly Dictionary<MacKey, string> _status = [];
    private readonly HashSet<MacKey> _simulatedDown = [];
    private readonly HashSet<MacKey> _joined = [];
    private readonly HashSet<string> _claimedSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _autoPlaced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How far above the link floor a link never seen before must be heard before it
    /// is added. Radio readings wobble by a few dB, so without this margin a pair of
    /// nodes at the very edge of range would gain a link on one lucky report and keep
    /// it - the same admission hysteresis real mesh stacks apply before accepting a
    /// new neighbour.
    /// </summary>
    public const double DiscoveryMarginDb = 3;

    /// <summary>Creates the service and places every sensor already registered.</summary>
    public MeshTopologyService(GatewayStore store, IOptions<StreamOptions> options, ILogger<MeshTopologyService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _logger = logger;
        _floorDbm = options.Value.Bands.LinkFloorDbm;
        _gatewayMac = MacKey.Parse(MeshSitePlan.GatewayMac);

        _graph.AddVertex(new MeshVertex(
            _gatewayMac, MeshSitePlan.GatewayName, MeshSitePlan.GatewayPosition.X, MeshSitePlan.GatewayPosition.Y, isGateway: true));

        // Subscribe before reading the existing sensors, so one registered in
        // between is caught by the event; adding is idempotent, so one seen twice
        // is harmless.
        _store.SensorRegistered += OnSensorRegistered;

        var existing = _store.AllSensors();
        lock (_gate)
        {
            foreach (var sensor in existing)
            {
                Place(sensor);
            }
        }
    }

    /// <summary>The gateway's MAC address.</summary>
    public string GatewayMac => MeshSitePlan.GatewayMac;

    /// <summary>Recognised algorithm names.</summary>
    public static bool IsKnownAlgorithm(string? algorithm)
        => string.IsNullOrWhiteSpace(algorithm)
           || algorithm.Equals(PrimMst.Name, StringComparison.OrdinalIgnoreCase)
           || algorithm.Equals(KruskalMst.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Unsubscribes from the store.</summary>
    public void Dispose() => _store.SensorRegistered -= OnSensorRegistered;

    // ---------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------

    /// <summary>The whole mesh: nodes, links, statistics and the healing report.</summary>
    public TopologySnapshotResponse Snapshot(string? algorithm)
    {
        var sensors = _store.AllSensors();

        lock (_gate)
        {
            var clock = Stopwatch.StartNew();
            var analysis = Analyse(sensors, algorithm);
            var elapsed = clock.Elapsed.TotalMicroseconds;

            var downstream = SubtreeSizes(analysis.Live);

            var nodes = _graph.Vertices.Select(vertex =>
            {
                var mac = vertex.Mac;
                var info = _nodes.GetValueOrDefault(mac);
                analysis.Reach.Hops.TryGetValue(mac, out var hops);

                return new TopologyNodeResponse
                {
                    Mac = mac.ToString(),
                    Name = vertex.Name,
                    Category = vertex.IsGateway ? "Gateway" : info?.Category ?? string.Empty,
                    SensorId = info?.SensorId,
                    Facility = vertex.IsGateway ? MeshSitePlan.Areas[0].Name : info?.Facility ?? string.Empty,
                    Location = vertex.IsGateway ? $"{MeshSitePlan.Areas[0].Name} / border-router-01" : info?.Location ?? string.Empty,
                    X = vertex.X,
                    Y = vertex.Y,
                    Status = StatusOf(mac),
                    IsGateway = vertex.IsGateway,
                    IsUp = vertex.IsUp,
                    SimulatedFailure = _simulatedDown.Contains(mac),
                    IsReachable = analysis.Reach.Reached(mac),
                    IsStranded = analysis.Reach.Stranded.Contains(mac),
                    IsCriticalRelay = analysis.CriticalRelays.Contains(mac),
                    Hops = analysis.Reach.Reached(mac) ? hops : null,
                    TreeDepth = analysis.Live.Depth.TryGetValue(mac, out var depth) ? depth : null,
                    NextHop = analysis.Live.Parents.TryGetValue(mac, out var next) ? next.ToString() : null,
                    PathCost = analysis.Live.PathCost.TryGetValue(mac, out var cost) ? cost : null,
                    Degree = _graph.DegreeOf(mac),
                    Downstream = downstream.GetValueOrDefault(mac)
                };
            }).ToList();

            var links = _graph.Links.Select(link =>
            {
                var a = _graph.Find(link.A)!;
                var b = _graph.Find(link.B)!;

                return new TopologyLinkResponse
                {
                    A = link.A.ToString(),
                    B = link.B.ToString(),
                    RssiDbm = link.RssiDbm,
                    Cost = link.Cost,
                    DistanceMetres = Math.Round(LinkCostModel.Distance(a.X, a.Y, b.X, b.Y), 1),
                    InTree = analysis.Live.Uses(link),
                    IsLive = a.IsUp && b.IsUp,
                    LastHeardUtc = link.LastHeardUtc,
                    Reports = link.Reports
                };
            }).ToList();

            var healing = analysis.Healing;

            return new TopologySnapshotResponse
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                GatewayMac = MeshSitePlan.GatewayMac,
                Width = MeshSitePlan.Width,
                Height = MeshSitePlan.Height,
                Areas = MeshSitePlan.Areas.Select(area => new TopologyAreaResponse
                {
                    Name = area.Name,
                    X = area.X,
                    Y = area.Y,
                    Width = area.Width,
                    Height = area.Height
                }).ToList(),
                Nodes = nodes,
                Links = links,
                Stats = new TopologyStatsResponse
                {
                    Algorithm = analysis.Live.Algorithm,
                    Nodes = _graph.VertexCount,
                    Links = _graph.LinkCount,
                    LiveNodes = _graph.VertexCount - analysis.Reach.Down.Count,
                    DownNodes = analysis.Reach.Down.Count,
                    StrandedNodes = analysis.Reach.Stranded.Count,
                    Reachable = analysis.Reach.Hops.Count,
                    CriticalRelays = analysis.CriticalRelays.Count,
                    TreeCost = analysis.Live.TotalCost,
                    BaselineTreeCost = analysis.Baseline.TotalCost,
                    DetourCost = healing.DetourCost,
                    MaxHops = Math.Max(0, analysis.Reach.Layers.Count - 1),
                    AlgorithmSteps = analysis.Live.Steps,
                    LinksExamined = analysis.Reach.LinksExamined,
                    ComputeMicroseconds = Math.Round(elapsed, 1)
                },
                Healing = new HealingResponse
                {
                    IsHealthy = healing.IsHealthy,
                    Reroutes = healing.Reroutes.Select(reroute => new RerouteResponse
                    {
                        Node = Ref(reroute.Node),
                        From = Ref(reroute.BaselineNextHop),
                        To = Ref(reroute.CurrentNextHop),
                        BaselineLinkCost = reroute.BaselineLinkCost,
                        CurrentLinkCost = reroute.CurrentLinkCost,
                        CostDelta = reroute.CostDelta,
                        NextHopFailed = reroute.NextHopFailed
                    }).ToList(),
                    Stranded = healing.Stranded.Select(stranded => new StrandedNodeResponse
                    {
                        Node = Ref(stranded.Node),
                        BlockedBy = stranded.BlockedBy.Select(Ref).ToList()
                    }).ToList(),
                    Failed = healing.Failed.Select(failed => new FailedNodeResponse
                    {
                        Node = Ref(failed.Node),
                        Reason = StatusOf(failed.Node),
                        Dependents = failed.Dependents.Count,
                        Rerouted = failed.Rerouted.Select(Ref).ToList(),
                        Stranded = failed.Stranded.Select(Ref).ToList()
                    }).ToList()
                }
            };
        }
    }

    /// <summary>One node's routes to the gateway and the subnet behind it, or null for an unknown node.</summary>
    public RouteTraceResponse? Trace(string mac, string? algorithm)
    {
        if (!MacKey.TryParse(mac, out var key))
        {
            return null;
        }

        var sensors = _store.AllSensors();

        lock (_gate)
        {
            if (!_graph.Contains(key))
            {
                return null;
            }

            var analysis = Analyse(sensors, algorithm);
            var trace = MeshRoutePlanner.Trace(_graph, analysis.Baseline, analysis.Live, analysis.Reach, key);

            // The subnet is taken from the live tree when the node is routing, and
            // from the baseline when it is down - "what depends on this node" is most
            // useful exactly when it has failed.
            var tree = analysis.Live.Contains(key) ? analysis.Live : analysis.Baseline;
            var downstream = MeshTraversal.Downstream(tree.Parents, key);

            return new RouteTraceResponse
            {
                Node = Ref(key),
                Status = StatusOf(key),
                Algorithm = analysis.Live.Algorithm,
                IsDown = trace.IsDown,
                IsReachable = trace.IsReachable,
                IsCriticalRelay = analysis.CriticalRelays.Contains(key),
                TreeRoute = trace.TreeRoute.Select(ToHop).ToList(),
                TreeCost = trace.TreeCost,
                FewestHopsRoute = trace.FewestHopsRoute.Select(ToHop).ToList(),
                FewestHopsCost = trace.FewestHopsCost,
                BaselineNextHop = trace.BaselineNextHop is { } before ? Ref(before) : null,
                CurrentNextHop = trace.CurrentNextHop is { } now ? Ref(now) : null,
                BlockedBy = trace.BlockedBy.Select(Ref).ToList(),
                Downstream = downstream.Select(entry => new DownstreamNodeResponse { Node = Ref(entry.Mac), Depth = entry.Depth }).ToList()
            };
        }
    }

    /// <summary>A breadth-first search from any node: what it can reach, ring by ring. Null for an unknown node.</summary>
    public ReachabilityResponse? Reachability(string mac)
    {
        if (!MacKey.TryParse(mac, out var key))
        {
            return null;
        }

        var sensors = _store.AllSensors();

        lock (_gate)
        {
            if (!_graph.Contains(key))
            {
                return null;
            }

            Refresh(sensors);
            var reach = MeshTraversal.BreadthFirst(_graph, key);

            return new ReachabilityResponse
            {
                Source = Ref(key),
                Layers = reach.Layers.Select((layer, hops) => new ReachabilityLayerResponse
                {
                    Hops = hops,
                    Nodes = layer.Select(Ref).ToList()
                }).ToList(),
                Reached = reach.Hops.Count,
                Unreachable = reach.Stranded.Order().Select(Ref).ToList(),
                Down = reach.Down.Order().Select(Ref).ToList(),
                LinksExamined = reach.LinksExamined
            };
        }
    }

    /// <summary>The surveyed position of a node, for the device emulator.</summary>
    public (double X, double Y)? PositionOf(string mac)
    {
        if (!MacKey.TryParse(mac, out var key))
        {
            return null;
        }

        lock (_gate)
        {
            return _graph.Find(key) is { } vertex ? (vertex.X, vertex.Y) : null;
        }
    }

    // ---------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fails or restores a node from the console - a what-if that takes effect at
    /// once, without waiting two heartbeat windows for a real outage to register.
    /// </summary>
    /// <returns>The outcome, or null for an unknown node.</returns>
    /// <exception cref="InvalidOperationException">The node is the gateway.</exception>
    public SimulatedFailureResponse? SetSimulatedFailure(string mac, bool failed)
    {
        if (!MacKey.TryParse(mac, out var key))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_graph.Contains(key))
            {
                return null;
            }

            if (key == _gatewayMac)
            {
                throw new InvalidOperationException("The gateway is the root every route leads to; it cannot be failed.");
            }

            var changed = failed ? _simulatedDown.Add(key) : _simulatedDown.Remove(key);
            var name = _graph.Find(key)!.Name;

            if (changed)
            {
                _logger.LogInformation("Operator {Action} {Node} ({Mac}) on the topology page.",
                    failed ? "failed" : "restored", name, key);
            }

            return new SimulatedFailureResponse
            {
                Node = Ref(key),
                SimulatedFailure = failed,
                Message = (failed, changed) switch
                {
                    (true, true) => $"{name} is now failed. Routes through it are recalculated.",
                    (true, false) => $"{name} was already failed.",
                    (false, true) => $"{name} is restored. The mesh will route through it again if it is reporting.",
                    _ => $"{name} was not failed."
                }
            };
        }
    }

    /// <summary>
    /// Folds devices' neighbour tables into the graph: known links have their
    /// signal strength smoothed towards the report, and a new link heard clearly
    /// above the floor (by <see cref="DiscoveryMarginDb"/>) is added.
    /// </summary>
    public NeighbourEnvelopeResponse ApplyNeighbourReports(NeighbourEnvelopeRequest envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var response = new NeighbourEnvelopeResponse();
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            foreach (var report in envelope.Reports)
            {
                if (!MacKey.TryParse(report.MacAddress, out var reporter) || !_graph.Contains(reporter))
                {
                    response.EntriesIgnored += report.Neighbours.Count;
                    continue;
                }

                response.ReportsAccepted++;

                foreach (var entry in report.Neighbours)
                {
                    if (!MacKey.TryParse(entry.MacAddress, out var neighbour) || neighbour == reporter || !_graph.Contains(neighbour))
                    {
                        response.EntriesIgnored++;
                        continue;
                    }

                    if (_graph.LinkBetween(reporter, neighbour) is { } link)
                    {
                        link.Report(entry.RssiDbm, now);
                        response.LinksUpdated++;
                    }
                    else if (LinkCostModel.IsUsable(entry.RssiDbm, _floorDbm + DiscoveryMarginDb))
                    {
                        _graph.Connect(reporter, neighbour, entry.RssiDbm).Report(entry.RssiDbm, now);
                        response.LinksDiscovered++;
                    }
                    else
                    {
                        response.EntriesIgnored++;
                    }
                }
            }
        }

        return response;
    }

    // ---------------------------------------------------------------------
    // Internals - callers hold the lock unless noted
    // ---------------------------------------------------------------------

    private void OnSensorRegistered(SensorRecord sensor)
    {
        lock (_gate)
        {
            Place(sensor);
        }
    }

    /// <summary>Adds a sensor as a vertex at its surveyed or automatic position, linked to everything in range.</summary>
    private void Place(SensorRecord sensor)
    {
        if (!MacKey.TryParse(sensor.MacAddress, out var mac) || _graph.Contains(mac))
        {
            return;
        }

        var facility = sensor.Location.Facility;
        (double X, double Y) position;

        if (MeshSitePlan.Surveyed.TryGetValue(sensor.Location.NodeId, out var surveyed) && _claimedSlots.Add(sensor.Location.NodeId))
        {
            position = surveyed;
        }
        else
        {
            var ordinal = _autoPlaced.GetValueOrDefault(facility);
            _autoPlaced[facility] = ordinal + 1;
            position = MeshSitePlan.AutoPlace(facility, ordinal);
        }

        var vertex = new MeshVertex(mac, sensor.DisplayName, position.X, position.Y);
        var others = _graph.Vertices.ToList();
        _graph.AddVertex(vertex);

        _nodes[mac] = new NodeInfo(sensor.Id, sensor.Category.ToString(), facility, sensor.Location.QualifiedPath);

        foreach (var other in others)
        {
            var rssi = LinkCostModel.RssiAt(LinkCostModel.Distance(vertex.X, vertex.Y, other.X, other.Y));

            if (LinkCostModel.IsUsable(rssi, _floorDbm))
            {
                _graph.Connect(mac, other.Mac, rssi);
            }
        }
    }

    /// <summary>Marks each vertex up or down from its sensor's heartbeat and any operator failure.</summary>
    private void Refresh(IReadOnlyList<SensorRecord> sensors)
    {
        var now = DateTimeOffset.UtcNow;
        _status[_gatewayMac] = "Gateway";

        foreach (var sensor in sensors)
        {
            if (!MacKey.TryParse(sensor.MacAddress, out var mac) || _graph.Find(mac) is not { } vertex)
            {
                continue;
            }

            var health = sensor.EvaluateStatus(now);
            var simulated = _simulatedDown.Contains(mac);

            // A node has joined the mesh once it has reported at all. One that never
            // has (a mis-flashed or never-powered device) is down, but was never part
            // of the healthy mesh the baseline describes.
            if (sensor.LastSeenUtc is not null)
            {
                _joined.Add(mac);
            }

            vertex.Name = sensor.DisplayName;
            vertex.IsUp = !simulated && health is SensorStatus.Online or SensorStatus.Degraded;
            _status[mac] = simulated ? "SimulatedFailure" : health.ToString();
        }
    }

    private Analysis Analyse(IReadOnlyList<SensorRecord> sensors, string? algorithm)
    {
        Refresh(sensors);

        var kruskal = string.Equals(algorithm, KruskalMst.Name, StringComparison.OrdinalIgnoreCase);
        Func<MeshGraph, MacKey, Func<MacKey, bool>?, SpanningTreeResult> build = kruskal ? KruskalMst.Build : PrimMst.Build;

        var baseline = build(_graph, _gatewayMac, mac => mac == _gatewayMac || _joined.Contains(mac));
        var live = build(_graph, _gatewayMac, null);
        var reach = MeshTraversal.BreadthFirst(_graph, _gatewayMac);
        var relays = MeshTraversal.ArticulationPoints(_graph);
        relays.Remove(_gatewayMac);

        return new Analysis(baseline, live, reach, relays, MeshRoutePlanner.Heal(_graph, baseline, live, reach));
    }

    /// <summary>How many nodes route through each node, from one pass deepest-first up the tree.</summary>
    private static Dictionary<MacKey, int> SubtreeSizes(SpanningTreeResult tree)
    {
        var sizes = new Dictionary<MacKey, int>();

        foreach (var (mac, _) in tree.Depth.OrderByDescending(pair => pair.Value))
        {
            if (tree.Parents.TryGetValue(mac, out var parent))
            {
                sizes[parent] = sizes.GetValueOrDefault(parent) + sizes.GetValueOrDefault(mac) + 1;
            }
        }

        return sizes;
    }

    private string StatusOf(MacKey mac) => _status.GetValueOrDefault(mac, "Unknown");

    private NodeRefResponse Ref(MacKey mac) => new()
    {
        Mac = mac.ToString(),
        Name = _graph.Find(mac)?.Name ?? mac.ToString()
    };

    private RouteHopResponse ToHop(RouteHop hop) => new()
    {
        From = Ref(hop.From),
        To = Ref(hop.To),
        RssiDbm = hop.RssiDbm,
        Cost = hop.Cost,
        CumulativeCost = hop.CumulativeCost
    };

    private sealed record NodeInfo(Guid SensorId, string Category, string Facility, string Location);

    private sealed record Analysis(
        SpanningTreeResult Baseline,
        SpanningTreeResult Live,
        ReachabilityResult Reach,
        HashSet<MacKey> CriticalRelays,
        HealingReport Healing);
}
