using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services.Topology;
using SmartX.Contracts.Topology;
using SmartX.Core.Models;

namespace SmartX.Api.Controllers;

/// <summary>
/// The Network Topology and Mesh Routing pillar: the mesh as a graph, its minimum
/// spanning tree, breadth-first reachability, route tracing by MAC, and an
/// operator what-if to fail a node and watch the mesh heal around it.
/// </summary>
[ApiController]
[Route("api/topology")]
[Produces("application/json")]
[Tags("Topology")]
public sealed class TopologyController(MeshTopologyService topology) : ControllerBase
{
    /// <summary>
    /// The whole mesh: every node with its position, health, hop count and next
    /// hop; every link with its signal strength, ETX cost and whether the spanning
    /// tree uses it; headline statistics; and how the mesh has healed around
    /// anything that is down.
    /// </summary>
    /// <param name="algorithm"><c>Prim</c> (default) or <c>Kruskal</c>. Both give a tree of the same total cost.</param>
    /// <response code="200">The snapshot.</response>
    /// <response code="400">The algorithm is not recognised.</response>
    [HttpGet]
    [ProducesResponseType(typeof(TopologySnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<TopologySnapshotResponse> Snapshot([FromQuery] string? algorithm = null)
        => MeshTopologyService.IsKnownAlgorithm(algorithm)
            ? Ok(topology.Snapshot(algorithm))
            : UnknownAlgorithm(algorithm);

    /// <summary>
    /// Traces one node's route to the gateway: the spanning-tree route with each
    /// hop's signal and cost, the fewest-hops route for comparison, why it is cut
    /// off if it is, and the subnet that routes through it.
    /// </summary>
    /// <param name="mac">Node MAC address.</param>
    /// <param name="algorithm"><c>Prim</c> (default) or <c>Kruskal</c>.</param>
    /// <response code="200">The trace.</response>
    /// <response code="400">The algorithm is not recognised.</response>
    /// <response code="404">No node in the mesh has that MAC address.</response>
    [HttpGet("route/{mac}")]
    [ProducesResponseType(typeof(RouteTraceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<RouteTraceResponse> Route(string mac, [FromQuery] string? algorithm = null)
    {
        if (!MeshTopologyService.IsKnownAlgorithm(algorithm))
        {
            return UnknownAlgorithm(algorithm);
        }

        return topology.Trace(mac, algorithm) is { } trace ? Ok(trace) : UnknownNode(mac);
    }

    /// <summary>
    /// Breadth-first search from any node: every node it can reach, ring by ring,
    /// and which are cut off from it - "what is connected to this subnet?".
    /// </summary>
    /// <param name="mac">The node to search from.</param>
    /// <response code="200">The search result.</response>
    /// <response code="404">No node in the mesh has that MAC address.</response>
    [HttpGet("nodes/{mac}/reachability")]
    [ProducesResponseType(typeof(ReachabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<ReachabilityResponse> Reachability(string mac)
        => topology.Reachability(mac) is { } reach ? Ok(reach) : UnknownNode(mac);

    /// <summary>
    /// Fails a node from the console. Routes through it are recalculated at once,
    /// without waiting for two missed heartbeats - a what-if for planning, and the
    /// quickest way to watch the mesh heal.
    /// </summary>
    /// <param name="mac">Node MAC address.</param>
    /// <response code="200">The node is failed.</response>
    /// <response code="400">The node is the gateway, which cannot be failed.</response>
    /// <response code="404">No node in the mesh has that MAC address.</response>
    [HttpPost("nodes/{mac}/failure")]
    [ProducesResponseType(typeof(SimulatedFailureResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<SimulatedFailureResponse> Fail(string mac) => SetFailure(mac, failed: true);

    /// <summary>Restores a node failed from the console.</summary>
    /// <param name="mac">Node MAC address.</param>
    /// <response code="200">The node is restored.</response>
    /// <response code="400">The node is the gateway.</response>
    /// <response code="404">No node in the mesh has that MAC address.</response>
    [HttpDelete("nodes/{mac}/failure")]
    [ProducesResponseType(typeof(SimulatedFailureResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<SimulatedFailureResponse> Restore(string mac) => SetFailure(mac, failed: false);

    private ActionResult<SimulatedFailureResponse> SetFailure(string mac, bool failed)
    {
        try
        {
            return topology.SetSimulatedFailure(mac, failed) is { } outcome ? Ok(outcome) : UnknownNode(mac);
        }
        catch (InvalidOperationException error)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "That node cannot be failed.",
                Detail = error.Message,
                Status = StatusCodes.Status400BadRequest,
                Instance = Request.Path
            });
        }
    }

    private NotFoundObjectResult UnknownNode(string mac) => NotFound(new ProblemDetails
    {
        Title = "No such node.",
        Detail = MacAddress.IsValid(mac)
            ? $"No node in the mesh has MAC address {mac}."
            : $"'{mac}' is not a valid MAC address.",
        Status = StatusCodes.Status404NotFound,
        Instance = Request.Path
    });

    private BadRequestObjectResult UnknownAlgorithm(string? algorithm) => BadRequest(new ProblemDetails
    {
        Title = "Unknown algorithm.",
        Detail = $"'{algorithm}' is not a spanning-tree algorithm this gateway runs. Use Prim or Kruskal.",
        Status = StatusCodes.Status400BadRequest,
        Instance = Request.Path
    });
}
