using SmartX.Core.Insights;

namespace SmartX.Core.Tests.Insights;

/// <summary>
/// Covers the behaviour model. The headline case is the one from the brief: an
/// operator who searches for "Pump A" after "Moisture Sensor B" drops.
/// </summary>
public class SequentialPatternMinerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    private const string MoistureLow = "alert:CriticalLow:A4:CF:12:00:00:01";
    private const string SearchPumpA = "search:pump a";
    private const string PumpAOn = "command:on:A4:CF:12:00:00:03";

    /// <summary>
    /// Five incidents an hour apart. In four the operator searched and switched the
    /// pump on within a few minutes; in one they did something else.
    /// </summary>
    private static SequentialPatternMiner TrainedOnFiveIncidents()
    {
        var miner = new SequentialPatternMiner(TimeSpan.FromMinutes(10));

        for (var incident = 0; incident < 5; incident++)
        {
            var at = Start.AddHours(incident);
            miner.Observe(MoistureLow, at);

            if (incident == 2)
            {
                miner.Observe("search:repeater", at.AddMinutes(1));
                continue;
            }

            miner.Observe(SearchPumpA, at.AddMinutes(1));
            miner.Observe(PumpAOn, at.AddMinutes(2));
        }

        return miner;
    }

    [Fact]
    public void AHabit_IsLearnedWithItsConfidence()
    {
        var miner = TrainedOnFiveIncidents();

        var rules = miner.RulesFrom(MoistureLow);
        var toCommand = rules.Single(rule => rule.Consequent == PumpAOn);

        Assert.Equal(4, toCommand.Support);
        Assert.Equal(5, toCommand.AntecedentCount);
        Assert.Equal(0.8d, toCommand.Confidence, precision: 6);
        Assert.Equal(TimeSpan.FromMinutes(2), toCommand.MeanGap);
    }

    [Fact]
    public void Rules_AreOrderedStrongestFirst()
    {
        var rules = TrainedOnFiveIncidents().RulesFrom(MoistureLow, minSupport: 1, minConfidence: 0d);

        // The search and the command are tied at 80%, so either may come first.
        Assert.Contains(rules.Take(2), rule => rule.Consequent == PumpAOn);
        Assert.Equal("search:repeater", rules[^1].Consequent);
    }

    [Fact]
    public void Thresholds_FilterWeakRules()
    {
        var miner = TrainedOnFiveIncidents();

        // The one-off search for the repeater has support 1 and confidence 0.2.
        Assert.DoesNotContain(miner.RulesFrom(MoistureLow, minSupport: 2), rule => rule.Consequent == "search:repeater");
        Assert.Empty(miner.RulesFrom(MoistureLow, minSupport: 1, minConfidence: 0.95d));
    }

    [Fact]
    public void AnEventOutsideTheWindow_IsNotLinked()
    {
        var miner = new SequentialPatternMiner(TimeSpan.FromMinutes(10));
        miner.Observe(MoistureLow, Start);
        miner.Observe(PumpAOn, Start.AddMinutes(11));

        Assert.Empty(miner.RulesFrom(MoistureLow, minSupport: 1, minConfidence: 0d));
        Assert.Equal(1, miner.WindowSize);
    }

    [Fact]
    public void RepeatedFollowers_AreCreditedOncePerOccurrence()
    {
        // One alert, then the operator searches the same thing three times. That is
        // one incident followed by a search, not three - confidence must stay at 1.
        var miner = new SequentialPatternMiner();
        miner.Observe(MoistureLow, Start);
        miner.Observe(SearchPumpA, Start.AddSeconds(10));
        miner.Observe(SearchPumpA, Start.AddSeconds(20));
        miner.Observe(SearchPumpA, Start.AddSeconds(30));

        var rule = Assert.Single(miner.RulesFrom(MoistureLow, minSupport: 1, minConfidence: 0d));
        Assert.Equal(1, rule.Support);
        Assert.Equal(1d, rule.Confidence);
    }

    [Fact]
    public void Lift_RewardsConsequentsThatAreRareOtherwise()
    {
        var miner = TrainedOnFiveIncidents();

        var rule = miner.RulesFrom(MoistureLow).Single(rule => rule.Consequent == PumpAOn);

        // 14 events in total, 4 of them the command: base rate 4/14, confidence 0.8.
        Assert.Equal(14, miner.TotalEvents);
        Assert.Equal(0.8d / (4d / 14d), rule.Lift, precision: 6);
        Assert.True(rule.Lift > 1d);
    }

    [Fact]
    public void Recent_ExposesTheCurrentWindowOldestFirst()
    {
        var miner = new SequentialPatternMiner(TimeSpan.FromMinutes(10));
        miner.Observe("search:old", Start);
        miner.Observe(MoistureLow, Start.AddMinutes(20));
        miner.Observe(SearchPumpA, Start.AddMinutes(21));

        Assert.Equal([MoistureLow, SearchPumpA], miner.Recent.Select(entry => entry.Token));
    }

    [Fact]
    public void AllRules_ListsWhatHasBeenLearned()
    {
        var rules = TrainedOnFiveIncidents().AllRules();

        Assert.Contains(rules, rule => rule.Antecedent == MoistureLow && rule.Consequent == PumpAOn);
        Assert.Contains(rules, rule => rule.Antecedent == SearchPumpA && rule.Consequent == PumpAOn);
    }
}
