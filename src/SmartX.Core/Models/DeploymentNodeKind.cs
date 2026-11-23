namespace SmartX.Core.Models;

/// <summary>
/// The tier a node occupies in the deployment tree. The recursive validator
/// enforces that these appear in descending order down any root-to-leaf path:
/// a Zone may not contain a Facility, and only a Device may be a leaf.
/// </summary>
public enum DeploymentNodeKind
{
    /// <summary>Top tier - a physical site, e.g. "Facility A".</summary>
    Facility = 0,

    /// <summary>A zone inside a facility, e.g. "Zone 1".</summary>
    Zone = 1,

    /// <summary>An optional subdivision of a zone, e.g. "Sub-Zone B".</summary>
    SubZone = 2,

    /// <summary>A physical mesh node. Always a leaf.</summary>
    Device = 3
}
