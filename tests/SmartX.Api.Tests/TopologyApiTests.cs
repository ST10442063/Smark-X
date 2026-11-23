using System.Net;
using System.Net.Http.Json;
using SmartX.Contracts.Topology;

namespace SmartX.Api.Tests;

/// <summary>
/// Drives the topology endpoints against the seeded site, whose layout is built to
/// show two things: Mesh Repeater 08 failing and the mesh healing around it at a
/// cost, and Pump Feed Meter 12 failing and stranding the valve behind it. These
/// tests pin both down, so the demonstration cannot quietly stop working.
/// </summary>
public class TopologyApiTests
{
    private const string Repeater08 = "A4:CF:12:00:07:03";
    private const string Meter04 = "A4:CF:12:00:03:6F";
    private const string Valve06 = "A4:CF:12:00:05:B9";
    private const string Meter12 = "A4:CF:12:00:0B:97";
    private const string Valve14 = "A4:CF:12:00:0D:E1";

    [Fact]
    public async Task SeededSite_IsOneMeshWithOnlyValve14Down()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        var mesh = await SnapshotAsync(http);

        // 14 seeded nodes, 3 actuators and the gateway.
        Assert.Equal(18, mesh.Stats.Nodes);
        Assert.Equal(1, mesh.Stats.DownNodes);
        Assert.Equal(0, mesh.Stats.StrandedNodes);
        Assert.Equal(17, mesh.Stats.Reachable);

        var valve14 = Node(mesh, Valve14);
        Assert.False(valve14.IsUp);
        Assert.Equal("Silent", valve14.Status);

        // A spanning tree over 17 live vertices has 16 links.
        Assert.Equal(16, mesh.Links.Count(link => link.InTree));
        Assert.All(mesh.Nodes.Where(node => node.IsUp && !node.IsGateway), node => Assert.NotNull(node.NextHop));
    }

    [Fact]
    public async Task SeededSite_Meter12IsTheOnlyCriticalRelay()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        var mesh = await SnapshotAsync(http);

        Assert.True(Node(mesh, Meter12).IsCriticalRelay);
        Assert.False(Node(mesh, Repeater08).IsCriticalRelay);
        Assert.Equal(1, mesh.Stats.CriticalRelays);
    }

    [Fact]
    public async Task FailingTheRepeater_ReroutesFacilityBAtACost()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();
        var before = await SnapshotAsync(http);
        Assert.Equal(Repeater08, Node(before, Meter04).NextHop);

        var failed = await http.PostAsync($"api/topology/nodes/{Repeater08}/failure", null);
        failed.EnsureSuccessStatusCode();
        var after = await SnapshotAsync(http);

        // Nobody is cut off: the mesh found the fallback across the gap.
        Assert.Equal(0, after.Stats.StrandedNodes);
        Assert.Equal("SimulatedFailure", Node(after, Repeater08).Status);
        Assert.NotEqual(Repeater08, Node(after, Meter04).NextHop);

        var reroute = Assert.Single(after.Healing.Reroutes, reroute => reroute.Node.Mac == Meter04);
        Assert.True(reroute.NextHopFailed);
        Assert.True(reroute.CostDelta > 0.5, $"Detour cost only {reroute.CostDelta}.");
        Assert.True(after.Stats.DetourCost > before.Stats.DetourCost);
        Assert.True(Node(after, Valve06).PathCost > Node(before, Valve06).PathCost);

        var repeater = Assert.Single(after.Healing.Failed, entry => entry.Node.Mac == Repeater08);
        Assert.Empty(repeater.Stranded);
        Assert.Contains(repeater.Rerouted, node => node.Mac == Meter04);
    }

    [Fact]
    public async Task FailingMeter12_StrandsValve06()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        (await http.PostAsync($"api/topology/nodes/{Meter12}/failure", null)).EnsureSuccessStatusCode();
        var mesh = await SnapshotAsync(http);

        Assert.True(Node(mesh, Valve06).IsStranded);
        var stranded = Assert.Single(mesh.Healing.Stranded);
        Assert.Equal(Valve06, stranded.Node.Mac);
        Assert.Equal(Meter12, Assert.Single(stranded.BlockedBy).Mac);

        var trace = await Gateway.GetAsync<RouteTraceResponse>(http, $"api/topology/route/{Valve06}");
        Assert.False(trace.IsReachable);
        Assert.Empty(trace.TreeRoute);
        Assert.Equal(Meter12, Assert.Single(trace.BlockedBy).Mac);
    }

    [Fact]
    public async Task Restore_ReturnsTheMeshToItsBaseline()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();
        var baseline = await SnapshotAsync(http);

        await http.PostAsync($"api/topology/nodes/{Repeater08}/failure", null);
        var restored = await http.DeleteFromJsonAsync<SimulatedFailureResponse>($"api/topology/nodes/{Repeater08}/failure", Gateway.Json);
        var mesh = await SnapshotAsync(http);

        Assert.False(restored!.SimulatedFailure);
        Assert.True(Node(mesh, Repeater08).IsUp);
        Assert.Equal(baseline.Stats.TreeCost, mesh.Stats.TreeCost);
    }

    [Fact]
    public async Task Trace_FollowsTheTreeAllTheWayToTheGateway()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        var trace = await Gateway.GetAsync<RouteTraceResponse>(http, $"api/topology/route/{Valve06}");

        Assert.True(trace.IsReachable);
        Assert.Equal(Valve06, trace.TreeRoute[0].From.Mac);
        Assert.Equal(Meter12, trace.TreeRoute[0].To.Mac);
        Assert.Contains(trace.TreeRoute, hop => hop.From.Mac == Repeater08);
        Assert.Equal("A4:CF:12:FE:00:01", trace.TreeRoute[^1].To.Mac);
        Assert.Equal(trace.TreeRoute[^1].CumulativeCost, trace.TreeCost);
        Assert.NotEmpty(trace.FewestHopsRoute);
        Assert.True(trace.FewestHopsRoute.Count <= trace.TreeRoute.Count);
    }

    [Fact]
    public async Task Trace_ListsTheSubnetBehindARelay()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        var trace = await Gateway.GetAsync<RouteTraceResponse>(http, $"api/topology/route/{Meter12}");

        Assert.True(trace.IsCriticalRelay);
        Assert.Contains(trace.Downstream, entry => entry.Node.Mac == Valve06 && entry.Depth == 1);
    }

    [Fact]
    public async Task PrimAndKruskal_AgreeOnTheTreeCost()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        var prim = await SnapshotAsync(http, "Prim");
        var kruskal = await SnapshotAsync(http, "Kruskal");

        Assert.Equal("Prim", prim.Stats.Algorithm);
        Assert.Equal("Kruskal", kruskal.Stats.Algorithm);
        Assert.Equal(prim.Stats.TreeCost, kruskal.Stats.TreeCost, precision: 6);
    }

    [Fact]
    public async Task Reachability_FromTheGateway_ReachesEveryLiveNode()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        var reach = await Gateway.GetAsync<ReachabilityResponse>(http, "api/topology/nodes/A4:CF:12:FE:00:01/reachability");

        Assert.Equal(17, reach.Reached);
        Assert.Equal(0, reach.Layers[0].Hops);
        Assert.Equal(Valve14, Assert.Single(reach.Down).Mac);
        Assert.Empty(reach.Unreachable);
    }

    [Fact]
    public async Task RegisteredNode_JoinsTheMeshNearItsFacility()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, "A4:CF:12:AB:00:01", "Bench Probe", "Environmental", "Percent");
        await Gateway.PublishAsync(http, Gateway.Packet("A4:CF:12:AB:00:01", "float", "41.5"));

        var mesh = await SnapshotAsync(http);
        var probe = Node(mesh, "A4:CF:12:AB:00:01");

        Assert.Equal(2, mesh.Stats.Nodes);
        Assert.InRange(probe.X, 0, 95);
        Assert.True(probe.Degree >= 1);
        Assert.Equal("A4:CF:12:FE:00:01", probe.NextHop);
    }

    [Fact]
    public async Task NeighbourReport_RefinesTheLinkSignal()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();
        var before = Link(await SnapshotAsync(http), Repeater08, Meter04);

        var response = await http.PostAsJsonAsync("api/devices/neighbours", new NeighbourEnvelopeRequest
        {
            GatewayId = "tests",
            Reports =
            [
                new NeighbourReportRequest
                {
                    MacAddress = Repeater08,
                    Neighbours = [new NeighbourEntryRequest { MacAddress = Meter04, RssiDbm = -86 }]
                },
                new NeighbourReportRequest
                {
                    MacAddress = "A4:CF:12:EE:EE:EE",
                    Neighbours = [new NeighbourEntryRequest { MacAddress = Meter04, RssiDbm = -60 }]
                }
            ]
        }, Gateway.Json);

        var result = await response.Content.ReadFromJsonAsync<NeighbourEnvelopeResponse>(Gateway.Json);
        var after = Link(await SnapshotAsync(http), Repeater08, Meter04);

        Assert.Equal(1, result!.ReportsAccepted);
        Assert.Equal(1, result.LinksUpdated);
        Assert.Equal(1, result.EntriesIgnored);
        Assert.Equal(-86, after.RssiDbm);
        Assert.True(after.Cost > before.Cost);
        Assert.Equal(1, after.Reports);
        Assert.NotNull(after.LastHeardUtc);
    }

    [Fact]
    public async Task NeighbourReport_NewLinkNeedsMarginAboveTheFloor()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        // Valve 06 and Meter 04 are 120 m apart: no surveyed link between them.
        Assert.DoesNotContain((await SnapshotAsync(http)).Links, link => Joins(link, Valve06, Meter04));

        var weak = await ReportAsync(http, Valve06, Meter04, -89);
        Assert.Equal(1, weak.EntriesIgnored);
        Assert.DoesNotContain((await SnapshotAsync(http)).Links, link => Joins(link, Valve06, Meter04));

        var clear = await ReportAsync(http, Valve06, Meter04, -84);
        Assert.Equal(1, clear.LinksDiscovered);
        Assert.Contains((await SnapshotAsync(http)).Links, link => Joins(link, Valve06, Meter04));
    }

    [Fact]
    public async Task BadRequests_AreRefused()
    {
        await using var factory = new GatewayFactory(seed: true);
        var http = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/topology/route/A4:CF:12:EE:EE:EE")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("api/topology?algorithm=Dijkstra")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("api/topology/nodes/A4:CF:12:FE:00:01/failure", null)).StatusCode);
    }

    private static Task<TopologySnapshotResponse> SnapshotAsync(HttpClient http, string algorithm = "Prim")
        => Gateway.GetAsync<TopologySnapshotResponse>(http, $"api/topology?algorithm={algorithm}");

    private static TopologyNodeResponse Node(TopologySnapshotResponse mesh, string mac)
        => mesh.Nodes.Single(node => node.Mac == mac);

    private static bool Joins(TopologyLinkResponse link, string a, string b)
        => (link.A == a && link.B == b) || (link.A == b && link.B == a);

    private static async Task<NeighbourEnvelopeResponse> ReportAsync(HttpClient http, string reporter, string heard, double rssi)
    {
        var response = await http.PostAsJsonAsync("api/devices/neighbours", new NeighbourEnvelopeRequest
        {
            Reports = [new NeighbourReportRequest { MacAddress = reporter, Neighbours = [new NeighbourEntryRequest { MacAddress = heard, RssiDbm = rssi }] }]
        }, Gateway.Json);

        return (await response.Content.ReadFromJsonAsync<NeighbourEnvelopeResponse>(Gateway.Json))!;
    }

    private static TopologyLinkResponse Link(TopologySnapshotResponse mesh, string a, string b)
        => mesh.Links.Single(link => (link.A == a && link.B == b) || (link.A == b && link.B == a));
}
