using SmartX.Core.Alerts;
using SmartX.Core.Devices;
using SmartX.Core.Models;
using SmartX.Core.Streaming;

namespace SmartX.Core.Insights;

/// <summary>Whether a suggestion is something to do or something to look at.</summary>
public enum SuggestionKind
{
    /// <summary>A command the operator can run in one click.</summary>
    Action = 0,

    /// <summary>A device or trend the operator should look at.</summary>
    Insight = 1
}

/// <summary>One proactive recommendation.</summary>
/// <param name="Key">Stable identity, used to accept or dismiss it. Two suggestions with one key are the same advice.</param>
/// <param name="Kind">Action or insight.</param>
/// <param name="Title">The advice in a few words.</param>
/// <param name="Reason">Why - the evidence behind it, in plain language.</param>
/// <param name="Source">Which part of the engine produced it.</param>
/// <param name="Score">Ranking score; higher is shown first.</param>
/// <param name="Confidence">For a learned pattern, how often it has held.</param>
/// <param name="Support">For a learned pattern, how many times it has been seen.</param>
/// <param name="Target">The device it concerns.</param>
/// <param name="DesiredState">For an action, the relay state it would set.</param>
public sealed record Suggestion(
    string Key,
    SuggestionKind Kind,
    string Title,
    string Reason,
    string Source,
    double Score,
    double? Confidence,
    int? Support,
    MacKey? Target,
    bool? DesiredState);

/// <summary>Tuning for the advisor.</summary>
public sealed class AdvisorSettings
{
    /// <summary>Fewest observations before a learned pattern is trusted.</summary>
    public int MinSupport { get; set; } = 2;

    /// <summary>Lowest confidence a pattern needs before it is suggested.</summary>
    public double MinConfidence { get; set; } = 0.4d;

    /// <summary>Most suggestions returned at once.</summary>
    public int MaxSuggestions { get; set; } = 6;

    /// <summary>How much recent history a trend is fitted over.</summary>
    public TimeSpan TrendWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How far ahead a projected crossing is worth warning about.</summary>
    public TimeSpan TrendHorizon { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Lowest goodness of fit at which a trend is believed.</summary>
    public double MinRSquared { get; set; } = 0.6d;

    /// <summary>Critical alerts in the recent window before a node is called a recurring fault.</summary>
    public int RecurringFaultThreshold { get; set; } = 3;

    /// <summary>Manual switches in the recent window before an actuator is called flapping.</summary>
    public int FlappingThreshold { get; set; } = 4;
}

/// <summary>The gateway state the advisor reads.</summary>
/// <param name="NowUtc">The current time.</param>
/// <param name="Miner">What has been learned about operator behaviour.</param>
/// <param name="Registry">The live devices.</param>
/// <param name="Alerts">Conditions currently active.</param>
/// <param name="Outages">Nodes currently down.</param>
/// <param name="RecentCriticalCounts">Critical alerts per device in the recent window.</param>
/// <param name="RecentCommandCounts">Manual switches per device in the recent window.</param>
/// <param name="Dismissed">Suggestion keys the operator has dismissed and not yet had back.</param>
/// <param name="Bands">The safety limits trends are projected against.</param>
public sealed record AdvisorContext(
    DateTimeOffset NowUtc,
    SequentialPatternMiner Miner,
    DeviceRegistry Registry,
    ActiveAlertSet Alerts,
    OutageTracker Outages,
    IReadOnlyDictionary<MacKey, int> RecentCriticalCounts,
    IReadOnlyDictionary<MacKey, int> RecentCommandCounts,
    IReadOnlySet<string> Dismissed,
    CriticalBands Bands);

/// <summary>
/// The predictive action engine: turns what the gateway has learned and what it
/// can see into a short ranked list of Suggested Actions and Automated Insights.
/// <para>
/// <b>Three sources of advice.</b>
/// </para>
/// <list type="number">
/// <item><b>Learned habits.</b> Everything that is happening now - each active
/// alert, each node that is down, the operator's own last few searches and
/// commands - is a trigger. For each trigger the pattern miner is asked what
/// usually follows it. A command that usually follows becomes a one-click action;
/// a search or a device that is usually opened next becomes "check this device";
/// an alert that usually follows becomes a warning to watch that device.</item>
/// <item><b>Trends.</b> A least-squares line through each sensor's recent timeline
/// is projected forward. A moisture probe falling steadily towards its floor is
/// reported before it gets there. If the operator has a learned habit for that
/// probe's low alert, the habit is offered early: switch the pump on now, not
/// after the alert.</item>
/// <item><b>Fault history.</b> Nodes that are offline, nodes raising critical
/// alerts again and again, and actuators being switched back and forth.</item>
/// </list>
/// <para>
/// <b>Ranking.</b> A learned suggestion scores
/// <c>confidence × trigger weight × support / (support + 2)</c>. The last factor is
/// a small-sample penalty: a habit seen twice is trusted less than one seen ten
/// times, however confident each looks. Patterns whose lift is below 1, meaning
/// things the operator does constantly whatever is happening, are weighted down.
/// Candidates are deduplicated by key, keeping the strongest, and the top few are
/// chosen with <see cref="TopK"/>. Advice that is already in effect - switching
/// on a pump that is already on - is never offered.
/// </para>
/// </summary>
public static class ActionAdvisor
{
    private const string LearnedSource = "Learned pattern";
    private const string TrendSource = "Trend forecast";
    private const string FaultSource = "Fault history";

    /// <summary>Builds the current ranked suggestions.</summary>
    public static List<Suggestion> Advise(AdvisorContext context, AdvisorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        var candidates = new Dictionary<string, Suggestion>(StringComparer.Ordinal);

        void Offer(Suggestion suggestion)
        {
            if (context.Dismissed.Contains(suggestion.Key))
            {
                return;
            }

            if (!candidates.TryGetValue(suggestion.Key, out var existing) || suggestion.Score > existing.Score)
            {
                candidates[suggestion.Key] = suggestion;
            }
        }

        foreach (var (token, weight) in Triggers(context))
        {
            ExpandLearned(context, settings, token, weight, TriggerPhrase(context, token), Offer);
        }

        AddTelemetryInsights(context, settings, Offer);

        return TopK.Select(candidates.Values, suggestion => suggestion.Score, settings.MaxSuggestions);
    }

    /// <summary>
    /// What is happening now, weighted by how much it should drive advice: an
    /// active alert fully, a down node slightly less, and the operator's own recent
    /// actions less again, fading the longer ago they were.
    /// </summary>
    private static Dictionary<string, double> Triggers(AdvisorContext context)
    {
        var triggers = new Dictionary<string, double>(StringComparer.Ordinal);

        void Weigh(string token, double weight)
        {
            if (weight > triggers.GetValueOrDefault(token))
            {
                triggers[token] = weight;
            }
        }

        foreach (var alert in context.Alerts.Active)
        {
            Weigh(ActivityToken.Alert(alert.Code, alert.Mac), 1d);
        }

        foreach (var mac in context.Outages.Down)
        {
            Weigh(ActivityToken.Disconnect(mac), 0.8d);
        }

        var window = context.Miner.Window.TotalSeconds;
        foreach (var (token, timestamp) in context.Miner.Recent)
        {
            var age = Math.Max(0d, (context.NowUtc - timestamp).TotalSeconds);
            Weigh(token, 0.9d * Math.Max(0d, 1d - (age / window)));
        }

        return triggers;
    }

    private static void ExpandLearned(
        AdvisorContext context,
        AdvisorSettings settings,
        string trigger,
        double weight,
        string triggerPhrase,
        Action<Suggestion> offer)
    {
        if (weight <= 0d)
        {
            return;
        }

        foreach (var rule in context.Miner.RulesFrom(trigger, settings.MinSupport, settings.MinConfidence))
        {
            if (!ActivityToken.TryParse(rule.Consequent, out var next))
            {
                continue;
            }

            var score = rule.Confidence * weight * (rule.Support / (rule.Support + 2d)) * (rule.Lift >= 1d ? 1d : 0.6d);
            var evidence = $"{rule.Support} of {rule.AntecedentCount} times ({rule.Confidence * 100:0}%){Gap(rule.MeanGap)}";

            switch (next.Kind)
            {
                case ActivityKind.Command when next.Mac is { } mac && next.State is { } state
                                               && context.Registry.TryGet(mac, out var actuator)
                                               && actuator.IsActuator
                                               && actuator.EffectiveState != state:
                    offer(new Suggestion(
                        CommandKey(state, mac),
                        SuggestionKind.Action,
                        $"Switch {OnOff(state)} {actuator.DisplayName}",
                        $"{triggerPhrase}, you switched {actuator.DisplayName} {OnOff(state).ToLowerInvariant()} {evidence}.",
                        LearnedSource,
                        score,
                        rule.Confidence,
                        rule.Support,
                        mac,
                        state));
                    break;

                case ActivityKind.Search when next.Text is { } text:
                {
                    var match = BestMatch(context.Registry, text);
                    offer(new Suggestion(
                        match is null ? $"search:{text}" : CheckKey(match.Mac),
                        SuggestionKind.Insight,
                        match is null ? $"Look up “{text}”" : $"Check {match.DisplayName}",
                        $"{triggerPhrase}, you searched for “{text}” {evidence}.",
                        LearnedSource,
                        score * 0.9d,
                        rule.Confidence,
                        rule.Support,
                        match?.Mac,
                        null));
                    break;
                }

                case ActivityKind.Inspect when next.Mac is { } mac && context.Registry.TryGet(mac, out var opened):
                    offer(new Suggestion(
                        CheckKey(mac),
                        SuggestionKind.Insight,
                        $"Check {opened.DisplayName}",
                        $"{triggerPhrase}, you opened {opened.DisplayName} {evidence}.",
                        LearnedSource,
                        score * 0.9d,
                        rule.Confidence,
                        rule.Support,
                        mac,
                        null));
                    break;

                case ActivityKind.Alert or ActivityKind.Disconnect when next.Mac is { } mac
                                                                     && context.Registry.TryGet(mac, out var watched)
                                                                     && !IsAlreadyHappening(context, next):
                    offer(new Suggestion(
                        $"watch:{mac}",
                        SuggestionKind.Insight,
                        $"Watch {watched.DisplayName}",
                        $"{triggerPhrase}, {watched.DisplayName} {Describe(next)} {evidence}.",
                        LearnedSource,
                        score * 0.85d,
                        rule.Confidence,
                        rule.Support,
                        mac,
                        null));
                    break;
            }
        }
    }

    private static void AddTelemetryInsights(AdvisorContext context, AdvisorSettings settings, Action<Suggestion> offer)
    {
        foreach (var device in context.Registry.All)
        {
            if (context.Outages.IsDown(device.Mac))
            {
                var silence = device.LastSeenUtc is { } seen
                    ? $"No packet for {Minutes(context.NowUtc - seen)}"
                    : "It has never reported";

                offer(new Suggestion(
                    $"down:{device.Mac}",
                    SuggestionKind.Insight,
                    $"{device.DisplayName} is offline",
                    $"{silence}, against a heartbeat every {device.HeartbeatIntervalSeconds} s."
                    + (device.IsActuator ? " Commands sent to it will time out until it reconnects." : string.Empty),
                    FaultSource,
                    0.55d,
                    null,
                    null,
                    device.Mac,
                    null));

                continue;
            }

            if (context.RecentCriticalCounts.GetValueOrDefault(device.Mac) is var criticals
                && criticals >= settings.RecurringFaultThreshold)
            {
                offer(new Suggestion(
                    $"recurring:{device.Mac}",
                    SuggestionKind.Insight,
                    $"Recurring fault on {device.DisplayName}",
                    $"{criticals} critical alerts in the last hour. Repeated alerts on one node usually mean a hardware or calibration fault rather than a passing spike.",
                    FaultSource,
                    0.6d + Math.Min(0.3d, criticals * 0.05d),
                    null,
                    null,
                    device.Mac,
                    null));
            }

            if (device.IsActuator
                && context.RecentCommandCounts.GetValueOrDefault(device.Mac) is var switches
                && switches >= settings.FlappingThreshold)
            {
                offer(new Suggestion(
                    $"flap:{device.Mac}",
                    SuggestionKind.Insight,
                    $"{device.DisplayName} is being switched often",
                    $"Switched {switches} times in the last hour. Frequent manual overrides on one relay suggest a schedule or an automatic rule would hold it steadier.",
                    FaultSource,
                    0.4d,
                    null,
                    null,
                    device.Mac,
                    null));
            }

            if (!device.IsActuator)
            {
                AddTrend(context, settings, device, offer);
            }
        }
    }

    private static void AddTrend(AdvisorContext context, AdvisorSettings settings, DeviceState device, Action<Suggestion> offer)
    {
        if (CriticalLine(device, context.Bands) is not { } line)
        {
            return;
        }

        var buckets = device.Timeline.Latest(settings.TrendWindow);
        if (buckets.Count < 6)
        {
            return;
        }

        var points = buckets.Select(bucket => (bucket.StartUtc, bucket.Mean)).ToList();
        if (TrendForecaster.Fit(points) is not { } fit || fit.RSquared < settings.MinRSquared)
        {
            return;
        }

        var heading = line.Falling ? fit.SlopePerMinute < 0d : fit.SlopePerMinute > 0d;
        var alreadyPast = line.Falling ? fit.Current <= line.Threshold : fit.Current >= line.Threshold;

        if (!heading || alreadyPast || fit.TimeUntil(line.Threshold) is not { } eta || eta > settings.TrendHorizon)
        {
            return;
        }

        var urgency = 1d - (eta / settings.TrendHorizon);
        var unit = UnitSuffix(device.Unit);

        offer(new Suggestion(
            $"trend:{device.Mac}",
            SuggestionKind.Insight,
            $"{device.DisplayName} is heading for {line.Threshold:0.#}{unit}",
            $"{(line.Falling ? "Falling" : "Rising")} {Math.Abs(fit.SlopePerMinute):0.##}{unit} a minute over the last " +
            $"{Minutes(fit.Span)} (fit R² {fit.RSquared:0.00}); projected to cross {line.Threshold:0.#}{unit} in about {Minutes(eta)}.",
            TrendSource,
            0.5d + (0.45d * urgency),
            null,
            null,
            device.Mac,
            null));

        // Anticipate the habit: if the operator has a learned response to this
        // device's alert, offer it now, before the alert fires.
        var anticipated = ActivityToken.Alert(line.Code, device.Mac);
        ExpandLearned(
            context,
            settings,
            anticipated,
            0.75d * urgency + 0.15d,
            $"{device.DisplayName} is projected to cross {line.Threshold:0.#}{unit} in about {Minutes(eta)}. When it has before",
            offer);
    }

    private readonly record struct CriticalLineSpec(double Threshold, bool Falling, AlertCode Code);

    private static CriticalLineSpec? CriticalLine(DeviceState device, CriticalBands bands) => device switch
    {
        { Category: SensorCategory.Environmental, Unit: TelemetryUnit.Percent }
            => new CriticalLineSpec(bands.EnvironmentalFloorPercent, true, AlertCode.CriticalLow),
        { Category: SensorCategory.PowerConsumption, Unit: TelemetryUnit.Watt }
            => new CriticalLineSpec(bands.PowerCeilingWatts, false, AlertCode.PowerSpike),
        { Category: SensorCategory.Connectivity, Unit: TelemetryUnit.Dbm }
            => new CriticalLineSpec(bands.LinkFloorDbm, true, AlertCode.LinkLoss),
        _ => null
    };

    private static bool IsAlreadyHappening(AdvisorContext context, ParsedActivity activity) => activity switch
    {
        { Kind: ActivityKind.Alert, Mac: { } mac, Code: { } code } => context.Alerts.IsActive(new AlertKey(mac, code)),
        { Kind: ActivityKind.Disconnect, Mac: { } mac } => context.Outages.IsDown(mac),
        _ => false
    };

    private static DeviceState? BestMatch(DeviceRegistry registry, string text)
    {
        DeviceState? best = null;

        foreach (var device in registry.All)
        {
            if (!DeviceRegistry.MatchesText(device, text))
            {
                continue;
            }

            // An actuator is the likelier target of a search that precedes a command,
            // and a name match beats a path match.
            if (best is null
                || (device.IsActuator && !best.IsActuator)
                || (device.IsActuator == best.IsActuator
                    && string.Compare(device.DisplayName, best.DisplayName, StringComparison.OrdinalIgnoreCase) < 0))
            {
                best = device;
            }
        }

        return best;
    }

    /// <summary>How a trigger reads at the start of a sentence: "After Soil Moisture Probe 01 fell below its safety floor".</summary>
    private static string TriggerPhrase(AdvisorContext context, string token)
    {
        if (!ActivityToken.TryParse(token, out var activity))
        {
            return "Recently";
        }

        var name = activity.Mac is { } mac && context.Registry.TryGet(mac, out var device)
            ? device.DisplayName
            : activity.Mac?.ToString() ?? "a device";

        return activity.Kind switch
        {
            ActivityKind.Alert => $"When {name} {Describe(activity)}",
            ActivityKind.Disconnect => $"When {name} went offline",
            ActivityKind.Search => $"After you searched “{activity.Text}”",
            ActivityKind.Inspect => $"After you opened {name}",
            ActivityKind.Command => $"After you switched {name} {OnOff(activity.State == true).ToLowerInvariant()}",
            _ => "Recently"
        };
    }

    private static string Describe(ParsedActivity activity) => activity switch
    {
        { Kind: ActivityKind.Disconnect } => "went offline",
        { Code: AlertCode.CriticalLow } => "fell below its safety floor",
        { Code: AlertCode.PowerSpike } => "spiked past its power limit",
        { Code: AlertCode.LinkLoss } => "lost its mesh link",
        { Code: AlertCode.DeviceAlarm } => "raised its own alarm",
        _ => "raised an alert"
    };

    /// <summary>The key a command suggestion is accepted by.</summary>
    public static string CommandKey(bool state, MacKey mac) => $"cmd:{(state ? "on" : "off")}:{mac}";

    private static string CheckKey(MacKey mac) => $"check:{mac}";

    private static string OnOff(bool state) => state ? "ON" : "OFF";

    /// <summary>"%" sits against the number; every other unit takes a space - "20%", "2500 W".</summary>
    private static string UnitSuffix(TelemetryUnit unit)
        => SmartX.Core.Telemetry.SensorReading.UnitSymbol(unit) switch
        {
            "" => string.Empty,
            "%" => "%",
            var symbol => $" {symbol}"
        };

    private static string Gap(TimeSpan gap) => gap < TimeSpan.FromSeconds(5) ? string.Empty : $", usually within {Minutes(gap)}";

    private static string Minutes(TimeSpan span) => span.TotalMinutes switch
    {
        < 1d => $"{Math.Max(1, (int)Math.Round(span.TotalSeconds))} s",
        < 90d => $"{(int)Math.Round(span.TotalMinutes)} min",
        _ => $"{span.TotalHours:0.#} h"
    };
}
