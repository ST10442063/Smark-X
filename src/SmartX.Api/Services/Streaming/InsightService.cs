using SmartX.Contracts.Commands;
using SmartX.Contracts.Insights;
using SmartX.Core.Commands;
using SmartX.Core.Devices;
using SmartX.Core.Insights;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// The predictive engine's API: current suggestions, what the operator does, and
/// accepting or dismissing advice. The analysis itself is
/// <see cref="ActionAdvisor"/> in SmartX.Core; this class feeds it and acts on it.
/// </summary>
public sealed class InsightService(CommandStreamStore store, CommandService commands)
{
    /// <summary>The current suggestions, recomputed first if anything has changed.</summary>
    public InsightsResponse Current()
    {
        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            if (state.SuggestionsDirty)
            {
                store.RefreshSuggestions(state, now);
            }

            return StreamMapper.Insights(state, now, store.Options);
        });
    }

    /// <summary>
    /// Feeds an operator search or device inspection into the behaviour model and
    /// returns the suggestions it now produces - a search can itself be the trigger
    /// for advice.
    /// </summary>
    /// <returns>Null when the activity was empty or named an unknown device.</returns>
    public InsightsResponse? Record(RecordActivityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? token;

        if (string.Equals(request.Kind, "Inspect", StringComparison.OrdinalIgnoreCase))
        {
            if (!MacKey.TryParse(request.MacAddress, out var mac) || !store.Read(state => state.Devices.Contains(mac)))
            {
                return null;
            }

            token = ActivityToken.Inspect(mac);
        }
        else
        {
            token = ActivityToken.Search(request.Text);
        }

        if (token is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            state.Miner.Observe(token, now);
            store.RefreshSuggestions(state, now);
            return StreamMapper.Insights(state, now, store.Options);
        });
    }

    /// <summary>
    /// Acts on a suggestion: runs the command for an action, and for an insight
    /// records that the operator looked at the device and puts the advice away.
    /// </summary>
    public (int Status, SuggestionAcceptedResponse? Response, string Message) Accept(string key)
    {
        var suggestion = store.Read(state => state.Suggestions.FirstOrDefault(item => item.Key == key));

        if (suggestion is null)
        {
            return (404, null, "That suggestion is no longer current - the situation behind it has changed.");
        }

        CommandOutcomeResponse? outcome = null;
        string message;

        if (suggestion is { Kind: SuggestionKind.Action, Target: { } target, DesiredState: { } desired })
        {
            var result = commands.Issue(
                new IssueCommandRequest
                {
                    Action = desired ? nameof(CommandAction.SwitchOn) : nameof(CommandAction.SwitchOff),
                    Targets = [target.ToString()],
                    Label = suggestion.Title
                },
                CommandOrigin.Suggestion);

            if (!result.Succeeded)
            {
                return (result.Status, null, result.Message);
            }

            outcome = result.Outcome;
            message = result.Message;
        }
        else
        {
            message = $"Noted: {suggestion.Title}.";
        }

        var now = DateTimeOffset.UtcNow;

        var insights = store.Write(state =>
        {
            if (suggestion.Kind == SuggestionKind.Insight)
            {
                if (suggestion.Target is { } mac)
                {
                    state.Miner.Observe(ActivityToken.Inspect(mac), now);
                }

                // An insight acted on has done its job; keep it from reappearing at once.
                state.DismissedUntil[suggestion.Key] = now.AddMinutes(store.Options.DismissMinutes);
            }

            state.Log(StreamEventKind.SuggestionAccepted, StreamEventLevel.Info, now, suggestion.Target,
                $"Accepted suggestion: {suggestion.Title}", suggestion.Reason);

            store.RefreshSuggestions(state, now);
            return StreamMapper.Insights(state, now, store.Options);
        });

        return (200, new SuggestionAcceptedResponse { Message = message, Command = outcome, Insights = insights }, message);
    }

    /// <summary>Hides a suggestion for the configured time.</summary>
    public InsightsResponse Dismiss(string key)
    {
        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            state.DismissedUntil[key] = now.AddMinutes(store.Options.DismissMinutes);
            store.RefreshSuggestions(state, now);
            return StreamMapper.Insights(state, now, store.Options);
        });
    }

    /// <summary>Every rule the engine currently trusts, strongest first.</summary>
    public List<PatternRuleResponse> Patterns(int minSupport, double minConfidence)
        => store.Read(state => state.Miner.AllRules(minSupport, minConfidence)
            .Select(rule => StreamMapper.Rule(state, rule, store.Options))
            .ToList());
}
