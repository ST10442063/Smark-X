namespace SmartX.Core.Insights;

/// <summary>
/// Picks the k highest-scoring items from a larger set.
/// <para>
/// Sorting everything to keep the top six is O(n log n). This keeps a
/// <see cref="PriorityQueue{TElement,TPriority}"/> of at most k items as a
/// min-heap, so its root is always the weakest of the current best. Each new item
/// is compared with that root and replaces it only if it scores higher. That is
/// O(n log k), and with k fixed at a handful of suggestions it is effectively one
/// pass over the candidates.
/// </para>
/// </summary>
public static class TopK
{
    /// <summary>The <paramref name="k"/> highest-scoring items, highest first.</summary>
    public static List<T> Select<T>(IEnumerable<T> items, Func<T, double> score, int k)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(score);

        if (k <= 0)
        {
            return [];
        }

        var heap = new PriorityQueue<T, double>(k + 1);

        foreach (var item in items)
        {
            var value = score(item);

            if (heap.Count < k)
            {
                heap.Enqueue(item, value);
            }
            else if (heap.TryPeek(out _, out var weakest) && value > weakest)
            {
                // Replace the weakest in one heap operation rather than two.
                heap.DequeueEnqueue(item, value);
            }
        }

        var result = new List<T>(heap.Count);
        while (heap.TryDequeue(out var item, out _))
        {
            result.Add(item);
        }

        // The heap yields weakest first.
        result.Reverse();
        return result;
    }
}
