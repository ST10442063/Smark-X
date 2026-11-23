using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Models;
using SmartX.Api.Services;
using SmartX.Contracts.Dashboard;

namespace SmartX.Api.Controllers;

/// <summary>
/// The Anomaly Radar feed - heartbeat strips, the composite gateway health score,
/// and the anomaly triage queue.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Produces("application/json")]
[Tags("Dashboard")]
public sealed class DashboardController(
    GatewayStore store,
    DashboardService dashboard,
    AnomalyDetectionService detector) : ControllerBase
{
    /// <summary>Fleet-wide counters for the statistics panel.</summary>
    /// <response code="200">The statistics.</response>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(DashboardStatsResponse), StatusCodes.Status200OK)]
    public ActionResult<DashboardStatsResponse> Stats()
    {
        // Sweep for disconnects on the way past. A node that stops publishing
        // generates no request of its own, so nothing else would ever notice it -
        // the absence of data is the signal, and something has to go looking.
        detector.SweepDisconnects();

        return Ok(dashboard.Stats());
    }

    /// <summary>
    /// One heartbeat strip per sensor, ordered with the nodes needing attention
    /// first. Each strip carries the recent readings normalised for drawing, plus
    /// the z-score of each point so the client can colour individual bars.
    /// </summary>
    /// <response code="200">The strips.</response>
    [HttpGet("heartbeats")]
    [ProducesResponseType(typeof(List<HeartbeatStripResponse>), StatusCodes.Status200OK)]
    public ActionResult<List<HeartbeatStripResponse>> Heartbeats()
    {
        detector.SweepDisconnects();
        return Ok(dashboard.HeartbeatStrips());
    }

    /// <summary>The composite health score, its tier, and the badges earned against it.</summary>
    /// <response code="200">The health summary.</response>
    [HttpGet("health")]
    [ProducesResponseType(typeof(GatewayHealthResponse), StatusCodes.Status200OK)]
    public ActionResult<GatewayHealthResponse> Health()
    {
        detector.SweepDisconnects();
        return Ok(dashboard.Health());
    }

    /// <summary>The anomaly triage queue, worst first.</summary>
    /// <param name="includeAcknowledged">When true, cleared anomalies are included.</param>
    /// <param name="limit">Maximum to return.</param>
    /// <response code="200">The queue.</response>
    [HttpGet("anomalies")]
    [ProducesResponseType(typeof(List<AnomalyResponse>), StatusCodes.Status200OK)]
    public ActionResult<List<AnomalyResponse>> Anomalies(
        [FromQuery] bool includeAcknowledged = false,
        [FromQuery] int limit = 50)
    {
        detector.SweepDisconnects();

        var anomalies = store.Anomalies(openOnly: !includeAcknowledged, limit: Math.Clamp(limit, 1, 200));

        return Ok(anomalies.Select(anomaly =>
        {
            var sensor = store.FindById(anomaly.SensorId);

            return new AnomalyResponse
            {
                Id = anomaly.Id,
                SensorId = anomaly.SensorId,
                MacAddress = anomaly.MacAddress,
                DisplayName = sensor?.DisplayName ?? anomaly.MacAddress,
                QualifiedPath = sensor?.Location.QualifiedPath ?? string.Empty,
                Kind = anomaly.Kind.ToString(),
                Severity = anomaly.Severity.ToString(),
                Reading = anomaly.Reading,
                ZScore = anomaly.ZScore,
                DetectedAtUtc = anomaly.DetectedAtUtc,
                Explanation = anomaly.Explanation,
                Acknowledged = anomaly.Acknowledged
            };
        }).ToList());
    }

    /// <summary>Clears one anomaly, which lifts the health score.</summary>
    /// <param name="anomalyId">Anomaly key.</param>
    /// <response code="200">It was cleared.</response>
    /// <response code="404">No open anomaly holds that key.</response>
    [HttpPost("anomalies/{anomalyId:guid}/acknowledge")]
    [ProducesResponseType(typeof(GatewayHealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<GatewayHealthResponse> Acknowledge(Guid anomalyId)
    {
        if (!store.Acknowledge(anomalyId))
        {
            return NotFound(new ProblemDetails
            {
                Title = "No such open anomaly.",
                Detail = $"Anomaly {anomalyId} is unknown or has already been acknowledged.",
                Status = StatusCodes.Status404NotFound,
                Instance = Request.Path
            });
        }

        // The recomputed score comes straight back, so the client can move the dial
        // without a second round trip.
        return Ok(dashboard.Health());
    }

    /// <summary>Clears every open anomaly at once.</summary>
    /// <response code="200">The recomputed health summary.</response>
    [HttpPost("anomalies/acknowledge-all")]
    [ProducesResponseType(typeof(GatewayHealthResponse), StatusCodes.Status200OK)]
    public ActionResult<GatewayHealthResponse> AcknowledgeAll()
    {
        store.AcknowledgeAll();
        return Ok(dashboard.Health());
    }

    /// <summary>
    /// Runs a disconnect sweep on demand, for demonstrating that a node going
    /// quiet is detected by absence rather than by anything it sends.
    /// </summary>
    /// <response code="200">How many disconnects the sweep raised.</response>
    [HttpPost("sweep")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Sweep()
    {
        var raised = detector.SweepDisconnects();

        return Ok(new
        {
            disconnectsRaised = raised.Count,
            nodes = raised.Select(anomaly => anomaly.MacAddress).ToList()
        });
    }
}
