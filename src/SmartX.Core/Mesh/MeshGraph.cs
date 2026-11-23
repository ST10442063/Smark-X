using SmartX.Core.Devices;

namespace SmartX.Core.Mesh;

/// <summary>
/// The physical IoT mesh as an undirected, weighted graph: each ESP32 node is a
/// vertex and each wireless link an edge weighted by its ETX cost.
/// <para>
/// <b>Why an adjacency list.</b> A mesh is sparse. A node hears the handful of
/// neighbours within about 70 metres, not the whole site, so the edge count grows
/// with the node count rather than its square. An adjacency matrix would spend
/// V x V cells - mostly empty - and make "who are my neighbours?" an O(V) row scan.
/// The list stores only the links that exist (O(V + E) memory) and enumerates a
/// node's neighbours in O(degree), which is the operation every traversal and
/// both spanning-tree algorithms perform on every step.
/// </para>
/// <para>
/// The list is a dictionary of dictionaries keyed by <see cref="MacKey"/>: the
/// outer lookup finds a node's neighbours in O(1) when a neighbour report arrives,
/// and the inner one finds a specific link in O(1) to update its signal strength,
/// with no scan of the neighbour list.
/// </para>
/// <para>Not thread-safe. The topology service serialises access.</para>
/// </summary>
public sealed class MeshGraph
{
    private readonly Dictionary<MacKey, MeshVertex> _vertices = [];
    private readonly Dictionary<MacKey, Dictionary<MacKey, MeshLink>> _adjacency = [];

    /// <summary>Vertices in the graph, including the gateway.</summary>
    public int VertexCount => _vertices.Count;

    /// <summary>Links in the graph, each counted once.</summary>
    public int LinkCount { get; private set; }

    /// <summary>The gateway vertex, once one has been added.</summary>
    public MeshVertex? Gateway { get; private set; }

    /// <summary>Every vertex, in MAC order so every algorithm sees the same order on every run.</summary>
    public IEnumerable<MeshVertex> Vertices => _vertices.Values.OrderBy(vertex => vertex.Mac);

    /// <summary>Every link once, in canonical endpoint order.</summary>
    public IEnumerable<MeshLink> Links
        => _adjacency
            .SelectMany(pair => pair.Value.Values.Where(link => link.A == pair.Key))
            .OrderBy(link => link.A)
            .ThenBy(link => link.B);

    /// <summary>Adds a vertex. Returns false if the MAC is already in the graph.</summary>
    /// <exception cref="InvalidOperationException">A second gateway was added.</exception>
    public bool AddVertex(MeshVertex vertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);

        if (_vertices.ContainsKey(vertex.Mac))
        {
            return false;
        }

        if (vertex.IsGateway)
        {
            if (Gateway is not null)
            {
                throw new InvalidOperationException("The mesh already has a gateway.");
            }

            Gateway = vertex;
        }

        _vertices[vertex.Mac] = vertex;
        _adjacency[vertex.Mac] = [];
        return true;
    }

    /// <summary>Removes a vertex and every link touching it. O(degree).</summary>
    public bool RemoveVertex(MacKey mac)
    {
        if (!_adjacency.Remove(mac, out var neighbours))
        {
            return false;
        }

        foreach (var neighbour in neighbours.Keys)
        {
            _adjacency[neighbour].Remove(mac);
        }

        LinkCount -= neighbours.Count;

        if (Gateway?.Mac == mac)
        {
            Gateway = null;
        }

        _vertices.Remove(mac);
        return true;
    }

    /// <summary>The vertex for a MAC, or null.</summary>
    public MeshVertex? Find(MacKey mac) => _vertices.GetValueOrDefault(mac);

    /// <summary>Whether the graph holds this MAC.</summary>
    public bool Contains(MacKey mac) => _vertices.ContainsKey(mac);

    /// <summary>
    /// Adds a link between two vertices, or returns the one already there unchanged;
    /// a fresh signal reading goes through <see cref="MeshLink.Report"/>. O(1).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Either endpoint is not in the graph.</exception>
    public MeshLink Connect(MacKey a, MacKey b, double rssiDbm)
    {
        var fromA = NeighboursOf(a);
        var fromB = NeighboursOf(b);

        if (fromA.TryGetValue(b, out var existing))
        {
            return existing;
        }

        var link = new MeshLink(a, b, rssiDbm);
        fromA[b] = link;
        fromB[a] = link;
        LinkCount++;
        return link;
    }

    /// <summary>Removes the link between two vertices, if there is one.</summary>
    public bool Disconnect(MacKey a, MacKey b)
    {
        if (!_adjacency.TryGetValue(a, out var fromA) || !fromA.Remove(b))
        {
            return false;
        }

        _adjacency[b].Remove(a);
        LinkCount--;
        return true;
    }

    /// <summary>The link between two vertices, if there is one. O(1).</summary>
    public MeshLink? LinkBetween(MacKey a, MacKey b)
        => _adjacency.TryGetValue(a, out var fromA) ? fromA.GetValueOrDefault(b) : null;

    /// <summary>A vertex's neighbours and the links to them, in MAC order.</summary>
    public IEnumerable<(MeshVertex Neighbour, MeshLink Link)> Neighbours(MacKey mac)
        => NeighboursOf(mac)
            .OrderBy(pair => pair.Key)
            .Select(pair => (_vertices[pair.Key], pair.Value));

    /// <summary>Number of links a vertex has.</summary>
    public int DegreeOf(MacKey mac) => NeighboursOf(mac).Count;

    /// <summary>
    /// The neighbours a packet could actually be sent to: both ends up. Every
    /// algorithm routes over this view, which is how a failed node drops out of
    /// every path without being removed from the graph.
    /// </summary>
    /// <param name="mac">The vertex.</param>
    /// <param name="isUp">Which vertices count as up. Defaults to each vertex's own <see cref="MeshVertex.IsUp"/>.</param>
    public IEnumerable<(MacKey Neighbour, MeshLink Link)> LiveNeighbours(MacKey mac, Func<MacKey, bool>? isUp = null)
    {
        isUp ??= IsUp;

        if (!isUp(mac))
        {
            yield break;
        }

        foreach (var (neighbour, link) in NeighboursOf(mac).OrderBy(pair => pair.Key))
        {
            if (isUp(neighbour))
            {
                yield return (neighbour, link);
            }
        }
    }

    /// <summary>Whether a vertex is in the graph and up.</summary>
    public bool IsUp(MacKey mac) => _vertices.TryGetValue(mac, out var vertex) && vertex.IsUp;

    private Dictionary<MacKey, MeshLink> NeighboursOf(MacKey mac)
        => _adjacency.TryGetValue(mac, out var neighbours)
            ? neighbours
            : throw new KeyNotFoundException($"{mac} is not in the mesh graph.");
}
