using SmartX.Core.Devices;
using SmartX.Core.Mesh;
using static SmartX.Core.Tests.Mesh.MeshTestGraphs;

namespace SmartX.Core.Tests.Mesh;

/// <summary>
/// Covers Prim's and Kruskal's algorithms. Each is checked against a textbook
/// graph with a known answer, against brute force over every spanning tree of
/// small graphs, and against each other on hundreds of random meshes.
/// </summary>
public class SpanningTreeTests
{
    public static TheoryData<string> Algorithms => [PrimMst.Name, KruskalMst.Name];

    /// <summary>
    /// The nine-vertex example from Cormen et al.'s Introduction to Algorithms,
    /// vertices a..i as 0..8. Weights are shifted to 1 + w/10 because an ETX can
    /// never be below 1; adding a constant to every edge does not change which
    /// spanning tree is minimum, since every spanning tree has the same edge count.
    /// </summary>
    private static MeshGraph Textbook() => Weighted(9,
        (0, 1, 1.4), (0, 7, 1.8), (1, 2, 1.8), (1, 7, 2.1), (2, 3, 1.7), (2, 5, 1.4), (2, 8, 1.2),
        (3, 4, 1.9), (3, 5, 2.4), (4, 5, 2.0), (5, 6, 1.2), (6, 7, 1.1), (6, 8, 1.6), (7, 8, 1.7));

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void Textbook_MatchesTheKnownMinimumTree(string algorithm)
    {
        var graph = Textbook();

        var tree = Build(algorithm, graph);

        // a-b, b-c (tied with a-h), c-i, c-f, f-g, g-h, c-d, d-e: weight 37 in the book.
        (int, int)[] expected = [(0, 1), (1, 2), (2, 8), (2, 5), (5, 6), (6, 7), (2, 3), (3, 4)];
        var expectedCost = expected.Sum(pair => graph.LinkBetween(Mac(pair.Item1), Mac(pair.Item2))!.Cost);

        Assert.Equal(8, tree.Links.Count);
        Assert.Equal(9, tree.Spanned);
        Assert.Equal(expectedCost, tree.TotalCost, precision: 6);
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void Tree_IsRootedAtTheGateway(string algorithm)
    {
        var tree = Build(algorithm, Textbook());

        Assert.Equal(Mac(0), tree.Root);
        Assert.Equal(0, tree.PathCost[Mac(0)]);
        Assert.Equal(0, tree.Depth[Mac(0)]);

        foreach (var (child, parent) in tree.Parents)
        {
            // Every hop moves one level closer to the gateway and costs a link.
            Assert.Equal(tree.Depth[parent] + 1, tree.Depth[child]);
            Assert.True(tree.PathCost[child] > tree.PathCost[parent]);
        }

        var path = tree.PathFrom(Mac(4));
        Assert.Equal(Mac(4), path[0]);
        Assert.Equal(Mac(0), path[^1]);
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void DownVertex_IsRoutedAround(string algorithm)
    {
        // Square 0-1-2-3-0 plus a diagonal; the cheap route to 2 runs through 1.
        var graph = Weighted(4, (0, 1, 1.1), (1, 2, 1.1), (2, 3, 1.9), (3, 0, 1.2), (0, 2, 3.0));
        Assert.Equal(Mac(1), Build(algorithm, graph).Parents[Mac(2)]);

        graph.Find(Mac(1))!.IsUp = false;
        var healed = Build(algorithm, graph);

        Assert.False(healed.Contains(Mac(1)));
        Assert.Equal(3, healed.Spanned);
        Assert.Equal(Mac(3), healed.Parents[Mac(2)]);
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void PartitionedMesh_SpansOnlyTheGatewaysPiece(string algorithm)
    {
        // 0-1-2 and a separate island 3-4.
        var graph = Weighted(5, (0, 1, 1.2), (1, 2, 1.2), (3, 4, 1.2));

        var tree = Build(algorithm, graph);

        Assert.Equal(3, tree.Spanned);
        Assert.Equal(2, tree.Unspanned);
        Assert.False(tree.Contains(Mac(3)));
        Assert.Empty(tree.PathFrom(Mac(4)));
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void DownGateway_SpansNothing(string algorithm)
    {
        var graph = Weighted(3, (0, 1, 1.2), (1, 2, 1.2));
        graph.Find(Mac(0))!.IsUp = false;

        var tree = Build(algorithm, graph);

        Assert.Equal(0, tree.Spanned);
        Assert.Empty(tree.Links);
    }

    [Fact]
    public void SmallGraphs_MatchBruteForceOverEverySpanningTree()
    {
        var random = new Random(21);

        for (var trial = 0; trial < 60; trial++)
        {
            var graph = RandomWeighted(random, vertices: 6, extraLinks: 5);
            var best = BruteForceMinimum(graph);

            Assert.Equal(best, PrimMst.Build(graph, Mac(0)).TotalCost, precision: 6);
            Assert.Equal(best, KruskalMst.Build(graph, Mac(0)).TotalCost, precision: 6);
        }
    }

    [Fact]
    public void RandomMeshes_PrimAndKruskalAgree()
    {
        var random = new Random(99);

        for (var trial = 0; trial < 200; trial++)
        {
            var vertices = random.Next(20, 400);

            // Side scaled so density stays at about ten neighbours a node.
            var graph = RandomGeometric(vertices, Math.Sqrt(vertices) * 40, seed: trial);
            AssertAgree(graph);
        }
    }

    [Fact]
    public void LargeMesh_PrimAndKruskalAgree()
    {
        var graph = RandomGeometric(2000, Math.Sqrt(2000) * 40, seed: 2026);

        var prim = AssertAgree(graph);

        Assert.Equal(prim.Spanned - 1, prim.Links.Count);
        Assert.True(prim.Spanned > 1500);
    }

    [Fact]
    public void Prim_IsDeterministic()
    {
        var first = PrimMst.Build(RandomGeometric(300, 700, seed: 4), Mac(0));
        var second = PrimMst.Build(RandomGeometric(300, 700, seed: 4), Mac(0));

        Assert.Equal(first.Parents.OrderBy(pair => pair.Key), second.Parents.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void DisjointSet_TracksMergedSets()
    {
        var sets = new DisjointSet<int>();
        foreach (var item in Enumerable.Range(0, 10))
        {
            sets.MakeSet(item);
        }

        Assert.True(sets.Union(1, 2));
        Assert.True(sets.Union(2, 3));
        Assert.False(sets.Union(1, 3));
        Assert.True(sets.Connected(1, 3));
        Assert.False(sets.Connected(1, 4));
        Assert.Equal(8, sets.SetCount);
        Assert.False(sets.MakeSet(5));
    }

    private static SpanningTreeResult Build(string algorithm, MeshGraph graph)
        => algorithm == PrimMst.Name ? PrimMst.Build(graph, Mac(0)) : KruskalMst.Build(graph, Mac(0));

    private static SpanningTreeResult AssertAgree(MeshGraph graph)
    {
        var prim = PrimMst.Build(graph, Mac(0));
        var kruskal = KruskalMst.Build(graph, Mac(0));

        Assert.Equal(prim.Spanned, kruskal.Spanned);
        Assert.Equal(prim.TotalCost, kruskal.TotalCost, precision: 6);
        Assert.Equal(prim.Spanned - 1, prim.Links.Count);
        return prim;
    }

    /// <summary>A connected random graph: a random spanning chain, plus extra random links.</summary>
    private static MeshGraph RandomWeighted(Random random, int vertices, int extraLinks)
    {
        var links = new List<(int, int, double)>();
        var used = new HashSet<(int, int)>();

        for (var i = 1; i < vertices; i++)
        {
            var j = random.Next(0, i);
            used.Add((j, i));
            links.Add((j, i, 1.05 + Math.Round(random.NextDouble() * 3, 1)));
        }

        while (extraLinks > 0)
        {
            var a = random.Next(0, vertices);
            var b = random.Next(0, vertices);
            if (a == b || !used.Add((Math.Min(a, b), Math.Max(a, b))))
            {
                continue;
            }

            links.Add((a, b, 1.05 + Math.Round(random.NextDouble() * 3, 1)));
            extraLinks--;
        }

        return Weighted(vertices, [.. links]);
    }

    /// <summary>Tries every set of V - 1 links and keeps the cheapest one that connects everything.</summary>
    private static double BruteForceMinimum(MeshGraph graph)
    {
        var links = graph.Links.ToList();
        var needed = graph.VertexCount - 1;
        var best = double.MaxValue;

        foreach (var subset in Subsets(links.Count, needed))
        {
            var sets = new DisjointSet<MacKey>();
            foreach (var vertex in graph.Vertices)
            {
                sets.MakeSet(vertex.Mac);
            }

            var cost = 0d;
            foreach (var index in subset)
            {
                sets.Union(links[index].A, links[index].B);
                cost += links[index].Cost;
            }

            if (sets.SetCount == 1)
            {
                best = Math.Min(best, cost);
            }
        }

        return Math.Round(best, 3);
    }

    private static IEnumerable<int[]> Subsets(int count, int size)
    {
        var indices = Enumerable.Range(0, size).ToArray();

        while (true)
        {
            yield return indices;

            var i = size - 1;
            while (i >= 0 && indices[i] == count - size + i)
            {
                i--;
            }

            if (i < 0)
            {
                yield break;
            }

            indices[i]++;
            for (var j = i + 1; j < size; j++)
            {
                indices[j] = indices[j - 1] + 1;
            }
        }
    }
}
