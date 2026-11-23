using SmartX.Core.Alerts;
using SmartX.Core.Devices;
using SmartX.Core.Insights;
using SmartX.Core.Streaming;

namespace SmartX.Core.Tests.Insights;

/// <summary>
/// Covers the predictive engine end to end: learned habits become one-click
/// actions, trends become early warnings, and advice already in effect or
/// dismissed is never offered.
/// </summary>
public class ActionAdvisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public Harness()
        {
            Registry = TestFleet.Registry(out var fleet);
            Fleet = fleet;
        }

        public DeviceRegistry Registry { get; }

        public TestFleet.Fleet Fleet { get; }

        public SequentialPatternMiner Miner { get; } = new(TimeSpan.FromMinutes(10));

        public ActiveAlertSet Alerts { get; } = new();

        public OutageTracker Outages { get; } = new();

        public Dictionary<MacKey, int> Criticals { get; } = [];

        public Dictionary<MacKey, int> Commands { get; } = [];

        public HashSet<string> Dismissed { get; } = [];

        /// <summary>Five past incidents where the operator switched Pump A on four times after the probe went low.</summary>
        public Harness WithLearnedHabit()
        {
            var low = ActivityToken.Alert(AlertCode.CriticalLow, Fleet.Probe.Mac);
            var search = ActivityToken.Search("pump a")!;
            var on = ActivityToken.Command(true, Fleet.PumpA.Mac);

            for (var incident = 0; incident < 5; incident++)
            {
                var at = Now.AddDays(-1).AddHours(incident);
                Miner.Observe(low, at);

                if (incident != 2)
                {
                    Miner.Observe(search, at.AddMinutes(1));
                    Miner.Observe(on, at.AddMinutes(2));
                }
            }

            return this;
        }

        public List<Suggestion> Advise(AdvisorSettings? settings = null)
            => ActionAdvisor.Advise(
                new AdvisorContext(Now, Miner, Registry, Alerts, Outages, Criticals, Commands, Dismissed, new CriticalBands()),
                settings ?? new AdvisorSettings());
    }

    [Fact]
    public void WhenTheTriggerRecurs_TheLearnedCommandIsSuggested()
    {
        var harness = new Harness().WithLearnedHabit();
        harness.Alerts.TryRaise(new AlertKey(harness.Fleet.Probe.Mac, AlertCode.CriticalLow));

        var action = harness.Advise().First(suggestion => suggestion.Kind == SuggestionKind.Action);

        Assert.Equal(harness.Fleet.PumpA.Mac, action.Target);
        Assert.True(action.DesiredState);
        Assert.Equal("Switch ON Water Pump A", action.Title);
        Assert.Equal(0.8d, action.Confidence!.Value, precision: 6);
        Assert.Contains("4 of 5 times", action.Reason);
        Assert.Contains("Soil Moisture Probe B", action.Reason);
    }

    [Fact]
    public void TheLearnedSearch_BecomesAnInsightAboutTheDeviceItFinds()
    {
        var harness = new Harness().WithLearnedHabit();
        harness.Alerts.TryRaise(new AlertKey(harness.Fleet.Probe.Mac, AlertCode.CriticalLow));

        var insight = harness.Advise().Single(suggestion => suggestion.Kind == SuggestionKind.Insight
                                                            && suggestion.Source == "Learned pattern");

        Assert.Equal("Check Water Pump A", insight.Title);
        Assert.Contains("“pump a”", insight.Reason);
    }

    [Fact]
    public void ACommandAlreadyInEffect_IsNotSuggested()
    {
        var harness = new Harness().WithLearnedHabit();
        harness.Alerts.TryRaise(new AlertKey(harness.Fleet.Probe.Mac, AlertCode.CriticalLow));
        harness.Fleet.PumpA.DesiredState = true;

        Assert.DoesNotContain(harness.Advise(), suggestion => suggestion.Kind == SuggestionKind.Action);
    }

    [Fact]
    public void ADismissedSuggestion_StaysDismissed()
    {
        var harness = new Harness().WithLearnedHabit();
        harness.Alerts.TryRaise(new AlertKey(harness.Fleet.Probe.Mac, AlertCode.CriticalLow));
        harness.Dismissed.Add(ActionAdvisor.CommandKey(true, harness.Fleet.PumpA.Mac));

        Assert.DoesNotContain(harness.Advise(), suggestion => suggestion.Kind == SuggestionKind.Action);
    }

    [Fact]
    public void WithNothingHappening_NoHabitIsSuggested()
    {
        var harness = new Harness().WithLearnedHabit();

        Assert.DoesNotContain(harness.Advise(), suggestion => suggestion.Source == "Learned pattern");
    }

    [Fact]
    public void AFallingProbe_IsFlaggedBeforeItCrossesItsFloor_AndTheHabitOfferedEarly()
    {
        var harness = new Harness().WithLearnedHabit();

        // 46% falling 0.8% a minute: about 26 minutes above the 20% floor.
        for (var minute = 0; minute < 20; minute++)
        {
            harness.Fleet.Probe.Observe(46d - (0.8d * minute), Now.AddMinutes(minute - 19));
        }

        var advice = harness.Advise();

        var trend = advice.Single(suggestion => suggestion.Source == "Trend forecast");
        Assert.Equal(harness.Fleet.Probe.Mac, trend.Target);
        Assert.Contains("projected to cross 20%", trend.Reason);

        var early = advice.Single(suggestion => suggestion.Kind == SuggestionKind.Action);
        Assert.Equal(harness.Fleet.PumpA.Mac, early.Target);
        Assert.Contains("projected to cross", early.Reason);
    }

    [Fact]
    public void AnOfflineNode_IsReported()
    {
        var harness = new Harness();
        harness.Outages.Reconcile([harness.Fleet.PumpB.Mac]);

        var insight = Assert.Single(harness.Advise());

        Assert.Equal("Water Pump B is offline", insight.Title);
        Assert.Contains("time out", insight.Reason);
    }

    [Fact]
    public void RepeatedCriticalAlerts_AreCalledARecurringFault()
    {
        var harness = new Harness();
        harness.Criticals[harness.Fleet.Meter.Mac] = 5;

        var insight = Assert.Single(harness.Advise());

        Assert.StartsWith("Recurring fault", insight.Title);
    }

    [Fact]
    public void TheListIsCappedAndRankedHighestFirst()
    {
        var harness = new Harness();
        foreach (var device in harness.Fleet.All)
        {
            harness.Outages.Reconcile(harness.Fleet.All.Select(each => each.Mac));
            harness.Criticals[device.Mac] = 9;
        }

        var advice = harness.Advise(new AdvisorSettings { MaxSuggestions = 3 });

        Assert.Equal(3, advice.Count);
        Assert.Equal(advice.OrderByDescending(suggestion => suggestion.Score), advice);
    }
}
