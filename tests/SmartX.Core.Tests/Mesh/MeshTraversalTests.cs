using SmartX.Core.Devices;
using SmartX.Core.Mesh;
using static SmartX.Core.Tests.Mesh.MeshTestGraphs;

namespace SmartX.Core.Tests.Mesh;

/// <summary>
/// Covers breadth-first reachability, the depth-first downstream walk and
/// articulation-point detection, on small graphs whose answers can be checked by eye.
/// </summary>
public class MeshTraversalTests
{
    // 0 - 1 - 2 - 3        A chain off the gateway, with a spur 1 - 4 - 5.
    //      \
    //       4 - 5
    private static MeshGraph Branching() => Weighted(6,
        (0, 1, 1.2), (1, 2, 1.2), (2, 3, 1.2), (1, 4, 1.2), (4, 5, 1.2));

    [Fact]
    public void BreadthFirst_CountsFewestHops()
    {
        var reach = MeshTraversal.BreadthFirst(Branching(), Mac(0));

        Assert.Equal(0, reach.Hops[Mac(0)]);
        Assert.Equal(1, reach.Hops[Mac(1)]);
        Assert.Equal(2, reach.Hops[Mac(2)]);
        Assert.Equal(3, reach.Hops[Mac(3)]);
        Assert.Equal(3, reach.Hops[Mac(5)]);
        Assert.Equal(4, reach.Layers.Count);
        Assert.Equal([Mac(2), Mac(4)], reach.Layers[2]);
        Assert.Empty(reach.Stranded);
    }

    [Fact]
    public void BreadthFirst_PrefersTheShortcut()
    {
        // A ring 0-1-2-3-4-0: node 3 is two hops round one way, three the other.
        var graph = Weighted(5, (0, 1, 1.2), (1, 2, 1.2), (2, 3, 1.2), (3, 4, 1.2), (4, 0, 1.2));

        var reach = MeshTraversal.BreadthFirst(graph, Mac(0));

        Assert.Equal(2, reach.Hops[Mac(3)]);
        Assert.Equal([Mac(3), Mac(4), Mac(0)], reach.PathFrom(Mac(3)));
    }

    [Fact]
    public void BreadthFirst_DownRelay_StrandsItsSubtree()
    {
        var graph = Branching();
        graph.Find(Mac(4))!.IsUp = false;

        var reach = MeshTraversal.BreadthFirst(graph, Mac(0));

        Assert.Equal(new HashSet<MacKey> { Mac(4) }, reach.Down);
        Assert.Equal(new HashSet<MacKey> { Mac(5) }, reach.Stranded);
        Assert.False(reach.Reached(Mac(5)));
        Assert.Empty(reach.PathFrom(Mac(5)));
    }

    [Fact]
    public void BreadthFirst_DownSource_ReachesNothing()
    {
        var graph = Branching();
        graph.Find(Mac(0))!.IsUp = false;

        var reach = MeshTraversal.BreadthFirst(graph, Mac(0));

        Assert.Empty(reach.Hops);
        Assert.Equal(5, reach.Stranded.Count);
    }

    [Fact]
    public void BreadthFirst_ExaminesEachLinkAtMostTwice()
    {
        var graph = RandomGeometric(300, 400, seed: 11);

        var reach = MeshTraversal.BreadthFirst(graph, Mac(0));

        Assert.True(reach.LinksExamined <= 2 * graph.LinkCount);
    }

    [Fact]
    public void Downstream_ListsTheWholeSubtreeWithDepths()
    {
        var reach = MeshTraversal.BreadthFirst(Branching(), Mac(0));

        var below = MeshTraversal.Downstream(reach.Parents, Mac(1));

        Assert.Equal(
            [(Mac(2), 1), (Mac(3), 2), (Mac(4), 1), (Mac(5), 2)],
            below);
        Assert.Empty(MeshTraversal.Downstream(reach.Parents, Mac(3)));
    }

    [Fact]
    public void Downstream_DeepChain_DoesNotOverflow()
    {
        // 20,000 hops: recursion would be at the mercy of the stack size; the
        // explicit stack is not.
        var parents = Enumerable.Range(1, 20_000).ToDictionary(i => Mac(i), i => Mac(i - 1));

        var below = MeshTraversal.Downstream(parents, Mac(0));

        Assert.Equal(20_000, below.Count);
        Assert.Equal(20_000, below[^1].Depth);
    }

    [Fact]
    public void ArticulationPoints_InAChain_AreTheInnerNodes()
    {
        var points = MeshTraversal.ArticulationPoints(Branching());

        // 1 joins the gateway to everything; 2 holds 3 on; 4 holds 5 on.
        Assert.Equal(new HashSet<MacKey> { Mac(1), Mac(2), Mac(4) }, points);
    }

    [Fact]
    public void ArticulationPoints_InARing_AreNone()
    {
        var ring = Weighted(5, (0, 1, 1.2), (1, 2, 1.2), (2, 3, 1.2), (3, 4, 1.2), (4, 0, 1.2));

        Assert.Empty(MeshTraversal.ArticulationPoints(ring));
    }

    [Fact]
    public void ArticulationPoints_TwoTrianglesJoinedAtAVertex()
    {
        // 0-1-2 and 2-3-4 share vertex 2, the classic bowtie.
        var bowtie = Weighted(5, (0, 1, 1.2), (1, 2, 1.2), (2, 0, 1.2), (2, 3, 1.2), (3, 4, 1.2), (4, 2, 1.2));

        Assert.Equal(new HashSet<MacKey> { Mac(2) }, MeshTraversal.ArticulationPoints(bowtie));
    }

    [Fact]
    public void ArticulationPoints_RootWithTwoBranches_IsOne()
    {
        // The DFS root special case: the gateway is a cut vertex here.
        var star = Weighted(3, (0, 1, 1.2), (0, 2, 1.2));

        Assert.Equal(new HashSet<MacKey> { Mac(0) }, MeshTraversal.ArticulationPoints(star));
    }

    [Fact]
    public void ArticulationPoints_MatchRemovingEachVertexInTurn()
    {
        // The definition, checked by brute force on a realistic random mesh.
        var graph = RandomGeometric(120, 220, seed: 5);
        var expected = new HashSet<MacKey>();
        var baseline = MeshTraversal.Components(graph).Count;

        foreach (var vertex in graph.Vertices.ToList())
        {
            var without = MeshTraversal.Components(graph, mac => mac != vertex.Mac).Count;
            if (without > baseline)
            {
                expected.Add(vertex.Mac);
            }
        }

        Assert.Equal(expected, MeshTraversal.ArticulationPoints(graph));
    }

    [Fact]
    public void Components_SplitWhenTheBridgeFails()
    {
        var graph = Branching();

        Assert.Single(MeshTraversal.Components(graph));

        graph.Find(Mac(1))!.IsUp = false;
        var pieces = MeshTraversal.Components(graph);

        Assert.Equal(3, pieces.Count);
        Assert.Equal([Mac(2), Mac(3)], pieces[0]);
    }
}
