using SmartX.Core.Streaming;

namespace SmartX.Core.Tests.Streaming;

/// <summary>
/// Covers the two-lane intake. The property the brief asks for is the bypass: a
/// critical alert queued behind a backlog of routine telemetry must come out first.
/// </summary>
public class DualLaneDispatchQueueTests
{
    private static List<Dispatched<string>> Drain(DualLaneDispatchQueue<string> queue)
    {
        var drained = new List<Dispatched<string>>();
        while (queue.TryDequeue(out var item))
        {
            drained.Add(item);
        }

        return drained;
    }

    [Fact]
    public void TheStandardLane_IsFirstInFirstOut()
    {
        var queue = new DualLaneDispatchQueue<string>();
        foreach (var packet in new[] { "a", "b", "c", "d" })
        {
            queue.Enqueue(packet);
        }

        Assert.Equal(["a", "b", "c", "d"], Drain(queue).Select(item => item.Item));
    }

    [Fact]
    public void ACriticalAlert_BypassesTheBacklog()
    {
        var queue = new DualLaneDispatchQueue<string>();
        for (var i = 0; i < 500; i++)
        {
            queue.Enqueue($"routine {i}");
        }

        var ticket = queue.EnqueuePriority("moisture crash", severity: 0);

        Assert.Equal(DispatchLane.Priority, ticket.Lane);
        Assert.Equal(500, ticket.StandardWaiting);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("moisture crash", first.Item);
        Assert.Equal(DispatchLane.Priority, first.Lane);
        Assert.Equal(500, first.Overtook);
    }

    [Fact]
    public void Overtook_CountsTheRoutinePacketsWaitingWhenTheAlertArrived()
    {
        var queue = new DualLaneDispatchQueue<string>();
        for (var i = 0; i < 10; i++)
        {
            queue.Enqueue($"routine {i}");
        }

        // Four routine packets are processed before the alert arrives, so it only
        // jumps the six still waiting - and the two that arrive after it.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(queue.TryDequeue(out _));
        }

        queue.EnqueuePriority("alert", severity: 0);
        queue.Enqueue("after the alert");
        queue.Enqueue("after the alert, again");

        Assert.True(queue.TryDequeue(out var alert));
        Assert.Equal(6, alert.Overtook);

        var idle = new DualLaneDispatchQueue<string>();
        idle.EnqueuePriority("alert on an idle gateway", 0);
        Assert.True(idle.TryDequeue(out var alone));
        Assert.Equal(0, alone.Overtook);
    }

    [Fact]
    public void MoreSevereAlerts_LeaveFirst()
    {
        var queue = new DualLaneDispatchQueue<string>();
        queue.EnqueuePriority("link loss", severity: 1);
        queue.EnqueuePriority("power spike", severity: 0);
        queue.Enqueue("routine");

        Assert.Equal(["power spike", "link loss", "routine"], Drain(queue).Select(item => item.Item));
    }

    [Fact]
    public void AlertsOfEqualSeverity_LeaveInArrivalOrder()
    {
        // A plain binary heap would not guarantee this.
        var queue = new DualLaneDispatchQueue<string>();
        var arrivals = Enumerable.Range(0, 200).Select(i => $"alert {i}").ToList();

        foreach (var alert in arrivals)
        {
            queue.EnqueuePriority(alert, severity: 0);
        }

        Assert.Equal(arrivals, Drain(queue).Select(item => item.Item));
    }

    [Fact]
    public void AFullStandardLane_ShedsRoutineTrafficButNeverAlerts()
    {
        var queue = new DualLaneDispatchQueue<string>(standardCapacity: 3);
        queue.Enqueue("1");
        queue.Enqueue("2");
        queue.Enqueue("3");

        var shed = queue.Enqueue("4");
        var alert = queue.EnqueuePriority("critical", 0);

        Assert.False(shed.Accepted);
        Assert.True(alert.Accepted);
        Assert.Equal(4, queue.Count);
        Assert.Equal(1, queue.Snapshot().StandardShed);
    }

    [Fact]
    public void TheSnapshot_RecordsTheBypass()
    {
        var queue = new DualLaneDispatchQueue<string>();
        queue.Enqueue("a");
        queue.Enqueue("b");
        queue.EnqueuePriority("alert", 0);
        Drain(queue);

        var metrics = queue.Snapshot();

        Assert.Equal(2, metrics.StandardProcessed);
        Assert.Equal(1, metrics.PriorityProcessed);
        Assert.Equal(1, metrics.Bypasses);
        Assert.Equal(2, metrics.PacketsOvertaken);
        Assert.Equal(2, metrics.LastPriorityOvertook);
        Assert.Equal(2, metrics.StandardHighWater);
        Assert.Equal(0, metrics.StandardDepth);
    }

    [Fact]
    public async Task DequeueAsync_WaitsForAnItem()
    {
        var queue = new DualLaneDispatchQueue<string>();
        var pending = queue.DequeueAsync().AsTask();

        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        queue.Enqueue("arrived");
        var item = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("arrived", item.Item);
    }

    [Fact]
    public async Task DequeueAsync_StopsWhenCancelled()
    {
        var queue = new DualLaneDispatchQueue<string>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queue.DequeueAsync(cancel.Token));
    }

    [Fact]
    public async Task ConcurrentProducers_LoseNothing()
    {
        const int producers = 8;
        const int each = 2_000;

        // Sized to hold everything: this test is about losing nothing under
        // contention, not about shedding, which has a test of its own.
        var queue = new DualLaneDispatchQueue<int>(standardCapacity: producers * each);

        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < each; i++)
            {
                if (i % 100 == 0)
                {
                    queue.EnqueuePriority((p * each) + i, severity: 0);
                }
                else
                {
                    queue.Enqueue((p * each) + i);
                }
            }
        })));

        var seen = new HashSet<int>();
        for (var i = 0; i < producers * each; i++)
        {
            var item = await queue.DequeueAsync();
            Assert.True(seen.Add(item.Item));
        }

        Assert.Equal(producers * each, seen.Count);
        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(0, queue.Snapshot().StandardShed);
    }
}
