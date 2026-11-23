using System.Text;
using SmartX.Core.Alerts;
using SmartX.Core.Devices;

namespace SmartX.Core.Insights;

/// <summary>What kind of thing an activity token records.</summary>
public enum ActivityKind
{
    /// <summary>A condition the gateway raised - not something the operator did.</summary>
    Alert = 0,

    /// <summary>A node went offline.</summary>
    Disconnect = 1,

    /// <summary>The operator typed a search.</summary>
    Search = 2,

    /// <summary>The operator opened a device.</summary>
    Inspect = 3,

    /// <summary>The operator switched an actuator.</summary>
    Command = 4
}

/// <summary>An activity token taken apart again.</summary>
/// <param name="Kind">What it records.</param>
/// <param name="Mac">The device, for every kind except a search.</param>
/// <param name="Code">The alert condition, for an alert.</param>
/// <param name="State">The relay state, for a command.</param>
/// <param name="Text">The normalised search text, for a search.</param>
public readonly record struct ParsedActivity(
    ActivityKind Kind,
    MacKey? Mac,
    AlertCode? Code,
    bool? State,
    string? Text);

/// <summary>
/// The vocabulary the pattern miner learns over. Every event - an alert, a
/// disconnect, a search, a device opened, a command - is reduced to one short
/// string, so the miner can count them all in the same dictionaries:
/// <code>
/// alert:CriticalLow:A4:CF:12:00:00:00
/// down:A4:CF:12:00:0D:E1
/// search:pump a
/// inspect:A4:CF:12:01:00:01
/// command:on:A4:CF:12:01:00:01
/// </code>
/// <para>
/// A command is recorded by the state it produced rather than the button
/// pressed. "Toggle" means opposite things depending on where the relay started,
/// so learning "after this alert, toggle the pump" would teach the engine nothing;
/// learning "after this alert, switch the pump on" teaches it the actual habit.
/// </para>
/// </summary>
public static class ActivityToken
{
    /// <summary>Longest search text kept. Longer input is cut, so one paste cannot flood the model.</summary>
    public const int MaxSearchLength = 40;

    /// <summary>An alert condition on a device.</summary>
    public static string Alert(AlertCode code, MacKey mac) => $"alert:{code}:{mac}";

    /// <summary>A device going offline.</summary>
    public static string Disconnect(MacKey mac) => $"down:{mac}";

    /// <summary>An operator search, or null when the text normalises to nothing.</summary>
    public static string? Search(string? text)
        => NormaliseSearch(text) is { Length: > 0 } normalised ? $"search:{normalised}" : null;

    /// <summary>An operator opening a device.</summary>
    public static string Inspect(MacKey mac) => $"inspect:{mac}";

    /// <summary>An operator switching an actuator to a state.</summary>
    public static string Command(bool state, MacKey mac) => $"command:{(state ? "on" : "off")}:{mac}";

    /// <summary>
    /// Lower-cases, trims and collapses whitespace, so "Pump A", " pump  a " and
    /// "PUMP A" all count as the same search.
    /// </summary>
    public static string NormaliseSearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxSearchLength));
        var pendingSpace = false;

        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(char.ToLowerInvariant(character));

            if (builder.Length >= MaxSearchLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Takes a token apart. Returns false for anything this vocabulary did not produce.</summary>
    public static bool TryParse(string? token, out ParsedActivity parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var separator = token.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var prefix = token[..separator];
        var rest = token[(separator + 1)..];

        switch (prefix)
        {
            case "search" when rest.Length > 0:
                parsed = new ParsedActivity(ActivityKind.Search, null, null, null, rest);
                return true;

            case "down" when MacKey.TryParse(rest, out var downMac):
                parsed = new ParsedActivity(ActivityKind.Disconnect, downMac, null, null, null);
                return true;

            case "inspect" when MacKey.TryParse(rest, out var inspectMac):
                parsed = new ParsedActivity(ActivityKind.Inspect, inspectMac, null, null, null);
                return true;

            case "alert":
            {
                var codeEnd = rest.IndexOf(':', StringComparison.Ordinal);
                if (codeEnd > 0
                    && Enum.TryParse<AlertCode>(rest[..codeEnd], out var code)
                    && MacKey.TryParse(rest[(codeEnd + 1)..], out var alertMac))
                {
                    parsed = new ParsedActivity(ActivityKind.Alert, alertMac, code, null, null);
                    return true;
                }

                return false;
            }

            case "command":
            {
                var stateEnd = rest.IndexOf(':', StringComparison.Ordinal);
                if (stateEnd > 0 && MacKey.TryParse(rest[(stateEnd + 1)..], out var commandMac))
                {
                    var state = rest[..stateEnd];
                    if (state is "on" or "off")
                    {
                        parsed = new ParsedActivity(ActivityKind.Command, commandMac, null, state == "on", null);
                        return true;
                    }
                }

                return false;
            }

            default:
                return false;
        }
    }
}
