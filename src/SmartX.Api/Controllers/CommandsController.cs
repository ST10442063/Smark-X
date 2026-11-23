using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services.Streaming;
using SmartX.Contracts.Commands;

namespace SmartX.Api.Controllers;

/// <summary>
/// Manual override commands, and the stack-backed undo and redo behind them.
/// </summary>
[ApiController]
[Route("api/commands")]
[Produces("application/json")]
[Tags("Commands")]
public sealed class CommandsController(CommandService commands) : ControllerBase
{
    /// <summary>Both command stacks, top (most recent) first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CommandHistoryResponse), StatusCodes.Status200OK)]
    public ActionResult<CommandHistoryResponse> History() => Ok(commands.History());

    /// <summary>
    /// Switches one or more actuators. Several targets make one bulk command - "shut
    /// down all valves" - that a single undo reverts. Each target's previous state
    /// is recorded so undo restores exactly what was there.
    /// </summary>
    /// <response code="200">Queued for delivery to each device.</response>
    /// <response code="400">A target is not an actuator, or not a MAC address.</response>
    /// <response code="404">A target is not registered.</response>
    [HttpPost]
    [ProducesResponseType(typeof(CommandOutcomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult Issue([FromBody] IssueCommandRequest request) => Respond(commands.Issue(request), "The command was refused.");

    /// <summary>Pops the undo stack and sends commands restoring the state before it.</summary>
    /// <response code="409">There is nothing to undo.</response>
    [HttpPost("undo")]
    [ProducesResponseType(typeof(CommandOutcomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult Undo() => Respond(commands.Undo(), "Nothing to undo.");

    /// <summary>Pops the redo stack and re-applies the command.</summary>
    /// <response code="409">There is nothing to redo.</response>
    [HttpPost("redo")]
    [ProducesResponseType(typeof(CommandOutcomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult Redo() => Respond(commands.Redo(), "Nothing to redo.");

    private IActionResult Respond(CommandResult result, string title)
        => result.Succeeded
            ? Ok(result.Outcome)
            : StatusCode(result.Status, new ProblemDetails
            {
                Title = title,
                Detail = result.Message,
                Status = result.Status,
                Instance = Request.Path
            });
}
