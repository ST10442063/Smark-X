namespace SmartX.Core.Models;

/// <summary>
/// Where a node physically sits in the deployment hierarchy,
/// e.g. Facility A -&gt; Zone 1 -&gt; Sub-Zone B -&gt; NODE-07.
/// </summary>
/// <param name="Facility">Top-level site, e.g. "Facility A".</param>
/// <param name="Zone">Zone within the facility, e.g. "Zone 1".</param>
/// <param name="SubZone">Optional sub-zone, e.g. "Sub-Zone B". May be blank for zone-level nodes.</param>
/// <param name="NodeId">Human-readable node label, e.g. "NODE-07".</param>
public sealed record DeploymentLocation(
    string Facility,
    string Zone,
    string SubZone,
    string NodeId)
{
    /// <summary>
    /// Renders the location as a single qualified path for display and logging.
    /// Blank segments are omitted so a zone-level node does not render "A / 1 //  N".
    /// </summary>
    public string QualifiedPath =>
        string.Join(" / ", new[] { Facility, Zone, SubZone, NodeId }
            .Where(segment => !string.IsNullOrWhiteSpace(segment)));

    /// <inheritdoc />
    public override string ToString() => QualifiedPath;
}
