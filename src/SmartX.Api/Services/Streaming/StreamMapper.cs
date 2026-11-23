using SmartX.Api.Models;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Insights;
using SmartX.Contracts.Stream;
using SmartX.Core.Alerts;
using SmartX.Core.Commands;
using SmartX.Core.Devices;
using SmartX.Core.Insights;
using SmartX.Core.Models;
using SmartX.Core.Streaming;
using SmartX.Core.Telemetry;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// Turns the command stream's internal structures into wire contracts. Every
/// method runs under <see cref="CommandStreamStore"/>'s lock, so the state it reads
/// cannot change halfway through a mapping.
/// </summary>
public static class StreamMapper
{
    /// <summary>Alert states, in the order the grid shows them.</summary>
    public static readonly string[] AlertStates = ["Critical", "Disconnected", "Warning", "Clear"];

    /// <summary>A device's live state for the grid.</summary>
    public static DeviceLiveResponse ToLive(StreamState state, DeviceState device, DateTimeOffset now)
    {
        var alerts = state.Alerts.For(device.Mac);
        var flag = state.Suggestions.FirstOrDefault(suggestion => suggestion.Target == device.Mac);

        return new DeviceLiveResponse
        {
            MacAddress = device.Mac.ToString(),
            SensorId = device.SensorId,
            DisplayName = device.DisplayName,
            Category = device.Category.ToString(),
            QualifiedPath = device.QualifiedPath,
            Zone = device.Zone,
            Unit = SensorReading.UnitSymbol(device.Unit),
            Status = device.EvaluateStatus(now).ToString(),
            LatestValue = device.LatestValue,
            LatestReading = device.LatestValue is { } value ? StreamFormat.Reading(device, value) : "no signal",
            SecondsSinceLastSeen = device.LastSeenUtc is { } seen ? Math.Round((now - seen).TotalSeconds, 1) : null,
            IsActuator = device.IsActuator,
            DesiredState = device.DesiredState,
            ReportedState = device.ReportedState,
            AwaitingConfirmation = device.AwaitingConfirmation,
            PendingCommands = device.Downlink.Count(command => command.Status == CommandStatus.Pending),
            Alerts = [.. alerts.Select(key => key.Code.ToString())],
            AlertState = AlertStateOf(state, device.Mac, alerts),
            Recent = [.. device.Timeline.Latest(TimeSpan.FromMinutes(30)).Select(bucket => Math.Round(bucket.Mean, 2))],
            Flagged = flag is not null,
            FlagReason = flag?.Title
        };
    }

    /// <summary>"Critical", "Disconnected", "Warning" or "Clear".</summary>
    public static string AlertStateOf(StreamState state, MacKey mac, IReadOnlyCollection<AlertKey>? alerts = null)
    {
        alerts ??= state.Alerts.For(mac);

        if (alerts.Any(key => key.Severity == AlertSeverity.Critical))
        {
            return "Critical";
        }

        if (state.Outages.IsDown(mac))
        {
            return "Disconnected";
        }

        return alerts.Count > 0 ? "Warning" : "Clear";
    }

    /// <summary>
    /// Runs a device filter. The category and alert state each narrow to a set, the
    /// sets are intersected, and only the survivors are text-matched.
    /// </summary>
    public static DeviceFilterResponse Filter(
        StreamState state,
        string? category,
        string? alertState,
        string? text,
        DateTimeOffset now)
    {
        SensorCategory? parsedCategory = Enum.TryParse<SensorCategory>(category, ignoreCase: true, out var c) ? c : null;
        var restrict = AlertStateSet(state, alertState, out var alertLabel);

        var devices = state.Devices.Query(new DeviceQuery(parsedCategory, restrict, text), out var cost);

        var steps = new List<string>();
        if (parsedCategory is { } chosen)
        {
            steps.Add($"{chosen} set ({state.Devices.InCategory(chosen).Count})");
        }

        if (restrict is not null)
        {
            steps.Add($"{alertLabel} set ({restrict.Count})");
        }

        var strategy = steps.Count switch
        {
            0 => $"no set filter - all {cost.FleetSize} devices",
            1 => steps[0],
            _ => string.Join(" ∩ ", steps)
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            strategy += $", then text match on {cost.CandidatesExamined}";
        }

        return new DeviceFilterResponse
        {
            Devices = Order(state, devices.Select(device => ToLive(state, device, now))),
            FleetSize = cost.FleetSize,
            CandidatesExamined = cost.CandidatesExamined,
            Strategy = strategy,
            CategoryCounts = state.Devices.CategoryCounts().ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
            AlertCounts = AlertCounts(state)
        };
    }

    /// <summary>
    /// Pins the devices the engine is pointing at, then the worst alert state, then
    /// alphabetical - so the device worth looking at is at the top before anyone searches.
    /// </summary>
    public static List<DeviceLiveResponse> Order(StreamState state, IEnumerable<DeviceLiveResponse> devices)
        => [.. devices
            .OrderByDescending(device => device.Flagged)
            .ThenBy(device => Array.IndexOf(AlertStates, device.AlertState))
            .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The set of devices in an alert state, or null for no restriction.</summary>
    private static HashSet<MacKey>? AlertStateSet(StreamState state, string? alertState, out string label)
    {
        label = alertState ?? string.Empty;

        switch (alertState?.Trim().ToLowerInvariant())
        {
            case "critical":
                label = "critical-alert";
                return state.Alerts.DevicesAt(AlertSeverity.Critical);

            case "warning":
                label = "warning-alert";
                var warnings = state.Alerts.DevicesAt(AlertSeverity.High);
                warnings.ExceptWith(state.Alerts.DevicesAt(AlertSeverity.Critical));
                return warnings;

            case "disconnected":
                label = "disconnected";
                return [.. state.Outages.Down];

            case "alerting":
                label = "any-alert";
                var any = state.Alerts.DevicesWithAny();
                any.UnionWith(state.Outages.Down);
                return any;

            case "clear":
                label = "clear";
                var clear = new HashSet<MacKey>(state.Devices.Keys);
                clear.ExceptWith(state.Alerts.DevicesWithAny());
                clear.ExceptWith(state.Outages.Down);
                return clear;

            default:
                return null;
        }
    }

    private static Dictionary<string, int> AlertCounts(StreamState state)
    {
        var counts = AlertStates.ToDictionary(name => name, _ => 0);
        foreach (var device in state.Devices.All)
        {
            counts[AlertStateOf(state, device.Mac)]++;
        }

        return counts;
    }

    /// <summary>A device's timeline for the graph.</summary>
    public static TimelineResponse Timeline(DeviceState device, TimeSpan window) => new()
    {
        MacAddress = device.Mac.ToString(),
        DisplayName = device.DisplayName,
        Unit = SensorReading.UnitSymbol(device.Unit),
        ResolutionSeconds = (int)device.Timeline.Resolution.TotalSeconds,
        BucketsHeld = device.Timeline.Count,
        LateArrivals = device.Timeline.LateArrivals,
        Points = [.. device.Timeline.Latest(window).Select(bucket => new TimelinePointResponse
        {
            StartUtc = bucket.StartUtc,
            Minimum = Math.Round(bucket.Minimum, 3),
            Maximum = Math.Round(bucket.Maximum, 3),
            Mean = Math.Round(bucket.Mean, 3),
            Count = bucket.Count,
            Critical = bucket.Critical
        })]
    };

    /// <summary>One history entry.</summary>
    public static StreamEventResponse Event(StreamState state, StreamEvent entry) => new()
    {
        Sequence = entry.Sequence,
        TimestampUtc = entry.TimestampUtc,
        Kind = entry.Kind.ToString(),
        Level = entry.Level.ToString(),
        MacAddress = entry.Mac,
        DisplayName = entry.Mac is not null && MacKey.TryParse(entry.Mac, out var mac) ? state.NameOf(mac) : null,
        Title = entry.Title,
        Detail = entry.Detail
    };

    /// <summary>Queue depths and counters, with the stream's own dedup figures.</summary>
    public static QueueStatsResponse QueueStats(DispatchMetrics metrics, StreamState state, DateTimeOffset now, int budgetMs) => new()
    {
        StandardDepth = metrics.StandardDepth,
        PriorityDepth = metrics.PriorityDepth,
        StandardCapacity = metrics.StandardCapacity,
        StandardHighWater = metrics.StandardHighWater,
        StandardProcessed = metrics.StandardProcessed,
        PriorityProcessed = metrics.PriorityProcessed,
        StandardShed = metrics.StandardShed,
        Bypasses = metrics.Bypasses,
        PacketsOvertaken = metrics.PacketsOvertaken,
        StandardMeanWaitMs = Math.Round(metrics.StandardMeanWaitMs, 1),
        PriorityMeanWaitMs = Math.Round(metrics.PriorityMeanWaitMs, 1),
        LastPriorityWaitMs = metrics.LastPriorityWaitMs is { } wait ? Math.Round(wait, 2) : null,
        LastPriorityOvertook = metrics.LastPriorityOvertook,
        ThroughputPerSecond = Math.Round(CommandStreamStore.Throughput(state, now), 1),
        ProcessingBudgetMs = budgetMs,
        SecondsSinceLastUplink = state.LastUplinkUtc is { } last ? Math.Round((now - last).TotalSeconds, 1) : null,
        AlertsActive = state.Alerts.Count,
        AlertDuplicatesSuppressed = state.Alerts.DuplicatesSuppressed,
        NodesDown = state.Outages.Count,
        OutageDuplicatesSuppressed = state.Outages.DuplicatesSuppressed
    };

    /// <summary>Both command stacks, top first.</summary>
    public static CommandHistoryResponse History(StreamState state) => new()
    {
        UndoStack = [.. state.History.UndoStack.Select(record => Record(state, record))],
        RedoStack = [.. state.History.RedoStack.Select(record => Record(state, record))],
        CanUndo = state.History.CanUndo,
        CanRedo = state.History.CanRedo,
        MaxDepth = state.History.MaxDepth
    };

    /// <summary>One command record, with where each target's latest command has got to.</summary>
    public static CommandRecordResponse Record(StreamState state, CommandRecord record)
    {
        // The newest command per target that belongs to this record - after an undo
        // that is the compensating command, which is the one an operator cares about.
        var latest = new Dictionary<MacKey, DeviceCommand>();
        foreach (var command in state.Commands.Values)
        {
            if (command.RecordId == record.Id
                && (!latest.TryGetValue(command.Target, out var seen) || command.IssuedUtc >= seen.IssuedUtc))
            {
                latest[command.Target] = command;
            }
        }

        return new CommandRecordResponse
        {
            Id = record.Id,
            Number = record.Number,
            Label = record.Label,
            Action = record.Action.ToString(),
            Origin = record.Origin.ToString(),
            IssuedUtc = record.IssuedUtc,
            Targets = [.. record.Changes.Select(change => new CommandTargetResponse
            {
                MacAddress = change.Target.ToString(),
                DisplayName = state.NameOf(change.Target),
                PreviousState = change.PreviousState,
                NewState = change.NewState,
                CommandId = latest.TryGetValue(change.Target, out var command) ? command.Id : null,
                Status = latest.TryGetValue(change.Target, out var current) ? current.Status.ToString() : "Expired"
            })]
        };
    }

    /// <summary>The current suggestions, split into actions and insights.</summary>
    public static InsightsResponse Insights(StreamState state, DateTimeOffset now, StreamOptions options)
    {
        var mapped = state.Suggestions.Select(suggestion => Suggestion(state, suggestion)).ToList();

        return new InsightsResponse
        {
            Actions = [.. mapped.Where(suggestion => suggestion.Kind == nameof(SuggestionKind.Action))],
            Insights = [.. mapped.Where(suggestion => suggestion.Kind == nameof(SuggestionKind.Insight))],
            GeneratedAtUtc = now,
            PatternsLearned = state.Miner.AllRules(options.Advisor.MinSupport, options.Advisor.MinConfidence).Count,
            EventsObserved = state.Miner.TotalEvents,
            WindowMinutes = state.Miner.Window.TotalMinutes
        };
    }

    /// <summary>One suggestion.</summary>
    public static SuggestionResponse Suggestion(StreamState state, Suggestion suggestion) => new()
    {
        Key = suggestion.Key,
        Kind = suggestion.Kind.ToString(),
        Title = suggestion.Title,
        Reason = suggestion.Reason,
        Source = suggestion.Source,
        Score = Math.Round(suggestion.Score, 3),
        Confidence = suggestion.Confidence is { } confidence ? Math.Round(confidence, 3) : null,
        Support = suggestion.Support,
        MacAddress = suggestion.Target?.ToString(),
        DisplayName = suggestion.Target is { } mac ? state.NameOf(mac) : null,
        DesiredState = suggestion.DesiredState
    };

    /// <summary>One learned rule, with both ends in words.</summary>
    public static PatternRuleResponse Rule(StreamState state, PatternRule rule, StreamOptions options) => new()
    {
        Trusted = rule.Support >= options.Advisor.MinSupport && rule.Confidence >= options.Advisor.MinConfidence,
        Antecedent = rule.Antecedent,
        AntecedentLabel = Label(state, rule.Antecedent),
        Consequent = rule.Consequent,
        ConsequentLabel = Label(state, rule.Consequent),
        Support = rule.Support,
        AntecedentCount = rule.AntecedentCount,
        Confidence = Math.Round(rule.Confidence, 3),
        Lift = Math.Round(rule.Lift, 2),
        MeanGapSeconds = Math.Round(rule.MeanGap.TotalSeconds, 1)
    };

    /// <summary>An activity token in words: "Soil Moisture Probe 01: critical low reading".</summary>
    public static string Label(StreamState state, string token)
    {
        if (!ActivityToken.TryParse(token, out var activity))
        {
            return token;
        }

        var name = activity.Mac is { } mac ? state.NameOf(mac) : string.Empty;

        return activity.Kind switch
        {
            ActivityKind.Alert => $"{name}: {AlertCodes.Describe(activity.Code!.Value).ToLowerInvariant()}",
            ActivityKind.Disconnect => $"{name} went offline",
            ActivityKind.Search => $"Searched “{activity.Text}”",
            ActivityKind.Inspect => $"Opened {name}",
            ActivityKind.Command => $"Switched {name} {(activity.State == true ? "ON" : "OFF")}",
            _ => token
        };
    }
}
