using SmartX.Core.Devices;

namespace SmartX.Core.Alerts;

/// <summary>What one health sweep changed.</summary>
/// <param name="NewlyDown">Nodes that went silent since the previous sweep - alert on these.</param>
/// <param name="Recovered">Nodes that were down and are reporting again.</param>
/// <param name="StillDown">Nodes that were already known to be down - the duplicates not re-raised.</param>
public sealed record OutageSweep(
    IReadOnlySet<MacKey> NewlyDown,
    IReadOnlySet<MacKey> Recovered,
    int StillDown);

/// <summary>
/// The set of nodes currently known to be disconnected.
/// <para>
/// <b>Why sets, and why set algebra.</b> The gateway sweeps the fleet every few
/// seconds and finds the nodes that have missed their heartbeat. A node that went
/// offline overnight is found silent on every one of thousands of sweeps, and the
/// operator needs to hear about it once. Rather than asking "have I already
/// alerted on this one?" node by node, each sweep is reconciled against the
/// known-down set with two set differences:
/// </para>
/// <code>
/// newlyDown = silentNow \ knownDown     // alert on these
/// recovered = knownDown \ silentNow     // clear these
/// </code>
/// <para>
/// Each difference is O(n) over the smaller operand with an O(1) membership test
/// per element. A node that stays down produces no event at all after its first
/// sweep, and the count of those absorbed repeats is kept so the suppression is
/// visible rather than silent. Not thread-safe: the stream store serialises access.
/// </para>
/// </summary>
public sealed class OutageTracker
{
    private readonly HashSet<MacKey> _down = [];

    /// <summary>Nodes currently known to be down.</summary>
    public IReadOnlySet<MacKey> Down => _down;

    /// <summary>Nodes currently known to be down.</summary>
    public int Count => _down.Count;

    /// <summary>Disconnects that were already known and so were not raised again.</summary>
    public long DuplicatesSuppressed { get; private set; }

    /// <summary>Distinct outages detected since startup.</summary>
    public long OutagesDetected { get; private set; }

    /// <summary>Whether a node is known to be down.</summary>
    public bool IsDown(MacKey mac) => _down.Contains(mac);

    /// <summary>
    /// Reconciles the result of a sweep against what was already known.
    /// </summary>
    /// <param name="silentNow">Every node the sweep found silent. Duplicates in the input are harmless.</param>
    public OutageSweep Reconcile(IEnumerable<MacKey> silentNow)
    {
        ArgumentNullException.ThrowIfNull(silentNow);

        var current = new HashSet<MacKey>(silentNow);

        var newlyDown = new HashSet<MacKey>(current);
        newlyDown.ExceptWith(_down);

        var recovered = new HashSet<MacKey>(_down);
        recovered.ExceptWith(current);

        var stillDown = current.Count - newlyDown.Count;
        DuplicatesSuppressed += stillDown;
        OutagesDetected += newlyDown.Count;

        _down.ExceptWith(recovered);
        _down.UnionWith(newlyDown);

        return new OutageSweep(newlyDown, recovered, stillDown);
    }

    /// <summary>
    /// Marks one node as reporting again, straight away, when a packet arrives from
    /// it - rather than leaving it flagged down until the next sweep notices.
    /// </summary>
    /// <returns>True when the node had been down.</returns>
    public bool MarkRecovered(MacKey mac) => _down.Remove(mac);
}
