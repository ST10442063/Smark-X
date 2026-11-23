namespace SmartX.Core.Models;

/// <summary>
/// One vertex of the multi-tier deployment tree, e.g.
/// <c>Facility A -&gt; Zone 1 -&gt; Sub-Zone B -&gt; NODE-07</c>.
/// <para>
/// Configuration profiles are nested rather than flat because a real IoT estate
/// inherits settings downward: a facility sets a default heartbeat interval, a
/// zone may tighten it, and an individual device may override it again. Walking
/// that inheritance is naturally recursive, which is what
/// <c>RecursiveDeploymentValidator</c> does.
/// </para>
/// </summary>
public sealed class DeploymentNode
{
    /// <summary>Stable identifier, unique within a single tree.</summary>
    public required string Id { get; set; }

    /// <summary>Display label, e.g. "Sub-Zone B".</summary>
    public required string Name { get; set; }

    /// <summary>Which tier this vertex occupies.</summary>
    public required DeploymentNodeKind Kind { get; set; }

    /// <summary>
    /// Heartbeat interval declared at this tier, in seconds. Null means
    /// "inherit from my parent" - resolving that is part of the recursive walk.
    /// </summary>
    public int? HeartbeatIntervalSeconds { get; set; }

    /// <summary>
    /// MAC address of the physical device. Required on <see cref="DeploymentNodeKind.Device"/>
    /// vertices and must be absent on every other tier.
    /// </summary>
    public string? MacAddress { get; set; }

    /// <summary>Child vertices. Empty on a Device leaf.</summary>
    public List<DeploymentNode> Children { get; set; } = [];

    /// <summary>True when this vertex has no children.</summary>
    public bool IsLeaf => Children.Count == 0;

    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{Name}";
}
