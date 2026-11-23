namespace SmartX.Core.Validation;

/// <summary>
/// How serious a finding from the recursive validator is.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>Worth flagging, but the profile can still be deployed.</summary>
    Warning = 0,

    /// <summary>The profile is structurally invalid and must not be deployed.</summary>
    Error = 1
}

/// <summary>
/// One finding against a specific vertex of the deployment tree.
/// </summary>
/// <param name="Severity">Whether this blocks deployment.</param>
/// <param name="Code">Stable machine-readable code, e.g. "TIER_ORDER".</param>
/// <param name="Path">Qualified path to the offending vertex, e.g. "Facility A / Zone 1 / NODE-07".</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Path,
    string Message);

/// <summary>
/// A device vertex with its configuration resolved through inheritance.
/// </summary>
/// <param name="NodeId">Identifier of the device vertex.</param>
/// <param name="MacAddress">Physical address declared on the device.</param>
/// <param name="QualifiedPath">Full root-to-leaf path.</param>
/// <param name="EffectiveHeartbeatSeconds">Heartbeat after walking up the tree for an inherited value.</param>
/// <param name="Depth">Distance from the root, where the root is depth 0.</param>
public sealed record ResolvedDevice(
    string NodeId,
    string MacAddress,
    string QualifiedPath,
    int EffectiveHeartbeatSeconds,
    int Depth);

/// <summary>
/// Outcome of validating one deployment tree.
/// </summary>
public sealed class DeploymentValidationResult
{
    /// <summary>Every finding, in the order the walk encountered them.</summary>
    public List<ValidationIssue> Issues { get; } = [];

    /// <summary>Devices found, with their inherited configuration resolved.</summary>
    public List<ResolvedDevice> Devices { get; } = [];

    /// <summary>Total vertices visited by the walk.</summary>
    public int NodesVisited { get; set; }

    /// <summary>Deepest level the walk reached, where the root is 0.</summary>
    public int MaxDepthReached { get; set; }

    /// <summary>True when nothing blocking was found.</summary>
    public bool IsValid => !Issues.Any(issue => issue.Severity == ValidationSeverity.Error);

    /// <summary>Blocking findings only.</summary>
    public IEnumerable<ValidationIssue> Errors =>
        Issues.Where(issue => issue.Severity == ValidationSeverity.Error);

    /// <summary>Non-blocking findings only.</summary>
    public IEnumerable<ValidationIssue> Warnings =>
        Issues.Where(issue => issue.Severity == ValidationSeverity.Warning);
}
