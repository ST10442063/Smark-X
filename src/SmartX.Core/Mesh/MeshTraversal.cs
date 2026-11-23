using SmartX.Core.Devices;

namespace SmartX.Core.Mesh;

/// <summary>What a breadth-first search from one vertex found.</summary>
public sealed class ReachabilityResult
{
    internal ReachabilityResult(
        MacKey source,
        Dictionary<MacKey, int> hops,
        Dictionary<MacKey, MacKey> parents,
        List<List<MacKey>> layers,
        HashSet<MacKey> down,
        HashSet<MacKey> stranded,
        int linksExamined)
    {
        Source = source;
        Hops = hops;
        Parents = parents;
        Layers = layers;
        Down = down;
        Stranded = stranded;
        LinksExamined = linksExamined;
    }

    /// <summary>Where the search started - normally the gateway.</summary>
    public MacKey Source { get; }

    /// <summary>Fewest hops from the source to every vertex it reached.</summary>
    public IReadOnlyDictionary<MacKey, int> Hops { get; }

    /// <summary>
    /// The vertex each reached vertex was discovered from. Following it back gives
    /// a fewest-hops path to the source.
    /// </summary>
    public IReadOnlyDictionary<MacKey, MacKey> Parents { get; }

    /// <summary>Vertices grouped by hop count: layer 0 is the source, layer 1 its neighbours, and so on.</summary>
    public IReadOnlyList<List<MacKey>> Layers { get; }

    /// <summary>Vertices that are down.</summary>
    public IReadOnlySet<MacKey> Down { get; }

    /// <summary>
    /// Vertices that are up but have no live path to the source: cut off because
    /// every route they had went through a node that is now down.
    /// </summary>
    public IReadOnlySet<MacKey> Stranded { get; }

    /// <summary>Links the search looked across - each live link at most twice.</summary>
    public int LinksExamined { get; }

    /// <summary>Whether the search reached <paramref name="mac"/>.</summary>
    public bool Reached(MacKey mac) => Hops.ContainsKey(mac);

    /// <summary>The fewest-hops path from <paramref name="mac"/> back to the source, or empty if unreachable.</summary>
    public List<MacKey> PathFrom(MacKey mac)
    {
        var path = new List<MacKey>();

        if (!Reached(mac))
        {
            return path;
        }

        var current = mac;
        path.Add(current);

        while (current != Source)
        {
            current = Parents[current];
            path.Add(current);
        }

        return path;
    }
}

/// <summary>
/// The graph traversals the topology page runs: breadth-first search for
/// reachability and hop counts, depth-first search for a node's downstream
/// subnet, and Tarjan's depth-first articulation-point search for the relays
/// whose loss would split the mesh.
/// </summary>
public static class MeshTraversal
{
    /// <summary>
    /// Breadth-first search from <paramref name="source"/> across live links.
    /// <para>
    /// <b>Why BFS.</b> BFS explores in rings: every vertex one hop away, then every
    /// vertex two hops away. The first time it reaches a vertex is therefore by the
    /// fewest hops possible, which is exactly the hop count a mesh reports, and the
    /// order it reaches vertices in is the order the layers are drawn. Every vertex
    /// and link is handled at most once, so the search is O(V + E).
    /// </para>
    /// <para>
    /// Anything up but never reached is stranded, which is the "which devices are
    /// cut off" answer; anything down is reported separately so the two can be told
    /// apart on the dashboard.
    /// </para>
    /// </summary>
    /// <param name="graph">The mesh.</param>
    /// <param name="source">Where to start; the gateway when asking what is connected to it.</param>
    /// <param name="isUp">Which vertices count as up. Defaults to each vertex's own state.</param>
    public static ReachabilityResult BreadthFirst(MeshGraph graph, MacKey source, Func<MacKey, bool>? isUp = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        isUp ??= graph.IsUp;

        var hops = new Dictionary<MacKey, int>();
        var parents = new Dictionary<MacKey, MacKey>();
        var layers = new List<List<MacKey>>();
        var examined = 0;

        if (graph.Contains(source) && isUp(source))
        {
            var queue = new Queue<MacKey>();
            hops[source] = 0;
            layers.Add([source]);
            queue.Enqueue(source);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var nextHop = hops[current] + 1;

                foreach (var (neighbour, _) in graph.LiveNeighbours(current, isUp))
                {
                    examined++;

                    // Already seen means already reached by a path at least as short.
                    if (!hops.TryAdd(neighbour, nextHop))
                    {
                        continue;
                    }

                    parents[neighbour] = current;

                    if (layers.Count == nextHop)
                    {
                        layers.Add([]);
                    }

                    layers[nextHop].Add(neighbour);
                    queue.Enqueue(neighbour);
                }
            }
        }

        var down = new HashSet<MacKey>();
        var stranded = new HashSet<MacKey>();

        foreach (var vertex in graph.Vertices)
        {
            if (!isUp(vertex.Mac))
            {
                down.Add(vertex.Mac);
            }
            else if (!hops.ContainsKey(vertex.Mac))
            {
                stranded.Add(vertex.Mac);
            }
        }

        return new ReachabilityResult(source, hops, parents, layers, down, stranded, examined);
    }

    /// <summary>
    /// Every vertex downstream of <paramref name="root"/> in a routing tree: the
    /// subnet whose traffic flows through it, and so the devices that lose their
    /// route if it fails.
    /// <para>
    /// Depth-first, with an explicit <see cref="Stack{T}"/> rather than recursion,
    /// because a routing tree over a long chain of repeaters can be as deep as the
    /// mesh is wide and should not be bounded by the call stack. Each entry carries
    /// its depth below the root.
    /// </para>
    /// </summary>
    /// <param name="parents">The routing tree, as each vertex's next hop towards the gateway.</param>
    /// <param name="root">The vertex whose subtree is wanted.</param>
    /// <returns>The subtree in depth-first pre-order, excluding the root itself.</returns>
    public static List<(MacKey Mac, int Depth)> Downstream(IReadOnlyDictionary<MacKey, MacKey> parents, MacKey root)
    {
        ArgumentNullException.ThrowIfNull(parents);

        // The tree is stored child -> parent; walking down it needs parent -> children.
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

        var result = new List<(MacKey, int)>();
        var stack = new Stack<(MacKey Mac, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();

            if (current != root)
            {
                result.Add((current, depth));
            }

            if (children.TryGetValue(current, out var below))
            {
                // Pushed in reverse so they pop, and are listed, in MAC order.
                foreach (var child in below.OrderDescending())
                {
                    stack.Push((child, depth + 1));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The articulation points of the live mesh: nodes whose failure would split it
    /// into pieces that can no longer reach each other - the single points of
    /// failure an engineer should add a second relay beside.
    /// <para>
    /// Tarjan's algorithm. A depth-first search numbers each vertex in the order it
    /// is discovered, and tracks for each one the lowest number reachable from its
    /// subtree by a single back edge. If no vertex in a child's subtree can reach
    /// above the parent, the parent is the only way out of that subtree, so it is
    /// an articulation point. The DFS root is a special case: it is one only if it
    /// has two or more DFS children. One pass, O(V + E).
    /// </para>
    /// <para>Iterative, with an explicit stack of (vertex, next neighbour) frames, for the same reason as <see cref="Downstream"/>.</para>
    /// </summary>
    /// <param name="graph">The mesh.</param>
    /// <param name="isUp">Which vertices count as up.</param>
    public static HashSet<MacKey> ArticulationPoints(MeshGraph graph, Func<MacKey, bool>? isUp = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        isUp ??= graph.IsUp;

        var discovered = new Dictionary<MacKey, int>();
        var low = new Dictionary<MacKey, int>();
        var parent = new Dictionary<MacKey, MacKey>();
        var points = new HashSet<MacKey>();
        var clock = 0;

        foreach (var start in graph.Vertices.Where(vertex => isUp(vertex.Mac)).Select(vertex => vertex.Mac))
        {
            if (discovered.ContainsKey(start))
            {
                continue;
            }

            var rootChildren = 0;
            var stack = new Stack<(MacKey Vertex, MacKey[] Neighbours, int Next)>();

            discovered[start] = low[start] = clock++;
            stack.Push((start, LiveNeighbourArray(graph, start, isUp), 0));

            while (stack.Count > 0)
            {
                var (vertex, neighbours, next) = stack.Pop();

                if (next < neighbours.Length)
                {
                    // Come back to this vertex for its remaining neighbours.
                    stack.Push((vertex, neighbours, next + 1));
                    var neighbour = neighbours[next];

                    if (!discovered.ContainsKey(neighbour))
                    {
                        parent[neighbour] = vertex;
                        discovered[neighbour] = low[neighbour] = clock++;

                        if (vertex == start)
                        {
                            rootChildren++;
                        }

                        stack.Push((neighbour, LiveNeighbourArray(graph, neighbour, isUp), 0));
                    }
                    else if (!parent.TryGetValue(vertex, out var vertexParent) || neighbour != vertexParent)
                    {
                        // A back edge: the subtree can climb to an earlier vertex this way.
                        low[vertex] = Math.Min(low[vertex], discovered[neighbour]);
                    }
                }
                else if (parent.TryGetValue(vertex, out var above))
                {
                    // Finished with this vertex: hand its low value up to its parent.
                    low[above] = Math.Min(low[above], low[vertex]);

                    if (above != start && low[vertex] >= discovered[above])
                    {
                        points.Add(above);
                    }
                }
            }

            if (rootChildren > 1)
            {
                points.Add(start);
            }
        }

        return points;
    }

    /// <summary>
    /// The live mesh split into its connected pieces, the largest first. A healthy
    /// mesh is one piece; each extra piece is an island cut off from the others.
    /// </summary>
    public static List<List<MacKey>> Components(MeshGraph graph, Func<MacKey, bool>? isUp = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        isUp ??= graph.IsUp;

        var seen = new HashSet<MacKey>();
        var components = new List<List<MacKey>>();

        foreach (var vertex in graph.Vertices)
        {
            if (!isUp(vertex.Mac) || seen.Contains(vertex.Mac))
            {
                continue;
            }

            var reach = BreadthFirst(graph, vertex.Mac, isUp);
            var component = reach.Hops.Keys.Order().ToList();
            seen.UnionWith(component);
            components.Add(component);
        }

        return components.OrderByDescending(component => component.Count).ToList();
    }

    private static MacKey[] LiveNeighbourArray(MeshGraph graph, MacKey mac, Func<MacKey, bool> isUp)
        => graph.LiveNeighbours(mac, isUp).Select(pair => pair.Neighbour).ToArray();
}
