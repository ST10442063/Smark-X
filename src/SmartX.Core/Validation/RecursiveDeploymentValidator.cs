using System.Text.RegularExpressions;
using SmartX.Core.Models;

namespace SmartX.Core.Validation;

/// <summary>
/// Validates a multi-tier deployment profile by recursive descent, confirming that
/// a node is safely configured within its branch - for example that NODE-07 really
/// does sit inside Sub-Zone B, inside Zone 1, inside Facility A.
/// <para>
/// <b>Why recursion.</b> The tree has no fixed depth: a small installation is
/// Facility -&gt; Device, a large one nests sub-zones several levels down, and the
/// gateway is handed whichever shape the operator configured. An iterative loop
/// would need an explicit stack and would still have to carry the inherited
/// configuration down by hand. Recursion lets each call hold its own slice of the
/// ancestry - the depth, the path so far, the inherited heartbeat - and hand it to
/// its children, which is the shape of the problem.
/// </para>
/// <para>
/// <b>Why it cannot run away.</b> Two guards. <see cref="MaxDepth"/> caps the
/// descent, so a pathologically deep profile records an error and stops rather than
/// exhausting the stack. And the set of ancestors on the current path is tracked by
/// reference, so a profile that contains itself as its own descendant is detected
/// and cut off instead of recursing forever.
/// </para>
/// </summary>
public static partial class RecursiveDeploymentValidator
{
    /// <summary>
    /// Deepest level the walk will descend to. Real deployments nest four or five
    /// tiers; 32 is far beyond anything legitimate while staying nowhere near the
    /// stack limit, so hitting it means the profile is malformed.
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>Fallback heartbeat when no tier in the branch declares one.</summary>
    public const int DefaultHeartbeatSeconds = 30;

    [GeneratedRegex(@"^([0-9A-F]{2}:){5}[0-9A-F]{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex MacPattern();

    /// <summary>
    /// Walks the tree from <paramref name="root"/> and reports every structural
    /// problem it finds, along with each device and its resolved configuration.
    /// </summary>
    /// <param name="root">Root of the deployment profile.</param>
    /// <returns>The findings. Never null, never throws for a malformed tree.</returns>
    public static DeploymentValidationResult Validate(DeploymentNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var result = new DeploymentValidationResult();

        // Ancestors on the *current* path, compared by reference. A node object
        // reused as a sibling elsewhere is legitimate; a node that is its own
        // ancestor is a cycle.
        var ancestors = new HashSet<DeploymentNode>(ReferenceEqualityComparer.Instance);

        // Ids seen anywhere in the tree, so duplicates can be flagged.
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.Kind != DeploymentNodeKind.Facility)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "ROOT_TIER",
                root.Name,
                $"A deployment profile must be rooted at a Facility, but this one starts at {root.Kind}."));
        }

        Descend(root, parentPath: string.Empty, depth: 0, inheritedHeartbeat: null,
                ancestors, seenIds, result);

        if (result.Devices.Count == 0)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "NO_DEVICES",
                root.Name,
                "The profile contains no device vertices, so nothing would publish telemetry."));
        }

        return result;
    }

    /// <summary>
    /// The recursive step. Validates one vertex, then descends into its children
    /// carrying the path, depth and inherited heartbeat resolved so far.
    /// </summary>
    private static void Descend(
        DeploymentNode node,
        string parentPath,
        int depth,
        int? inheritedHeartbeat,
        HashSet<DeploymentNode> ancestors,
        HashSet<string> seenIds,
        DeploymentValidationResult result)
    {
        var path = string.IsNullOrEmpty(parentPath) ? node.Name : $"{parentPath} / {node.Name}";

        // --- Guard 1: depth. Stop before the stack is at risk. ---
        if (depth > MaxDepth)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "MAX_DEPTH",
                path,
                $"Deployment nesting exceeded the {MaxDepth}-tier limit and was not descended further."));
            return;
        }

        // --- Guard 2: cycles. A vertex that is its own ancestor never terminates. ---
        if (!ancestors.Add(node))
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "CYCLE",
                path,
                $"Vertex '{node.Name}' appears inside its own subtree, which would never terminate."));
            return;
        }

        result.NodesVisited++;
        result.MaxDepthReached = Math.Max(result.MaxDepthReached, depth);

        ValidateVertex(node, path, seenIds, result);

        // Inheritance: this tier overrides its ancestors, otherwise it passes theirs down.
        var effectiveHeartbeat = node.HeartbeatIntervalSeconds ?? inheritedHeartbeat;

        // --- Base case: a device is a leaf, so the branch resolves here. ---
        if (node.Kind == DeploymentNodeKind.Device)
        {
            if (node.Children.Count > 0)
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "DEVICE_HAS_CHILDREN",
                    path,
                    $"Device '{node.Name}' has {node.Children.Count} child vertices. A device must be a leaf."));
            }

            result.Devices.Add(new ResolvedDevice(
                node.Id,
                node.MacAddress ?? string.Empty,
                path,
                effectiveHeartbeat ?? DefaultHeartbeatSeconds,
                depth));

            ancestors.Remove(node);
            return;
        }

        // --- Base case: a non-device leaf is a container with nothing in it. ---
        if (node.IsLeaf)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "EMPTY_CONTAINER",
                path,
                $"{node.Kind} '{node.Name}' contains no child vertices."));

            ancestors.Remove(node);
            return;
        }

        // --- Recursive step: descend into every child. ---
        foreach (var child in node.Children)
        {
            if (child is null)
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "NULL_CHILD",
                    path,
                    $"'{node.Name}' contains a null child vertex."));
                continue;
            }

            // A child must sit below its parent in the tier order: a Zone may hold a
            // Sub-Zone or a Device, but never another Facility. Sub-Zones are the one
            // tier that may repeat - a greenhouse bay inside a rack inside a row - which
            // is what lets a real site nest as deeply as its layout needs.
            if (child.Kind < node.Kind
                || (child.Kind == node.Kind && node.Kind != DeploymentNodeKind.SubZone))
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "TIER_ORDER",
                    $"{path} / {child.Name}",
                    $"A {child.Kind} cannot be nested inside a {node.Kind}."));
            }

            Descend(child, path, depth + 1, effectiveHeartbeat, ancestors, seenIds, result);
        }

        // Leaving this branch: the vertex is no longer an ancestor of anything.
        ancestors.Remove(node);
    }

    /// <summary>Checks the rules that apply to a single vertex in isolation.</summary>
    private static void ValidateVertex(
        DeploymentNode node,
        string path,
        HashSet<string> seenIds,
        DeploymentValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(node.Id))
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error, "MISSING_ID", path, "Vertex has no identifier."));
        }
        else if (!seenIds.Add(node.Id))
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "DUPLICATE_ID",
                path,
                $"Identifier '{node.Id}' is used by more than one vertex in this profile."));
        }

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error, "MISSING_NAME", path, "Vertex has no display name."));
        }

        if (node.HeartbeatIntervalSeconds is { } heartbeat && heartbeat is < 1 or > 3600)
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "HEARTBEAT_RANGE",
                path,
                $"Heartbeat interval {heartbeat}s is outside the supported range of 1-3600 seconds."));
        }

        if (node.Kind == DeploymentNodeKind.Device)
        {
            if (string.IsNullOrWhiteSpace(node.MacAddress))
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "DEVICE_NO_MAC",
                    path,
                    $"Device '{node.Name}' has no MAC address, so telemetry could not be attributed to it."));
            }
            else if (!MacPattern().IsMatch(node.MacAddress))
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "DEVICE_BAD_MAC",
                    path,
                    $"'{node.MacAddress}' is not a valid MAC address. Expected the form AA:BB:CC:DD:EE:FF."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(node.MacAddress))
        {
            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "NON_DEVICE_MAC",
                path,
                $"{node.Kind} '{node.Name}' declares a MAC address. Only devices have one."));
        }
    }

    /// <summary>
    /// Builds the qualified path from the root down to the vertex with
    /// <paramref name="targetId"/>, e.g. "Facility A / Zone 1 / Sub-Zone B / NODE-07".
    /// Returns null when the vertex is not in this tree.
    /// </summary>
    /// <remarks>
    /// Recursive depth-first search. Each frame prepends its own name onto whatever
    /// its children resolve, so the path assembles itself as the recursion unwinds.
    /// </remarks>
    public static string? BuildQualifiedPath(DeploymentNode node, string targetId, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (depth > MaxDepth)
        {
            return null;
        }

        if (string.Equals(node.Id, targetId, StringComparison.OrdinalIgnoreCase))
        {
            return node.Name;
        }

        foreach (var child in node.Children)
        {
            if (child is null)
            {
                continue;
            }

            var childPath = BuildQualifiedPath(child, targetId, depth + 1);
            if (childPath is not null)
            {
                return $"{node.Name} / {childPath}";
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a vertex by identifier anywhere in the tree, or null if absent.
    /// </summary>
    public static DeploymentNode? Find(DeploymentNode node, string targetId, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (depth > MaxDepth)
        {
            return null;
        }

        if (string.Equals(node.Id, targetId, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (child is null)
            {
                continue;
            }

            var found = Find(child, targetId, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Counts every vertex in the subtree, including <paramref name="node"/> itself.
    /// </summary>
    public static int CountNodes(DeploymentNode node, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (depth > MaxDepth)
        {
            return 0;
        }

        // Base case is implicit: a leaf has no children, so the fold returns 1.
        return 1 + node.Children
            .Where(child => child is not null)
            .Sum(child => CountNodes(child, depth + 1));
    }
}
