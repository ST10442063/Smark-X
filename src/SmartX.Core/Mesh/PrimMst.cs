using SmartX.Core.Devices;

namespace SmartX.Core.Mesh;

/// <summary>
/// Prim's algorithm: the minimum spanning tree of the live mesh, grown outward
/// from the gateway one cheapest link at a time.
/// <para>
/// <b>What the tree means for the mesh.</b> A spanning tree connects every
/// reachable node with no cycles, so it is a complete routing table - each node
/// forwards to its parent and every packet has exactly one way home. Of all such
/// trees, the minimum one has the lowest total link cost. With links weighted by
/// ETX, that is the backbone that keeps every node connected for the fewest
/// expected transmissions in total, and so for the least radio energy: the
/// "most power-efficient routing" the brief asks for.
/// </para>
/// <para>
/// <b>How it works.</b> Start with the gateway alone in the tree. Every node
/// outside waits in a min-heap at the cost of the cheapest link joining it to the
/// tree. Pop the cheapest, add it with that link, then offer each of its
/// neighbours the link through it - a decrease-key whenever that is cheaper than
/// what they were waiting at. The cut property guarantees each popped link belongs
/// to some minimum spanning tree.
/// </para>
/// <para>
/// <b>Why Prim for a mesh.</b> Growing from one root gives the routing tree
/// directly, already oriented towards the gateway, and only the gateway's own
/// connected piece is ever explored. With the <see cref="IndexedMinHeap{TKey}"/>
/// each of the V pops and up to E decrease-keys costs O(log V): O(E log V) overall,
/// against O(E log E) for Kruskal, whose first step is sorting every link.
/// </para>
/// </summary>
public static class PrimMst
{
    /// <summary>Algorithm name as reported to the console.</summary>
    public const string Name = "Prim";

    /// <summary>Builds the minimum spanning tree of the live mesh rooted at <paramref name="root"/>.</summary>
    /// <param name="graph">The mesh.</param>
    /// <param name="root">The gateway.</param>
    /// <param name="isUp">Which vertices count as up. Defaults to each vertex's own state.</param>
    public static SpanningTreeResult Build(MeshGraph graph, MacKey root, Func<MacKey, bool>? isUp = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        isUp ??= graph.IsUp;

        var liveVertices = graph.Vertices.Count(vertex => isUp(vertex.Mac));
        var parents = new Dictionary<MacKey, MacKey>();
        var rootLive = graph.Contains(root) && isUp(root);
        var steps = 0L;

        if (rootLive)
        {
            var inTree = new HashSet<MacKey>();
            var cheapestLink = new Dictionary<MacKey, MeshLink>();
            var heap = new IndexedMinHeap<MacKey>();
            heap.Push(root, 0);

            while (heap.Count > 0)
            {
                var (current, _) = heap.Pop();
                inTree.Add(current);
                steps++;

                if (cheapestLink.TryGetValue(current, out var joinedBy))
                {
                    parents[current] = joinedBy.Other(current);
                }

                foreach (var (neighbour, link) in graph.LiveNeighbours(current, isUp))
                {
                    // A strictly cheaper link only, so an equal-cost tie keeps the
                    // link found first and the tree is the same on every run.
                    if (!inTree.Contains(neighbour) && heap.TryDecrease(neighbour, link.Cost))
                    {
                        cheapestLink[neighbour] = link;
                    }
                }
            }
        }

        return SpanningTreeResult.FromParents(Name, graph, root, rootLive, parents, liveVertices, steps);
    }
}
