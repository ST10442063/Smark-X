using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Contracts.Attachments;

namespace SmartX.Api.Controllers;

/// <summary>
/// Device configuration files, deployment photos and hardware logs attached to a
/// sensor profile.
/// </summary>
[ApiController]
[Route("api/sensors/{sensorId:guid}/attachments")]
[Produces("application/json")]
[Tags("Attachments")]
public sealed class AttachmentsController(
    GatewayStore store,
    AttachmentService attachments) : ControllerBase
{
    /// <summary>
    /// Attaches a file to a sensor profile. The body is multipart form data with
    /// the file in a field named <c>file</c>.
    /// </summary>
    /// <param name="sensorId">Sensor to attach it to.</param>
    /// <param name="file">The uploaded file.</param>
    /// <param name="cancellationToken">Cancels the upload if the client disconnects.</param>
    /// <response code="201">The file was stored.</response>
    /// <response code="400">The file was empty, too large, or not an accepted type.</response>
    /// <response code="404">No node holds that key.</response>
    [HttpPost]
    [ProducesResponseType(typeof(AttachmentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    // The cap is enforced here as well as in the service so an oversized body is
    // rejected by the server before it is streamed, rather than after.
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<AttachmentResponse>> Upload(
        Guid sensorId,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (store.FindById(sensorId) is null)
        {
            return NotFound(NoSuchSensor(sensorId));
        }

        var outcome = await attachments.StoreAsync(sensorId, file, cancellationToken);

        if (!outcome.Succeeded)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "That file could not be attached.",
                Detail = outcome.Message,
                Status = StatusCodes.Status400BadRequest,
                Instance = Request.Path
            });
        }

        var attachment = outcome.Attachment!;
        store.AddAttachment(attachment);

        var response = ContractMapper.ToResponse(attachment);
        return CreatedAtAction(nameof(Download), new { sensorId, attachmentId = attachment.Id }, response);
    }

    /// <summary>Lists the files attached to a sensor profile, newest first.</summary>
    /// <param name="sensorId">Sensor key.</param>
    /// <response code="200">The attachments.</response>
    /// <response code="404">No node holds that key.</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<AttachmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<List<AttachmentResponse>> List(Guid sensorId)
    {
        if (store.FindById(sensorId) is null)
        {
            return NotFound(NoSuchSensor(sensorId));
        }

        return Ok(store.Attachments(sensorId).Select(ContractMapper.ToResponse).ToList());
    }

    /// <summary>Retrieves the bytes of one attachment.</summary>
    /// <param name="sensorId">Sensor key.</param>
    /// <param name="attachmentId">Attachment key.</param>
    /// <response code="200">The file.</response>
    /// <response code="404">No such sensor or attachment, or the bytes are missing from disk.</response>
    [HttpGet("{attachmentId:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult Download(Guid sensorId, Guid attachmentId)
    {
        var attachment = store.FindAttachment(sensorId, attachmentId);
        if (attachment is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No such attachment.",
                Detail = $"Sensor {sensorId} has no attachment {attachmentId}.",
                Status = StatusCodes.Status404NotFound,
                Instance = Request.Path
            });
        }

        var stream = attachments.OpenRead(attachment);
        if (stream is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "The attachment bytes are missing.",
                Detail = $"'{attachment.OriginalFileName}' is recorded against this sensor, " +
                         "but its contents are no longer on disk.",
                Status = StatusCodes.Status404NotFound,
                Instance = Request.Path
            });
        }

        // The download is named with the uploader's original file name for the
        // operator's benefit. FileStreamResult quotes and escapes it into the
        // Content-Disposition header, so it still never reaches a file path.
        return File(stream, attachment.ContentType, attachment.OriginalFileName);
    }

    /// <summary>The limits the upload endpoint enforces, so a client can state them up front.</summary>
    /// <response code="200">The upload policy.</response>
    [HttpGet("/api/attachments/policy")]
    [ProducesResponseType(typeof(AttachmentPolicyResponse), StatusCodes.Status200OK)]
    public ActionResult<AttachmentPolicyResponse> Policy() => Ok(new AttachmentPolicyResponse
    {
        MaxSizeBytes = attachments.MaxUploadBytes,
        MaxSizeDisplay = ContractMapper.FormatBytes(attachments.MaxUploadBytes),
        AllowedExtensions = [.. attachments.AllowedExtensions]
    });

    private ProblemDetails NoSuchSensor(Guid sensorId) => new()
    {
        Title = "No such sensor.",
        Detail = $"No sensor with key {sensorId} is registered with this gateway.",
        Status = StatusCodes.Status404NotFound,
        Instance = Request.Path
    };
}
