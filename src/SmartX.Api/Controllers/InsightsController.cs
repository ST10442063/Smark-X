using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services.Streaming;
using SmartX.Contracts.Insights;

namespace SmartX.Api.Controllers;

/// <summary>
/// The predictive action engine: Suggested Actions, Automated Insights, and the
/// operator activity they are learned from.
/// </summary>
[ApiController]
[Route("api/insights")]
[Produces("application/json")]
[Tags("Insights")]
public sealed class InsightsController(InsightService insights, CommandStreamStore store) : ControllerBase
{
    /// <summary>The current suggestions, ranked.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(InsightsResponse), StatusCodes.Status200OK)]
    public ActionResult<InsightsResponse> Current() => Ok(insights.Current());

    /// <summary>
    /// Records a search or a device being opened. The engine learns from it, and a
    /// search can itself trigger advice, so the fresh suggestions come back.
    /// </summary>
    /// <response code="400">The search was blank, or the device is unknown.</response>
    [HttpPost("activity")]
    [ProducesResponseType(typeof(InsightsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<InsightsResponse> Record([FromBody] RecordActivityRequest request)
    {
        var result = insights.Record(request);

        return result is null
            ? BadRequest(new ProblemDetails
            {
                Title = "Nothing to record.",
                Detail = "A search needs some text, and an inspection needs a registered device.",
                Status = StatusCodes.Status400BadRequest,
                Instance = Request.Path
            })
            : Ok(result);
    }

    /// <summary>
    /// Accepts a suggestion. For an action this issues the command, which lands on
    /// the undo stack like any other.
    /// </summary>
    /// <response code="404">The suggestion is no longer current.</response>
    [HttpPost("accept")]
    [ProducesResponseType(typeof(SuggestionAcceptedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult Accept([FromBody] SuggestionKeyRequest request)
    {
        var (status, response, message) = insights.Accept(request.Key);

        return response is not null
            ? Ok(response)
            : StatusCode(status, new ProblemDetails
            {
                Title = "The suggestion could not be accepted.",
                Detail = message,
                Status = status,
                Instance = Request.Path
            });
    }

    /// <summary>Hides a suggestion for a while.</summary>
    [HttpPost("dismiss")]
    [ProducesResponseType(typeof(InsightsResponse), StatusCodes.Status200OK)]
    public ActionResult<InsightsResponse> Dismiss([FromBody] SuggestionKeyRequest request)
        => Ok(insights.Dismiss(request.Key));

    /// <summary>
    /// Everything the engine has learned: each rule with its support, confidence and
    /// lift. Shown in the console so the suggestions are never a black box.
    /// </summary>
    /// <param name="minSupport">Fewest observations.</param>
    /// <param name="minConfidence">Lowest confidence, 0 to 1.</param>
    [HttpGet("patterns")]
    [ProducesResponseType(typeof(List<PatternRuleResponse>), StatusCodes.Status200OK)]
    public ActionResult<List<PatternRuleResponse>> Patterns(
        [FromQuery] int? minSupport = null,
        [FromQuery] double? minConfidence = null)
        => Ok(insights.Patterns(
            Math.Max(1, minSupport ?? store.Options.Advisor.MinSupport),
            Math.Clamp(minConfidence ?? store.Options.Advisor.MinConfidence, 0d, 1d)));
}
