using SmartX.Core.Alerts;
using SmartX.Core.Devices;
using SmartX.Core.Insights;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// Gives the pattern miner a few days of past operator behaviour to learn from,
/// so the predictive engine has something to say on first open.
/// <para>
/// <b>This is demonstration history, and it is labelled as such.</b> It goes into
/// the behaviour model only - not into the event log as if it had happened - and a
/// single log entry says it was loaded. Everything the operator does from then on is
/// learned on top of it, live, through exactly the same code path, so the engine
/// can be watched picking up a brand-new habit in a couple of minutes.
/// </para>
/// <para>
/// The sessions follow the brief's own example: when the soil moisture probe drops
/// below 20%, the operator searches for "Pump A" and switches it on. Two other
/// routines give the engine a power spike and an outage to reason about. Each has a
/// session that breaks the pattern, so the learned confidence is a realistic 75-80%
/// rather than a suspicious 100%.
/// </para>
/// </summary>
public sealed class OperatorHistorySeeder(CommandStreamStore store, ILogger<OperatorHistorySeeder> logger)
{
    /// <summary>Loads the sessions. Skips any routine whose devices are not registered.</summary>
    /// <returns>Sessions loaded.</returns>
    public int Seed()
    {
        if (!store.Options.SeedOperatorHistory)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            MacKey? Find(string name) => state.Devices.All
                .FirstOrDefault(device => string.Equals(device.DisplayName, name, StringComparison.OrdinalIgnoreCase))?.Mac;

            var probe = Find("Soil Moisture Probe 01");
            var pumpA = Find("Water Pump A");
            var lightingMeter = Find("Lighting Rail Meter 13");
            var growLights = Find("Grow Light Relay");
            var silentValve = Find("Irrigation Valve 14");
            var repeater = Find("Mesh Repeater 08");

            var events = new List<(DateTimeOffset At, string Token)>();
            var sessions = 0;

            // Sessions are spread over the last three days, a few hours apart, so
            // nothing from one session falls inside another's ten-minute window.
            var start = now.AddDays(-3);
            var slot = 0;
            DateTimeOffset NextSlot() => start.AddHours(5 * slot++);

            if (probe is { } p && pumpA is { } pump)
            {
                for (var session = 0; session < 5; session++)
                {
                    var at = NextSlot();
                    events.Add((at, ActivityToken.Alert(AlertCode.CriticalLow, p)));

                    if (session == 2)
                    {
                        // The odd one out: the operator went and looked at the probe instead.
                        events.Add((at.AddMinutes(1), ActivityToken.Search("moisture")!));
                        events.Add((at.AddMinutes(2), ActivityToken.Inspect(p)));
                    }
                    else
                    {
                        events.Add((at.AddMinutes(1), ActivityToken.Search("pump a")!));
                        events.Add((at.AddMinutes(2), ActivityToken.Command(true, pump)));
                    }

                    sessions++;
                }
            }

            if (lightingMeter is { } meter && growLights is { } relay)
            {
                for (var session = 0; session < 4; session++)
                {
                    var at = NextSlot();
                    events.Add((at, ActivityToken.Alert(AlertCode.PowerSpike, meter)));
                    events.Add((at.AddSeconds(30), ActivityToken.Inspect(meter)));

                    if (session != 1)
                    {
                        events.Add((at.AddSeconds(90), ActivityToken.Command(false, relay)));
                    }

                    sessions++;
                }
            }

            if (silentValve is { } valve && repeater is { } link)
            {
                for (var session = 0; session < 3; session++)
                {
                    var at = NextSlot();
                    events.Add((at, ActivityToken.Disconnect(valve)));
                    events.Add((at.AddMinutes(1), ActivityToken.Inspect(link)));
                    sessions++;
                }
            }

            // The miner expects events in time order.
            foreach (var (at, token) in events.OrderBy(entry => entry.At))
            {
                state.Miner.Observe(token, at);
            }

            if (sessions > 0)
            {
                state.Log(StreamEventKind.GatewayNote, StreamEventLevel.Info, now, null,
                    $"Pattern engine primed with {sessions} past operator sessions",
                    "Demonstration history from the seeder, loaded into the behaviour model only. " +
                    "Everything you do from now on is learned on top of it.");
                state.SuggestionsDirty = true;
            }

            logger.LogInformation(
                "Loaded {Sessions} demonstration operator sessions ({Events} events) into the pattern miner.",
                sessions, events.Count);

            return sessions;
        });
    }
}
