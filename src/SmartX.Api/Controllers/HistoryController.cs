using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services.History;
using SmartX.Contracts.History;
using SmartX.Core.Models;

namespace SmartX.Api.Controllers;

/// <summary>
/// Time-window queries over each sensor's AVL history tree - the "pull every
/// moisture reading between 14:00 and 16:00 for Node A" requirement of the final PoE.
/// <para>
/// Windows are half-open, <c>[fromUtc, toUtc)</c>, so a chart cut into adjacent
/// slices never counts a reading twice. Every response carries the number of tree
/// nodes the query visited next to the number of readings held, which is the
/// evidence that the window was found by descending the tree rather than scanning
/// the history.
/// </para>
/// </summary>
[ApiController]
[Route("api/history")]
[Produces("application/json")]
[Tags("History")]
public sealed class HistoryController(HistoryQueryService history) : ControllerBase
{
    /// <summary>Largest page the log will return.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Most slices a chart may ask for.</summary>
    public const int MaxBuckets = 500;

    /// <summary>
    /// One page of a sensor's readings in a time window, newest first by default,
    /// with the count, minimum, maximum and mean of the whole window.
    /// </summary>
    /// <param name="mac">Node MAC address.</param>
    /// <param name="fromUtc">Start of the window, inclusive. Defaults to two hours before <paramref name="toUtc"/>.</param>
    /// <param name="toUtc">End of the window, exclusive. Defaults to just after the newest reading.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Readings per page, 1 to 500.</param>
    /// <param name="order"><c>newest</c> (default) or <c>oldest</c> first.</param>
    /// <response code="200">The page.</response>
    /// <response code="400">The window or paging is invalid.</response>
    /// <response code="404">No node has that MAC address.</response>
    [HttpGet("{mac}")]
    [ProducesResponseType(typeof(HistoryPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<HistoryPageResponse> Page(
        string mac,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string order = "newest")
    {
        if (history.Find(mac) is not { } sensor)
        {
            return UnknownNode(mac);
        }

        if (page < 1 || pageSize is < 1 or > MaxPageSize)
        {
            return Invalid("Paging is out of range.", $"Page must be 1 or more, and page size between 1 and {MaxPageSize}.");
        }

        if (order is not ("newest" or "oldest"))
        {
            return Invalid("Unknown order.", "Order must be 'newest' or 'oldest'.");
        }

        var (from, to) = history.ResolveWindow(sensor, fromUtc, toUtc);
        if (from >= to)
        {
            return Invalid("The window is empty.", "fromUtc must be earlier than toUtc.");
        }

        return Ok(history.Page(sensor, from, to, page, pageSize, newestFirst: order == "newest"));
    }

    /// <summary>
    /// A time window cut into equal slices for charting, each summarised from the
    /// tree's subtree aggregates in O(log n).
    /// </summary>
    /// <param name="mac">Node MAC address.</param>
    /// <param name="fromUtc">Start of the window, inclusive.</param>
    /// <param name="toUtc">End of the window, exclusive.</param>
    /// <param name="buckets">Slices, 1 to 500.</param>
    /// <response code="200">The series.</response>
    /// <response code="400">The window or slice count is invalid.</response>
    /// <response code="404">No node has that MAC address.</response>
    [HttpGet("{mac}/series")]
    [ProducesResponseType(typeof(HistorySeriesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<HistorySeriesResponse> Series(
        string mac,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int buckets = 120)
    {
        if (history.Find(mac) is not { } sensor)
        {
            return UnknownNode(mac);
        }

        if (buckets is < 1 or > MaxBuckets)
        {
            return Invalid("Too many or too few slices.", $"Buckets must be between 1 and {MaxBuckets}.");
        }

        var (from, to) = history.ResolveWindow(sensor, fromUtc, toUtc);
        if (from >= to)
        {
            return Invalid("The window is empty.", "fromUtc must be earlier than toUtc.");
        }

        return Ok(history.Series(sensor, from, to, buckets));
    }

    /// <summary>The oldest and newest readings a node's tree holds, and the tree's shape.</summary>
    /// <param name="mac">Node MAC address.</param>
    /// <response code="200">The bounds.</response>
    /// <response code="404">No node has that MAC address.</response>
    [HttpGet("{mac}/bounds")]
    [ProducesResponseType(typeof(HistoryBoundsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<HistoryBoundsResponse> Bounds(string mac)
        => history.Find(mac) is { } sensor ? Ok(history.Bounds(sensor)) : UnknownNode(mac);

    /// <summary>A node's newest readings, newest first.</summary>
    /// <param name="mac">Node MAC address.</param>
    /// <param name="count">Readings to return, 1 to 200.</param>
    /// <response code="200">The readings.</response>
    /// <response code="404">No node has that MAC address.</response>
    [HttpGet("{mac}/recent")]
    [ProducesResponseType(typeof(List<HistoryReadingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<List<HistoryReadingResponse>> Recent(string mac, [FromQuery] int count = 12)
        => history.Find(mac) is { } sensor
            ? Ok(history.Recent(sensor, Math.Clamp(count, 1, 200)))
            : UnknownNode(mac);

    private NotFoundObjectResult UnknownNode(string mac) => NotFound(new ProblemDetails
    {
        Title = "No such node.",
        Detail = MacAddress.IsValid(mac)
            ? $"No node with MAC address {mac} is registered with this gateway."
            : $"'{mac}' is not a valid MAC address.",
        Status = StatusCodes.Status404NotFound,
        Instance = Request.Path
    });

    private BadRequestObjectResult Invalid(string title, string detail) => BadRequest(new ProblemDetails
    {
        Title = title,
        Detail = detail,
        Status = StatusCodes.Status400BadRequest,
        Instance = Request.Path
    });
}
