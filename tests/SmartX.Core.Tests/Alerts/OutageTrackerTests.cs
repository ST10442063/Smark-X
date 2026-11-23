using SmartX.Core.Alerts;
using SmartX.Core.Devices;

namespace SmartX.Core.Tests.Alerts;

/// <summary>
/// Covers disconnect tracking by set difference. A node that stays down must be
/// reported on the first sweep only, however many sweeps follow.
/// </summary>
public class OutageTrackerTests
{
    private static readonly MacKey A = MacKey.Parse("A4:CF:12:00:00:0A");
    private static readonly MacKey B = MacKey.Parse("A4:CF:12:00:00:0B");
    private static readonly MacKey C = MacKey.Parse("A4:CF:12:00:00:0C");

    [Fact]
    public void TheFirstSweep_ReportsEverySilentNodeAsNewlyDown()
    {
        var tracker = new OutageTracker();

        var sweep = tracker.Reconcile([A, B]);

        Assert.Equal(new HashSet<MacKey> { A, B }, sweep.NewlyDown);
        Assert.Empty(sweep.Recovered);
        Assert.Equal(0, sweep.StillDown);
    }

    [Fact]
    public void ANodeThatStaysDown_IsNotReportedAgain()
    {
        var tracker = new OutageTracker();
        tracker.Reconcile([A]);

        for (var sweep = 0; sweep < 100; sweep++)
        {
            Assert.Empty(tracker.Reconcile([A]).NewlyDown);
        }

        Assert.Equal(1, tracker.OutagesDetected);
        Assert.Equal(100, tracker.DuplicatesSuppressed);
    }

    [Fact]
    public void ASweep_SeparatesNewRecoveredAndContinuingOutages()
    {
        var tracker = new OutageTracker();
        tracker.Reconcile([A, B]);

        var sweep = tracker.Reconcile([B, C]);

        Assert.Equal([C], sweep.NewlyDown);
        Assert.Equal([A], sweep.Recovered);
        Assert.Equal(1, sweep.StillDown);
        Assert.Equal(new HashSet<MacKey> { B, C }, tracker.Down);
    }

    [Fact]
    public void ANodeThatRecoversAndDropsAgain_IsANewOutage()
    {
        var tracker = new OutageTracker();
        tracker.Reconcile([A]);
        tracker.Reconcile([]);

        Assert.Equal([A], tracker.Reconcile([A]).NewlyDown);
        Assert.Equal(2, tracker.OutagesDetected);
    }

    [Fact]
    public void APacketFromADownNode_RecoversItImmediately()
    {
        var tracker = new OutageTracker();
        tracker.Reconcile([A]);

        Assert.True(tracker.MarkRecovered(A));
        Assert.False(tracker.IsDown(A));
        Assert.False(tracker.MarkRecovered(A));
    }

    [Fact]
    public void DuplicatesInTheSweepInput_AreHarmless()
    {
        var tracker = new OutageTracker();

        var sweep = tracker.Reconcile([A, A, A]);

        Assert.Single(sweep.NewlyDown);
        Assert.Equal(1, tracker.Count);
    }
}
