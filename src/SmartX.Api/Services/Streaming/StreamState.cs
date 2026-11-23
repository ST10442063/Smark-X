using SmartX.Contracts.Stream;
using SmartX.Core.Alerts;
using SmartX.Core.Collections;
using SmartX.Core.Commands;
using SmartX.Core.Devices;
using SmartX.Core.Insights;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// Every data structure behind the real-time command stream, in one place.
/// <para>
/// Instances are only ever touched inside <see cref="CommandStreamStore"/>'s lock -
/// the store hands this object to a callback rather than returning it, so nothing
/// can keep a reference and read it after the lock is released. That is the same
/// discipline as Part 1's <c>GatewayStore.ReadHistory</c>.
/// </para>
/// </summary>
public sealed class StreamState
{
    /// <summary>Creates the state.</summary>
    public StreamState(int undoDepth, int eventLogCapacity, TimeSpan patternWindow)
    {
        History = new CommandHistory(undoDepth);
        Events = new StreamEventLog(eventLogCapacity);
        Miner = new SequentialPatternMiner(patternWindow);
    }

    /// <summary>Live devices: <c>Dictionary&lt;MacKey, DeviceState&gt;</c> plus category sets.</summary>
    public DeviceRegistry Devices { get; } = new();

    /// <summary>Active alert conditions, each once: <c>HashSet&lt;AlertKey&gt;</c>.</summary>
    public ActiveAlertSet Alerts { get; } = new();

    /// <summary>Nodes known to be down: <c>HashSet&lt;MacKey&gt;</c>, reconciled by set difference.</summary>
    public OutageTracker Outages { get; } = new();

    /// <summary>Manual overrides: <c>Stack&lt;CommandRecord&gt;</c> for undo and another for redo.</summary>
    public CommandHistory History { get; }

    /// <summary>The event history: a reversed <c>SortedDictionary</c> keyed by time.</summary>
    public StreamEventLog Events { get; }

    /// <summary>The operator behaviour model.</summary>
    public SequentialPatternMiner Miner { get; }

    /// <summary>Every live command by id, so an acknowledgement finds its command in O(1).</summary>
    public Dictionary<Guid, DeviceCommand> Commands { get; } = [];

    /// <summary>
    /// Devices with something waiting in their downlink mailbox. A downlink poll
    /// walks this set rather than the whole fleet.
    /// </summary>
    public HashSet<MacKey> PendingMailboxes { get; } = [];

    /// <summary>Dismissed suggestion keys and when each may come back.</summary>
    public Dictionary<string, DateTimeOffset> DismissedUntil { get; } = new(StringComparer.Ordinal);

    /// <summary>The suggestions as last computed.</summary>
    public List<Suggestion> Suggestions { get; set; } = [];

    /// <summary>
    /// The most recently processed packets, for the live feed. This is Part 1's
    /// fixed-capacity ring buffer, reused: the feed only ever wants the newest few
    /// hundred, and the buffer never grows or reallocates however long the stream runs.
    /// </summary>
    public IngestionBuffer<ProcessedPacketResponse> RecentPackets { get; } = new(200);

    /// <summary>
    /// Processing timestamps inside the throughput window. A queue, because they
    /// expire in the order they were added: each new packet evicts the front.
    /// </summary>
    public Queue<DateTimeOffset> ThroughputWindow { get; } = new();

    /// <summary>Running command number for the console's "#12" labels.</summary>
    public long CommandNumber { get; set; }

    /// <summary>When any device last published through the uplink.</summary>
    public DateTimeOffset? LastUplinkUtc { get; set; }

    // ------------------------------------------------------------------
    // Change journal - what the broadcaster still has to push.
    // ------------------------------------------------------------------

    /// <summary>Devices whose live state changed since the last push. A set, so a busy device is pushed once.</summary>
    public HashSet<MacKey> ChangedDevices { get; } = [];

    /// <summary>Packets processed since the last push.</summary>
    public List<ProcessedPacketResponse> PendingPackets { get; } = [];

    /// <summary>Events logged since the last push.</summary>
    public List<StreamEvent> PendingEvents { get; } = [];

    /// <summary>Whether the command stacks changed since the last push.</summary>
    public bool CommandsChanged { get; set; }

    /// <summary>Whether something happened that could change the suggestions.</summary>
    public bool SuggestionsDirty { get; set; } = true;

    /// <summary>Records an event in the history and queues it for the console.</summary>
    public StreamEvent Log(
        StreamEventKind kind,
        StreamEventLevel level,
        DateTimeOffset timestampUtc,
        MacKey? mac,
        string title,
        string detail)
    {
        var entry = Events.Append(kind, level, timestampUtc, mac?.ToString(), title, detail);
        PendingEvents.Add(entry);
        return entry;
    }

    /// <summary>The device's display name, or its address when it is not registered.</summary>
    public string NameOf(MacKey mac) => Devices.TryGet(mac, out var device) ? device.DisplayName : mac.ToString();
}
