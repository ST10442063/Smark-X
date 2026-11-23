using SmartX.Core.Mesh;

namespace SmartX.Core.Tests.Mesh;

/// <summary>Covers the indexed binary heap Prim's algorithm runs on, in particular decrease-key.</summary>
public class IndexedMinHeapTests
{
    [Fact]
    public void Pop_ReturnsKeysInPriorityOrder()
    {
        var random = new Random(3);
        var heap = new IndexedMinHeap<int>();
        var priorities = Enumerable.Range(0, 2000).ToDictionary(key => key, _ => random.NextDouble() * 100);

        foreach (var (key, priority) in priorities)
        {
            heap.Push(key, priority);
        }

        Assert.True(heap.IsValid());

        var popped = new List<double>();
        while (heap.Count > 0)
        {
            popped.Add(heap.Pop().Priority);
        }

        // Heap sort: popping everything yields the priorities in order.
        Assert.Equal(priorities.Values.Order(), popped);
    }

    [Fact]
    public void TryDecrease_MovesAKeyUpInPlace()
    {
        var heap = new IndexedMinHeap<string>();
        heap.Push("pump", 5);
        heap.Push("probe", 3);
        heap.Push("relay", 8);

        Assert.True(heap.TryDecrease("relay", 1));

        Assert.Equal(3, heap.Count);
        Assert.Equal(1, heap.PriorityOf("relay"));
        Assert.Equal("relay", heap.Pop().Key);
        Assert.True(heap.IsValid());
    }

    [Fact]
    public void TryDecrease_HigherPriority_IsIgnored()
    {
        var heap = new IndexedMinHeap<string>();
        heap.Push("probe", 3);

        Assert.False(heap.TryDecrease("probe", 9));
        Assert.Equal(3, heap.PriorityOf("probe"));
    }

    [Fact]
    public void TryDecrease_UnknownKey_PushesIt()
    {
        var heap = new IndexedMinHeap<string>();

        Assert.True(heap.TryDecrease("probe", 4));
        Assert.True(heap.Contains("probe"));
    }

    [Fact]
    public void Push_Duplicate_Throws()
    {
        var heap = new IndexedMinHeap<int>();
        heap.Push(1, 1);

        Assert.Throws<InvalidOperationException>(() => heap.Push(1, 0.5));
    }

    [Fact]
    public void EqualPriorities_PopInKeyOrder()
    {
        var heap = new IndexedMinHeap<int>();
        foreach (var key in new[] { 7, 2, 9, 4 })
        {
            heap.Push(key, 1.0);
        }

        Assert.Equal([2, 4, 7, 9], Enumerable.Range(0, 4).Select(_ => heap.Pop().Key));
    }

    [Fact]
    public void RandomPushDecreasePop_KeepsTheHeapValid()
    {
        var random = new Random(8);
        var heap = new IndexedMinHeap<int>();
        var reference = new Dictionary<int, double>();

        for (var step = 0; step < 5000; step++)
        {
            var key = random.Next(0, 400);
            var roll = random.Next(0, 3);

            if (roll == 0 && heap.Count > 0)
            {
                var (popped, priority) = heap.Pop();
                Assert.Equal(reference.Values.Min(), priority);
                reference.Remove(popped);
            }
            else
            {
                var priority = random.NextDouble() * 50;
                if (heap.TryDecrease(key, priority))
                {
                    reference[key] = priority;
                }
            }

            if (step % 100 == 0)
            {
                Assert.True(heap.IsValid());
            }
        }

        Assert.Equal(reference.Count, heap.Count);
    }

    [Fact]
    public void Pop_Empty_Throws()
    {
        var heap = new IndexedMinHeap<int>();

        Assert.False(heap.TryPeek(out _, out _));
        Assert.Throws<InvalidOperationException>(() => heap.Pop());
    }
}
