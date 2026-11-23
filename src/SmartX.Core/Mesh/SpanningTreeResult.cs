using SmartX.Core.Devices;

namespace SmartX.Core.Mesh;

/// <summary>
/// A minimum spanning tree of the live mesh, rooted at the gateway and read as a
/// routing tree: every node's parent is its next hop towards the gateway.
/// <para>
/// Only the gateway's own connected piece is spanned. A node with no live path to
/// the gateway has no route to compute, so it appears in no tree and is reported
/// as stranded instead.
/// </para>
/// </summary>
public sealed class SpanningTreeResult
{
    private readonly HashSet<(MacKey, MacKey)> _linkSet;

    private SpanningTreeResult(
        string algorithm,
        MacKey root,
        bool rootLive,
        List<MeshLink> links,
        Dictionary<MacKey, MacKey> parents,
        Dictionary<MacKey, double> pathCost,
        Dictionary<MacKey, int> depth,
        int liveVertices,
        long steps)
    {
        Algorithm = algorithm;
        Root = root;
        Links = links;
        Parents = parents;
        PathCost = pathCost;
        Depth = depth;
        TotalCost = Math.Round(links.Sum(link => link.Cost), 3);
        Spanned = rootLive ? parents.Count + 1 : 0;
        Unspanned = liveVertices - Spanned;
        Steps = steps;
        _linkSet = links.Select(link => (link.A, link.B)).ToHashSet();
    }

    /// <summary>"Prim" or "Kruskal".</summary>
    public string Algorithm { get; }

    /// <summary>The gateway the tree is rooted at.</summary>
    public MacKey Root { get; }

    /// <summary>The links in the tree - one fewer than the vertices it spans.</summary>
    public IReadOnlyList<MeshLink> Links { get; }

    /// <summary>Each spanned node's next hop towards the root.</summary>
    public IReadOnlyDictionary<MacKey, MacKey> Parents { get; }

    /// <summary>Each spanned node's total ETX cost to the root along the tree.</summary>
    public IReadOnlyDictionary<MacKey, double> PathCost { get; }

    /// <summary>Each spanned node's hop count to the root along the tree.</summary>
    public IReadOnlyDictionary<MacKey, int> Depth { get; }

    /// <summary>
    /// Sum of the tree's link costs: the expected transmissions needed to move one
    /// packet across every link of the backbone, which the tree minimises.
    /// </summary>
    public double TotalCost { get; }

    /// <summary>Vertices in the tree, including the root.</summary>
    public int Spanned { get; }

    /// <summary>Live vertices left out because they cannot reach the root.</summary>
    public int Unspanned { get; }

    /// <summary>The algorithm's main-loop iterations: heap pops for Prim, links considered for Kruskal.</summary>
    public long Steps { get; }

    /// <summary>Whether the tree spans <paramref name="mac"/>.</summary>
    public bool Contains(MacKey mac) => mac == Root ? Spanned > 0 : Parents.ContainsKey(mac);

    /// <summary>Whether a link is part of the tree.</summary>
    public bool Uses(MeshLink link) => _linkSet.Contains((link.A, link.B));

    /// <summary>The tree path from <paramref name="mac"/> up to the root, or empty if not spanned.</summary>
    public List<MacKey> PathFrom(MacKey mac)
    {
        var path = new List<MacKey>();

        if (!Contains(mac))
        {
            return path;
        }

        for (var current = mac; ; current = Parents[current])
        {
            path.Add(current);

            if (current == Root)
            {
                return path;
            }
        }
    }

    /// <summary>
    /// Builds the result from a parent map, deriving the links, path costs and
    /// depths with one breadth-first pass down the tree from the root.
    /// </summary>
    internal static SpanningTreeResult FromParents(
        string algorithm,
        MeshGraph graph,
        MacKey root,
        bool rootLive,
        Dictionary<MacKey, MacKey> parents,
        int liveVertices,
        long steps)
    {
        var children = new Dictionary<MacKey, List<MacKey>>();
        foreach (var (child, parent) in parents)
        {
            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }

            list.Add(child);
        }

        var links = new List<MeshLink>(parents.Count);
        var pathCost = new Dictionary<MacKey, double>();
        var depth = new Dictionary<MacKey, int>();

        if (rootLive)
        {
            pathCost[root] = 0;
            depth[root] = 0;
            var queue = new Queue<MacKey>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (!children.TryGetValue(current, out var below))
                {
                    continue;
                }

                foreach (var child in below.Order())
                {
                    var link = graph.LinkBetween(child, current)
                        ?? throw new InvalidOperationException($"The tree uses a link {child} - {current} the graph does not have.");

                    links.Add(link);
                    pathCost[child] = Math.Round(pathCost[current] + link.Cost, 3);
                    depth[child] = depth[current] + 1;
                    queue.Enqueue(child);
                }
            }
        }

        return new SpanningTreeResult(algorithm, root, rootLive, links, parents, pathCost, depth, liveVertices, steps);
    }
}
