using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Contracts.Sensors;
using SmartX.Core.Models;

namespace SmartX.Api.Controllers;

/// <summary>
/// Sensor registration and lookup.
/// <para>
/// The two lookup endpoints below deliberately use different strategies, because
/// the registry is ordered on exactly one dimension. Looking up by MAC bisects
/// that ordering in O(log n); searching free text has to scan, because no single
/// ordering can serve queries against names, zones and categories at once. Both
/// report the comparisons they used, so the difference is visible in the client
/// rather than merely asserted in a comment.
/// </para>
/// </summary>
[ApiController]
[Route("api/sensors")]
[Produces("application/json")]
[Tags("Sensors")]
public sealed class SensorsController(GatewayStore store, ILogger<SensorsController> logger)
    : ControllerBase
{
    /// <summary>Registers a node with the gateway.</summary>
    /// <param name="request">Registration details.</param>
    /// <response code="201">The node was registered.</response>
    /// <response code="400">The request failed validation.</response>
    /// <response code="409">A node with that MAC address is already registered.</response>
    [HttpPost]
    [ProducesResponseType(typeof(SensorResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public ActionResult<SensorResponse> Register([FromBody] RegisterSensorRequest request)
    {
        // The MAC has already passed the format attribute during model binding, so
        // normalisation here cannot fail - but it is what makes a re-registration
        // in a different separator style correctly read as a duplicate.
        var sensor = ContractMapper.ToRecord(request);

        if (!store.TryRegister(sensor))
        {
            logger.LogInformation("Rejected duplicate registration for {Mac}.", sensor.MacAddress);

            return Conflict(new ProblemDetails
            {
                Title = "That device is already registered.",
                Detail = $"A sensor with MAC address {sensor.MacAddress} is already known to the gateway.",
                Status = StatusCodes.Status409Conflict,
                Instance = Request.Path
            });
        }

        logger.LogInformation(
            "Registered {Mac} as {Name} at {Path}.",
            sensor.MacAddress, sensor.DisplayName, sensor.Location.QualifiedPath);

        var response = ContractMapper.ToResponse(sensor, DateTimeOffset.UtcNow);
        return CreatedAtAction(nameof(GetById), new { id = sensor.Id }, response);
    }

    /// <summary>Lists registered nodes, optionally filtered by category.</summary>
    /// <param name="category">Optional category filter, e.g. "PowerConsumption".</param>
    /// <response code="200">The registry, in MAC address order.</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<SensorResponse>), StatusCodes.Status200OK)]
    public ActionResult<List<SensorResponse>> List([FromQuery] string? category)
    {
        var now = DateTimeOffset.UtcNow;
        var sensors = store.AllSensors();

        if (!string.IsNullOrWhiteSpace(category)
            && Enum.TryParse<SensorCategory>(category, ignoreCase: true, out var parsed))
        {
            sensors = sensors.Where(sensor => sensor.Category == parsed).ToList();
        }

        return Ok(sensors.Select(sensor => Project(sensor, now)).ToList());
    }

    /// <summary>Fetches one node by its gateway-assigned key.</summary>
    /// <param name="id">Sensor key.</param>
    /// <response code="200">The node.</response>
    /// <response code="404">No node holds that key.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SensorResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<SensorResponse> GetById(Guid id)
    {
        var sensor = store.FindById(id);

        return sensor is null
            ? NotFound(NoSuchSensor(id.ToString()))
            : Ok(Project(sensor, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Fetches one node by MAC address using a binary search over the sorted
    /// registry - the O(log n) path every arriving telemetry packet takes.
    /// </summary>
    /// <param name="mac">Address in any form the firmware reports.</param>
    /// <response code="200">The node, with the comparison count in the X-Search-Comparisons header.</response>
    /// <response code="404">No node holds that address.</response>
    [HttpGet("by-mac/{mac}")]
    [ProducesResponseType(typeof(SensorResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<SensorResponse> GetByMac(string mac)
    {
        var (sensor, comparisons) = store.FindByMac(mac);

        // Exposed as a header so the cost is observable from Swagger and from the
        // client without changing the response body shape.
        Response.Headers["X-Search-Strategy"] = "binary";
        Response.Headers["X-Search-Comparisons"] = comparisons.ToString();

        return sensor is null
            ? NotFound(NoSuchSensor(mac))
            : Ok(Project(sensor, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Free-text search across display name, MAC address, category and every
    /// segment of the deployment path. Linear by necessity - see the class remarks.
    /// </summary>
    /// <param name="q">Case-insensitive substring. Blank returns the whole registry.</param>
    /// <response code="200">Matching nodes, with the scan cost alongside.</response>
    [HttpGet("search")]
    [ProducesResponseType(typeof(SensorSearchResponse), StatusCodes.Status200OK)]
    public ActionResult<SensorSearchResponse> Search([FromQuery] string? q)
    {
        var now = DateTimeOffset.UtcNow;
        var (matches, comparisons) = store.Search(q);

        return Ok(new SensorSearchResponse
        {
            Sensors = matches.Select(sensor => Project(sensor, now)).ToList(),
            TotalRegistered = store.SensorCount,
            ComparisonsUsed = comparisons,
            Strategy = "linear"
        });
    }

    /// <summary>The categories, units and statuses the API accepts, for populating client dropdowns.</summary>
    /// <response code="200">The accepted vocabulary.</response>
    [HttpGet("vocabulary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Vocabulary() => Ok(new
    {
        categories = SmartX.Contracts.SensorVocabulary.Categories,
        units = SmartX.Contracts.SensorVocabulary.Units,
        statuses = SmartX.Contracts.SensorVocabulary.Statuses
    });

    /// <summary>Fills in the parts of the response that need a second lookup.</summary>
    private SensorResponse Project(SensorRecord sensor, DateTimeOffset now)
    {
        var latest = store.RecentReadings(sensor.Id, 1);

        return ContractMapper.ToResponse(
            sensor,
            now,
            store.AttachmentCountFor(sensor.Id),
            latest.Count > 0 ? ContractMapper.Format(latest[0]) : null);
    }

    private ProblemDetails NoSuchSensor(string identifier) => new()
    {
        Title = "No such sensor.",
        Detail = $"No sensor matching '{identifier}' is registered with this gateway.",
        Status = StatusCodes.Status404NotFound,
        Instance = Request.Path
    };
}
