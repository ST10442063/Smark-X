using System.ComponentModel.DataAnnotations;
using SmartX.Contracts.Validation;

namespace SmartX.Contracts.Deployment;

/// <summary>
/// One vertex of a deployment profile on the wire.
/// <para>
/// The type is self-referencing through <see cref="Children"/>, which is what
/// lets an operator post an arbitrarily nested profile in a single request - and
/// is why the gateway validates it by recursive descent rather than by iterating
/// a flat list.
/// </para>
/// </summary>
public sealed class DeploymentNodeDto
{
    /// <summary>Identifier, unique within the profile.</summary>
    [Required(ErrorMessage = "Every vertex needs an identifier.")]
    [StringLength(60)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Display label, e.g. "Sub-Zone B".</summary>
    [Required(ErrorMessage = "Every vertex needs a name.")]
    [StringLength(60)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Tier this vertex occupies.</summary>
    [Required]
    [AllowedValuesFrom("NodeKinds")]
    public string Kind { get; set; } = "Facility";

    /// <summary>Heartbeat declared at this tier. Null means inherit from the parent.</summary>
    [Range(1, 3600)]
    public int? HeartbeatIntervalSeconds { get; set; }

    /// <summary>MAC address. Required on a Device vertex, absent on every other tier.</summary>
    [MacAddress(AllowEmpty = true)]
    public string? MacAddress { get; set; }

    /// <summary>Child vertices. Empty on a Device leaf.</summary>
    public List<DeploymentNodeDto> Children { get; set; } = [];
}

/// <summary>Asks the gateway to validate a deployment profile.</summary>
public sealed class ValidateDeploymentRequest
{
    /// <summary>Root of the profile. Must be a Facility.</summary>
    [Required(ErrorMessage = "A deployment profile is required.")]
    public DeploymentNodeDto Root { get; set; } = new();
}

/// <summary>One finding against a vertex.</summary>
public sealed class ValidationIssueResponse
{
    /// <summary>"Warning" or "Error".</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>Machine-readable code, e.g. "TIER_ORDER".</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Qualified path to the offending vertex.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>A device with its inherited configuration resolved.</summary>
public sealed class ResolvedDeviceResponse
{
    /// <summary>Identifier of the device vertex.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Declared MAC address.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Full root-to-leaf path.</summary>
    public string QualifiedPath { get; set; } = string.Empty;

    /// <summary>Heartbeat after walking up the tree for an inherited value.</summary>
    public int EffectiveHeartbeatSeconds { get; set; }

    /// <summary>Distance from the root, where the root is 0.</summary>
    public int Depth { get; set; }

    /// <summary>Whether this MAC is already registered with the gateway.</summary>
    public bool IsRegistered { get; set; }
}

/// <summary>Outcome of validating a profile.</summary>
public sealed class ValidateDeploymentResponse
{
    /// <summary>True when nothing blocking was found.</summary>
    public bool IsValid { get; set; }

    /// <summary>Every finding, in the order the walk encountered them.</summary>
    public List<ValidationIssueResponse> Issues { get; set; } = [];

    /// <summary>Devices found, with their configuration resolved.</summary>
    public List<ResolvedDeviceResponse> Devices { get; set; } = [];

    /// <summary>Vertices the walk visited.</summary>
    public int NodesVisited { get; set; }

    /// <summary>Deepest tier the walk reached.</summary>
    public int MaxDepthReached { get; set; }

    /// <summary>The cap the walk will not descend past.</summary>
    public int MaxDepthLimit { get; set; }

    /// <summary>One-line summary for the client toast.</summary>
    public string Summary { get; set; } = string.Empty;
}
