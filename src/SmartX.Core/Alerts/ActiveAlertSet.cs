using SmartX.Core.Devices;

namespace SmartX.Core.Alerts;

/// <summary>
/// The alert conditions that are currently active across the fleet, each held
/// exactly once.
/// <para>
/// <b>Why a <see cref="HashSet{T}"/>.</b> A power meter that spikes does not
/// spike once: it publishes every few seconds, and every packet while it is high
/// is critical. Raising an alert per packet would bury the operator under the
/// same alert forty times. The set answers "is this condition already open?" in
/// O(1) as part of adding it - <see cref="HashSet{T}.Add"/> returns false for an
/// element that is already present - so deduplication costs nothing extra and
/// cannot race with a separate check.
/// </para>
/// <para>
/// The packets are still processed and stored. Only the alert is suppressed, and
/// every suppression is counted so the dashboard can show how much noise the set
/// absorbed. Not thread-safe: the stream store serialises access.
/// </para>
/// </summary>
public sealed class ActiveAlertSet
{
    private readonly HashSet<AlertKey> _active = [];

    /// <summary>Conditions currently active.</summary>
    public int Count => _active.Count;

    /// <summary>Alerts raised since startup - first occurrences only.</summary>
    public long Raised { get; private set; }

    /// <summary>Repeat occurrences that were absorbed because the condition was already active.</summary>
    public long DuplicatesSuppressed { get; private set; }

    /// <summary>The active conditions.</summary>
    public IReadOnlyCollection<AlertKey> Active => _active;

    /// <summary>
    /// Records a condition. Returns true the first time, so the caller raises it,
    /// and false while it is already active, so the caller stays quiet.
    /// </summary>
    public bool TryRaise(AlertKey key)
    {
        if (_active.Add(key))
        {
            Raised++;
            return true;
        }

        DuplicatesSuppressed++;
        return false;
    }

    /// <summary>Whether a condition is active.</summary>
    public bool IsActive(AlertKey key) => _active.Contains(key);

    /// <summary>Clears one condition. Returns false when it was not active.</summary>
    public bool Clear(AlertKey key) => _active.Remove(key);

    /// <summary>Clears every condition on one device and returns how many there were.</summary>
    public int ClearDevice(MacKey mac) => _active.RemoveWhere(key => key.Mac == mac);

    /// <summary>The active conditions on one device.</summary>
    public List<AlertKey> For(MacKey mac) => [.. _active.Where(key => key.Mac == mac)];

    /// <summary>
    /// The devices with at least one active condition at <paramref name="severity"/>.
    /// Returned as a set so the device filter can intersect it with a category in
    /// O(min(a, b)).
    /// </summary>
    public HashSet<MacKey> DevicesAt(AlertSeverity severity)
    {
        var devices = new HashSet<MacKey>();
        foreach (var key in _active)
        {
            if (key.Severity == severity)
            {
                devices.Add(key.Mac);
            }
        }

        return devices;
    }

    /// <summary>Every device with any active condition.</summary>
    public HashSet<MacKey> DevicesWithAny()
    {
        var devices = new HashSet<MacKey>();
        foreach (var key in _active)
        {
            devices.Add(key.Mac);
        }

        return devices;
    }
}
