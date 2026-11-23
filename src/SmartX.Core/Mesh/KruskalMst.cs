using SmartX.Core.Devices;

namespace SmartX.Core.Mesh;

/// <summary>
/// Kruskal's algorithm: the minimum spanning tree built by taking links cheapest
/// first and keeping each one that joins two pieces not yet connected.
/// <para>
/// Sort every live link by cost. Walk the list; a link whose two ends are already
/// in the same piece would close a cycle and is skipped, any other link joins two
/// pieces and is kept. A <see cref="DisjointSet{T}"/> answers "same piece?" in
/// near-constant time, so the sort dominates: O(E log E).
/// </para>
/// <para>
/// Kruskal works on the whole graph at once rather than growing from a root, so a
/// partitioned mesh naturally comes out as a forest - one tree per island. The
/// console offers it beside <see cref="PrimMst"/> because two independent
/// algorithms agreeing on the total cost is strong evidence both are right; the
/// tests check exactly that on hundreds of random meshes.
/// </para>
/// </summary>
public static class KruskalMst
{
    /// <summary>Algorithm name as reported to the console.</summary>
    public const string Name = "Kruskal";

    /// <summary>
    /// Builds the minimum spanning forest of the live mesh, then returns the tree
    /// containing <paramref name="root"/>, oriented towards it.
    /// </summary>
    /// <param name="graph">The mesh.</param>
    /// <param name="root">The gateway.</param>
    /// <param name="isUp">Which vertices count as up.</param>
    public static SpanningTreeResult Build(MeshGraph graph, MacKey root, Func<MacKey, bool>? isUp = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        isUp ??= graph.IsUp;

        var pieces = new DisjointSet<MacKey>();
        var liveVertices = 0;

        foreach (var vertex in graph.Vertices.Where(vertex => isUp(vertex.Mac)))
        {
            pieces.MakeSet(vertex.Mac);
            liveVertices++;
        }

        // Cheapest first; ties broken by endpoints so the forest is the same every run.
        var candidates = graph.Links
            .Where(link => isUp(link.A) && isUp(link.B))
            .OrderBy(link => link.Cost)
            .ThenBy(link => link.A)
            .ThenBy(link => link.B);

        var forest = new Dictionary<MacKey, List<MacKey>>();
        var kept = 0;
        var steps = 0L;

        foreach (var link in candidates)
        {
            steps++;

            if (!pieces.Union(link.A, link.B))
            {
                continue;
            }

            Attach(forest, link.A, link.B);
            Attach(forest, link.B, link.A);

            // A forest over n vertices has at most n - 1 links; once the pieces are
            // all joined nothing further can be kept.
            if (++kept == liveVertices - 1)
            {
                break;
            }
        }

        // Orient the root's tree: a breadth-first walk out from the root along the
        // kept links gives every node in that tree its parent.
        var rootLive = graph.Contains(root) && isUp(root);
        var parents = new Dictionary<MacKey, MacKey>();

        if (rootLive)
        {
            var queue = new Queue<MacKey>();
            var seen = new HashSet<MacKey> { root };
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (!forest.TryGetValue(current, out var adjacent))
                {
                    continue;
                }

                foreach (var next in adjacent)
                {
                    if (seen.Add(next))
                    {
                        parents[next] = current;
                        queue.Enqueue(next);
                    }
                }
            }
        }

        return SpanningTreeResult.FromParents(Name, graph, root, rootLive, parents, liveVertices, steps);
    }

    private static void Attach(Dictionary<MacKey, List<MacKey>> forest, MacKey from, MacKey to)
    {
        if (!forest.TryGetValue(from, out var list))
        {
            list = [];
            forest[from] = list;
        }

        list.Add(to);
    }
}
