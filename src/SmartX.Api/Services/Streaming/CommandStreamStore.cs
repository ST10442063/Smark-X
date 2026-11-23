using Microsoft.Extensions.Options;
using SmartX.Api.Models;
using SmartX.Contracts.Stream;
using SmartX.Core.Alerts;
using SmartX.Core.Devices;
using SmartX.Core.Insights;
using SmartX.Core.Models;
using SmartX.Core.Streaming;
using SmartX.Core.Telemetry;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services.Streaming;

/// <summary>What observing one packet changed, for the caller to push.</summary>
/// <param name="Alert">Set when the packet was critical: the alert to push, new or absorbed.</param>
public readonly record struct Observation(CriticalAlertResponse? Alert);

/// <summary>
/// The command stream's state, and the lock around it.
/// <para>
/// A singleton, like Part 1's <see cref="GatewayStore"/>, because it <i>is</i> the
/// live picture of the mesh. It keeps its own lock rather than sharing the
/// gateway's, so a slow history query on the Part 1 pages never stalls the live
/// stream. The two locks are never held together - every path takes one, releases
/// it, then takes the other - so they cannot deadlock.
/// </para>
/// <para>
/// It stays in step with registrations through <see cref="GatewayStore.SensorRegistered"/>,
/// and catches up on whatever was registered before it was created.
/// </para>
/// </summary>
public sealed class CommandStreamStore
{
    /// <summary>Seconds the throughput figure is averaged over.</summary>
    public const int ThroughputWindowSeconds = 5;

    private readonly object _gate = new();
    private readonly StreamState _state;
    private readonly GatewayStore _gateway;
    private readonly StreamOptions _options;

    /// <summary>Creates the store and starts tracking the gateway registry.</summary>
    public CommandStreamStore(GatewayStore gateway, IOptions<StreamOptions> options)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(options);

        _gateway = gateway;
        _options = options.Value;
        _state = new StreamState(
            _options.UndoDepth,
            _options.EventLogCapacity,
            TimeSpan.FromMinutes(_options.PatternWindowMinutes));

        // Subscribe first, then catch up: a sensor registered in between is seen
        // twice, and the registry simply refuses the second add.
        _gateway.SensorRegistered += Track;
        foreach (var sensor in _gateway.AllSensors())
        {
            Track(sensor);
        }
    }

    /// <summary>The options the stream was configured with.</summary>
    public StreamOptions Options => _options;

    /// <summary>Runs <paramref name="reader"/> under the lock and returns its result.</summary>
    public T Read<T>(Func<StreamState, T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock (_gate)
        {
            return reader(_state);
        }
    }

    /// <summary>Runs <paramref name="writer"/> under the lock and returns its result.</summary>
    public T Write<T>(Func<StreamState, T> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        lock (_gate)
        {
            return writer(_state);
        }
    }

    /// <summary>Runs <paramref name="writer"/> under the lock.</summary>
    public void Write(Action<StreamState> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        lock (_gate)
        {
            writer(_state);
        }
    }

    /// <summary>Adds a newly registered sensor to the live registry.</summary>
    public void Track(SensorRecord sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        if (!MacKey.TryParse(sensor.MacAddress, out var mac))
        {
            return;
        }

        lock (_gate)
        {
            if (_state.Devices.TryAdd(new DeviceState(mac, sensor)))
            {
                _state.ChangedDevices.Add(mac);
                _state.SuggestionsDirty = true;
            }
        }
    }

    /// <summary>
    /// Loads each device's timeline from the Part 1 historical store, for devices
    /// that have not observed anything yet. Run once after seeding, so the timeline
    /// graphs have hours of history from the first page load.
    /// </summary>
    /// <param name="perDevice">Most recent readings to load per device.</param>
    /// <returns>Readings loaded.</returns>
    public int BackfillFromHistory(int perDevice = 360)
    {
        var pending = Read(state => state.Devices.All
            .Where(device => device.PacketsObserved == 0)
            .Select(device => (device.Mac, device.SensorId))
            .ToList());

        var loaded = 0;

        foreach (var (mac, sensorId) in pending)
        {
            // Read from the gateway first, outside this store's lock, so the two
            // locks are never held at once.
            var readings = _gateway.ReadHistory(sensorId, history => history.TakeNewest(perDevice));

            lock (_gate)
            {
                if (!_state.Devices.TryGet(mac, out var device) || device.PacketsObserved > 0)
                {
                    continue;
                }

                foreach (var reading in readings)
                {
                    device.Observe(reading.Value, reading.TimestampUtc);
                }

                loaded += readings.Count;
                _state.ChangedDevices.Add(mac);
            }
        }

        return loaded;
    }

    /// <summary>Records a packet that came through the dispatch queue.</summary>
    public Observation ObserveQueued(Dispatched<QueuedPacket> dispatched)
    {
        var item = dispatched.Item;
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            _state.LastUplinkUtc = now;

            if (!_state.Devices.TryGet(item.Mac, out var device))
            {
                return default;
            }

            return ObserveCore(
                device,
                dispatched.Sequence,
                item.Packet.NumericValue,
                item.Packet.TimestampUtc,
                item.Triage,
                dispatched.Lane,
                dispatched.Waited.TotalMilliseconds,
                dispatched.Overtook,
                now);
        }
    }

    /// <summary>
    /// Records packets that were accepted directly - the Part 1 workbench publishes
    /// synchronously rather than through the queue. They are triaged here instead
    /// of at the door, so a critical reading raises the same alert whichever way in
    /// it came.
    /// </summary>
    public IReadOnlyList<Observation> ObserveDirect(SensorRecord sensor, IReadOnlyList<ITelemetryPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(packets);

        if (!MacKey.TryParse(sensor.MacAddress, out var mac))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var observations = new List<Observation>();

        lock (_gate)
        {
            if (!_state.Devices.TryGet(mac, out var device))
            {
                return [];
            }

            foreach (var packet in packets)
            {
                var triage = CriticalBandClassifier.Classify(
                    device.Category, device.Unit, packet.NumericValue, device.Baseline, false, _options.Bands);

                var observation = ObserveCore(
                    device, 0, packet.NumericValue, packet.TimestampUtc, triage, triage.Lane, 0d, 0, now);

                if (observation.Alert is not null)
                {
                    observations.Add(observation);
                }
            }
        }

        return observations;
    }

    /// <summary>Recomputes the suggestions. The caller holds the lock.</summary>
    public List<Suggestion> RefreshSuggestions(StreamState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (var expired in state.DismissedUntil.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
        {
            state.DismissedUntil.Remove(expired);
        }

        var hourAgo = now.AddHours(-1);
        var criticals = new Dictionary<MacKey, int>();
        var switches = new Dictionary<MacKey, int>();

        foreach (var entry in state.Events.Between(hourAgo, now, 5000))
        {
            if (entry.Mac is null || !MacKey.TryParse(entry.Mac, out var mac))
            {
                continue;
            }

            if (entry is { Kind: StreamEventKind.AlertRaised, Level: StreamEventLevel.Critical })
            {
                criticals[mac] = criticals.GetValueOrDefault(mac) + 1;
            }
            else if (entry.Kind == StreamEventKind.CommandIssued)
            {
                switches[mac] = switches.GetValueOrDefault(mac) + 1;
            }
        }

        var context = new AdvisorContext(
            now,
            state.Miner,
            state.Devices,
            state.Alerts,
            state.Outages,
            criticals,
            switches,
            state.DismissedUntil.Keys.ToHashSet(StringComparer.Ordinal),
            _options.Bands);

        state.Suggestions = ActionAdvisor.Advise(context, _options.Advisor);
        state.SuggestionsDirty = false;
        return state.Suggestions;
    }

    /// <summary>Packets processed per second over the throughput window. The caller holds the lock.</summary>
    public static double Throughput(StreamState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        TrimThroughput(state, now);
        return state.ThroughputWindow.Count / (double)ThroughputWindowSeconds;
    }

    // Callers hold the lock.
    private Observation ObserveCore(
        DeviceState device,
        long sequence,
        double value,
        DateTimeOffset capturedUtc,
        TriageResult triage,
        DispatchLane lane,
        double waitedMs,
        int overtook,
        DateTimeOffset now)
    {
        var state = _state;
        var isLatest = device.Observe(value, capturedUtc, triage.IsUrgent);
        state.ChangedDevices.Add(device.Mac);

        state.ThroughputWindow.Enqueue(now);
        TrimThroughput(state, now);

        if (state.Outages.MarkRecovered(device.Mac))
        {
            state.Log(StreamEventKind.NodeRecovered, StreamEventLevel.Info, now, device.Mac,
                $"{device.DisplayName} is reporting again", "A packet arrived from a node that had been marked offline.");
            state.SuggestionsDirty = true;
        }

        var reading = StreamFormat.Reading(device, value);
        CriticalAlertResponse? alert = null;

        if (triage.Code is { } code)
        {
            var isNew = state.Alerts.TryRaise(new AlertKey(device.Mac, code));
            var severity = AlertCodes.SeverityOf(code);

            if (isNew)
            {
                state.Log(
                    StreamEventKind.AlertRaised,
                    severity == AlertSeverity.Critical ? StreamEventLevel.Critical : StreamEventLevel.Warning,
                    now,
                    device.Mac,
                    $"{AlertCodes.Describe(code)} on {device.DisplayName}",
                    $"{Capitalise(triage.Reason)}. Processed through the priority lane after {waitedMs:0.#} ms, " +
                    $"ahead of {overtook} queued routine packet{(overtook == 1 ? string.Empty : "s")}.");

                // The alert is part of what the operator reacts to, so it goes into the
                // behaviour model alongside their own searches and commands.
                state.Miner.Observe(ActivityToken.Alert(code, device.Mac), now);
                state.SuggestionsDirty = true;
            }

            alert = new CriticalAlertResponse
            {
                MacAddress = device.Mac.ToString(),
                DisplayName = device.DisplayName,
                Code = code.ToString(),
                Severity = severity.ToString(),
                Reading = reading,
                Reason = triage.Reason ?? string.Empty,
                IsNew = isNew,
                WaitedMs = Math.Round(waitedMs, 2),
                Overtook = overtook,
                DetectedAtUtc = now
            };
        }
        else if (isLatest)
        {
            // Only the newest reading can say a condition has ended. A routine packet
            // captured before the alert - one that was still queued behind it - is
            // history, not recovery.
            ClearRecovered(device, value, now);
        }

        var processed = new ProcessedPacketResponse
        {
            Sequence = sequence,
            MacAddress = device.Mac.ToString(),
            DisplayName = device.DisplayName,
            Lane = lane.ToString(),
            Reading = reading,
            WaitedMs = Math.Round(waitedMs, 2),
            Overtook = overtook,
            ProcessedAtUtc = now,
            Reason = triage.Reason
        };

        state.RecentPackets.Add(processed);
        state.PendingPackets.Add(processed);

        return new Observation(alert);
    }

    // Callers hold the lock.
    private void ClearRecovered(DeviceState device, double value, DateTimeOffset now)
    {
        foreach (var key in _state.Alerts.For(device.Mac))
        {
            if (!CriticalBandClassifier.HasRecovered(key.Code, value, device.Baseline, _options.Bands))
            {
                continue;
            }

            _state.Alerts.Clear(key);
            _state.Log(StreamEventKind.AlertCleared, StreamEventLevel.Info, now, device.Mac,
                $"{AlertCodes.Describe(key.Code)} cleared on {device.DisplayName}",
                $"Back to {StreamFormat.Reading(device, value)}, inside its normal band.");
            _state.SuggestionsDirty = true;
        }
    }

    private static string Capitalise(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : char.ToUpperInvariant(text[0]) + text[1..];

    private static void TrimThroughput(StreamState state, DateTimeOffset now)
    {
        var cutoff = now.AddSeconds(-ThroughputWindowSeconds);
        while (state.ThroughputWindow.Count > 0 && state.ThroughputWindow.Peek() < cutoff)
        {
            state.ThroughputWindow.Dequeue();
        }
    }
}

/// <summary>How the stream renders readings.</summary>
public static class StreamFormat
{
    /// <summary>"ON"/"OFF" for a relay, the value with its unit for everything else.</summary>
    public static string Reading(DeviceState device, double value)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.IsActuator || device.Unit == TelemetryUnit.State)
        {
            return value > 0.5d ? "ON" : "OFF";
        }

        // A percentage sits against its number; every other unit takes a space.
        return device.Unit == TelemetryUnit.Percent
            ? $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%"
            : new SensorReading(value, device.Unit).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
