using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Contracts.Deployment;
using SmartX.Core.Models;
using SmartX.Core.Validation;

namespace SmartX.Api.Controllers;

/// <summary>
/// Validation of multi-tier deployment profiles.
/// <para>
/// An operator posts a nested configuration - Facility, Zones, Sub-Zones, Devices -
/// and the gateway confirms every device is safely configured within its branch
/// before any of it is applied. The walk is recursive because the profile has no
/// fixed depth; see <see cref="RecursiveDeploymentValidator"/> for why that matters
/// and how the descent is bounded.
/// </para>
/// </summary>
[ApiController]
[Route("api/deployment")]
[Produces("application/json")]
[Tags("Deployment")]
public sealed class DeploymentController(GatewayStore store) : ControllerBase
{
    /// <summary>
    /// Validates a deployment profile and resolves each device inherited
    /// configuration.
    /// </summary>
    /// <param name="request">The profile, rooted at a Facility.</param>
    /// <response code="200">
    /// The findings. A structurally invalid profile still returns 200 with
    /// <c>isValid: false</c> - the request itself was well-formed, and the caller
    /// wants the list of problems rather than a bare rejection.
    /// </response>
    /// <response code="400">The request could not be bound at all.</response>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidateDeploymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<ValidateDeploymentResponse> Validate([FromBody] ValidateDeploymentRequest request)
    {
        var root = ToDomain(request.Root, depth: 0);
        var result = RecursiveDeploymentValidator.Validate(root);

        var devices = result.Devices.Select(device => new ResolvedDeviceResponse
        {
            NodeId = device.NodeId,
            MacAddress = device.MacAddress,
            QualifiedPath = device.QualifiedPath,
            EffectiveHeartbeatSeconds = device.EffectiveHeartbeatSeconds,
            Depth = device.Depth,
            IsRegistered = store.FindByMac(device.MacAddress).Sensor is not null
        }).ToList();

        return Ok(new ValidateDeploymentResponse
        {
            IsValid = result.IsValid,
            NodesVisited = result.NodesVisited,
            MaxDepthReached = result.MaxDepthReached,
            MaxDepthLimit = RecursiveDeploymentValidator.MaxDepth,
            Devices = devices,
            Issues = result.Issues.Select(issue => new ValidationIssueResponse
            {
                Severity = issue.Severity.ToString(),
                Code = issue.Code,
                Path = issue.Path,
                Message = issue.Message
            }).ToList(),
            Summary = Summarise(result, devices.Count)
        });
    }

    /// <summary>
    /// A worked example profile, so the client can populate its editor with
    /// something valid to start from.
    /// </summary>
    /// <response code="200">The example profile.</response>
    [HttpGet("example")]
    [ProducesResponseType(typeof(ValidateDeploymentRequest), StatusCodes.Status200OK)]
    public ActionResult<ValidateDeploymentRequest> Example()
    {
        // Built from whatever is actually registered, so validating the example
        // exercises the is-registered check rather than reporting unknown devices.
        var registered = store.AllSensors().Take(3).ToList();

        var devices = registered.Count > 0
            ? registered.Select((sensor, index) => new DeploymentNodeDto
            {
                Id = $"device-{index + 1}",
                Name = sensor.Location.NodeId,
                Kind = "Device",
                MacAddress = sensor.MacAddress
            }).ToList()
            : [new DeploymentNodeDto
            {
                Id = "device-1",
                Name = "NODE-07",
                Kind = "Device",
                MacAddress = "A4:CF:12:9B:0E:77"
            }];

        return Ok(new ValidateDeploymentRequest
        {
            Root = new DeploymentNodeDto
            {
                Id = "facility-a",
                Name = "Facility A",
                Kind = "Facility",
                HeartbeatIntervalSeconds = 60,
                Children =
                [
                    new DeploymentNodeDto
                    {
                        Id = "zone-1",
                        Name = "Zone 1",
                        Kind = "Zone",
                        Children =
                        [
                            new DeploymentNodeDto
                            {
                                Id = "sub-b",
                                Name = "Sub-Zone B",
                                Kind = "SubZone",
                                HeartbeatIntervalSeconds = 15,
                                Children = devices
                            }
                        ]
                    }
                ]
            }
        });
    }

    /// <summary>
    /// Projects the wire profile onto the domain tree.
    /// <para>
    /// Bounded by the same depth limit the validator uses, because this projection
    /// runs <i>before</i> validation: a hostile payload nested ten thousand deep
    /// would otherwise exhaust the stack here, having never reached the guard that
    /// exists to prevent exactly that.
    /// </para>
    /// </summary>
    private static DeploymentNode ToDomain(DeploymentNodeDto dto, int depth)
    {
        var node = new DeploymentNode
        {
            Id = dto.Id,
            Name = dto.Name,
            Kind = ContractMapper.ParseNodeKind(dto.Kind),
            HeartbeatIntervalSeconds = dto.HeartbeatIntervalSeconds,
            MacAddress = string.IsNullOrWhiteSpace(dto.MacAddress)
                ? null
                : MacAddress.TryNormalise(dto.MacAddress, out var normalised) ? normalised : dto.MacAddress
        };

        if (depth > RecursiveDeploymentValidator.MaxDepth)
        {
            // Stop projecting one tier past the limit, not at it. The validator only
            // reports MAX_DEPTH when it meets a vertex beyond the limit, so cutting the
            // tree off exactly at the limit made that vertex vanish and turned the
            // finding into a misleading EMPTY_CONTAINER on the last tier instead.
            return node;
        }

        foreach (var child in dto.Children)
        {
            if (child is not null)
            {
                node.Children.Add(ToDomain(child, depth + 1));
            }
        }

        return node;
    }

    private static string Summarise(DeploymentValidationResult result, int deviceCount)
    {
        if (result.IsValid)
        {
            var warnings = result.Warnings.Count();
            var summary = $"Profile is valid: {result.NodesVisited} vertices, {deviceCount} device" +
                          $"{(deviceCount == 1 ? string.Empty : "s")} resolved.";

            return warnings == 0 ? summary : $"{summary} {warnings} warning{(warnings == 1 ? string.Empty : "s")}.";
        }

        var errors = result.Errors.Count();
        return $"Profile is invalid: {errors} blocking issue{(errors == 1 ? string.Empty : "s")} " +
               $"across {result.NodesVisited} vertices.";
    }
}
