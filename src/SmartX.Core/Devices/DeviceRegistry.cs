using SmartX.Core.Models;

namespace SmartX.Core.Devices;

/// <summary>A device query: every part is optional, and the parts that are given all apply.</summary>
/// <param name="Category">Only devices of this operational class.</param>
/// <param name="RestrictTo">Only devices in this set - an alert state, for instance.</param>
/// <param name="Text">Only devices whose name, path or address contains this text.</param>
public sealed record DeviceQuery(
    SensorCategory? Category = null,
    IReadOnlySet<MacKey>? RestrictTo = null,
    string? Text = null);

/// <summary>What a filter cost, so the console can show the indexes earning their keep.</summary>
/// <param name="FleetSize">Devices registered.</param>
/// <param name="CandidatesExamined">Devices the text match actually had to look at.</param>
/// <param name="Matched">Devices returned.</param>
public readonly record struct FilterCost(int FleetSize, int CandidatesExamined, int Matched);

/// <summary>
/// Every live device, keyed by MAC address, with secondary indexes for filtering.
/// <para>
/// <b>Why a <see cref="Dictionary{TKey,TValue}"/>.</b> Every packet the mesh
/// publishes starts with "which device is this?". The Part 1 sorted index answers
/// that by binary search, which is O(log n): about eleven comparisons for a thousand
/// nodes, and every one of them a string comparison. Keyed on <see cref="MacKey"/>,
/// the dictionary hashes one integer, goes straight to a bucket and makes one
/// equality check - O(1), whatever the size of the fleet. That lookup is on the
/// hot path of every packet.
/// </para>
/// <para>
/// <b>Why the category index is a dictionary of sets.</b> Filtering "actuators
/// with a critical alert" by scanning every device is O(n) per keystroke. Here each
/// category maps to the set of its devices, and the alert state is also a set, so
/// the filter is a set intersection. The intersection walks the smaller set and
/// tests membership in the larger one in O(1), so its cost is O(min(a, b)), and
/// the free-text match only ever sees the devices that survived it.
/// </para>
/// <para>
/// A second dictionary maps the Part 1 sensor key to the MAC, so code that only
/// has a sensor id - the Part 1 workbench, for one - still reaches the device in
/// O(1). Not thread-safe: the stream store serialises access.
/// </para>
/// </summary>
public sealed class DeviceRegistry
{
    private readonly Dictionary<MacKey, DeviceState> _devices = [];
    private readonly Dictionary<Guid, MacKey> _bySensorId = [];
    private readonly Dictionary<SensorCategory, HashSet<MacKey>> _byCategory = [];

    /// <summary>Devices registered.</summary>
    public int Count => _devices.Count;

    /// <summary>Every device, in no particular order.</summary>
    public IReadOnlyCollection<DeviceState> All => _devices.Values;

    /// <summary>Every device key.</summary>
    public IReadOnlyCollection<MacKey> Keys => _devices.Keys;

    /// <summary>Adds a device. Returns false when its MAC is already registered.</summary>
    public bool TryAdd(DeviceState device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!_devices.TryAdd(device.Mac, device))
        {
            return false;
        }

        _bySensorId[device.SensorId] = device.Mac;

        if (!_byCategory.TryGetValue(device.Category, out var members))
        {
            members = [];
            _byCategory[device.Category] = members;
        }

        members.Add(device.Mac);
        return true;
    }

    /// <summary>Finds a device by MAC address in O(1).</summary>
    public bool TryGet(MacKey mac, out DeviceState device) => _devices.TryGetValue(mac, out device!);

    /// <summary>Finds a device by its Part 1 sensor key in O(1).</summary>
    public bool TryGet(Guid sensorId, out DeviceState device)
    {
        if (_bySensorId.TryGetValue(sensorId, out var mac))
        {
            return _devices.TryGetValue(mac, out device!);
        }

        device = null!;
        return false;
    }

    /// <summary>Whether a MAC is registered.</summary>
    public bool Contains(MacKey mac) => _devices.ContainsKey(mac);

    /// <summary>The devices in one category, as a set.</summary>
    public IReadOnlySet<MacKey> InCategory(SensorCategory category)
        => _byCategory.TryGetValue(category, out var members) ? members : EmptySet;

    /// <summary>How many devices each category holds.</summary>
    public Dictionary<SensorCategory, int> CategoryCounts()
        => _byCategory.ToDictionary(pair => pair.Key, pair => pair.Value.Count);

    /// <summary>
    /// Runs a query: narrows by set intersection first, then applies the text
    /// match to whatever is left.
    /// </summary>
    /// <param name="query">What to match.</param>
    /// <param name="cost">How many devices the text match had to examine.</param>
    /// <returns>Matching devices, by display name.</returns>
    public List<DeviceState> Query(DeviceQuery query, out FilterCost cost)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<MacKey> candidates = _devices.Keys;
        var candidateCount = _devices.Count;

        if (query.Category is { } category)
        {
            var members = InCategory(category);
            candidates = members;
            candidateCount = members.Count;

            if (query.RestrictTo is { } restrict)
            {
                var both = Intersect(members, restrict);
                candidates = both;
                candidateCount = both.Count;
            }
        }
        else if (query.RestrictTo is { } restrict)
        {
            // Keys the registry no longer holds are dropped by the lookup below.
            candidates = restrict;
            candidateCount = restrict.Count;
        }

        var text = query.Text?.Trim();
        var matches = new List<DeviceState>();

        foreach (var mac in candidates)
        {
            if (!_devices.TryGetValue(mac, out var device))
            {
                continue;
            }

            if (string.IsNullOrEmpty(text) || MatchesText(device, text))
            {
                matches.Add(device);
            }
        }

        matches.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
        cost = new FilterCost(_devices.Count, candidateCount, matches.Count);
        return matches;
    }

    /// <summary>
    /// Free-text match against the fields an operator would type: the name
    /// ("pump a"), the location ("zone 1") or the address.
    /// </summary>
    public static bool MatchesText(DeviceState device, string text)
    {
        ArgumentNullException.ThrowIfNull(device);

        return device.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
               || device.QualifiedPath.Contains(text, StringComparison.OrdinalIgnoreCase)
               || device.Mac.ToString().Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Intersects two sets by walking the smaller and probing the larger, so the
    /// cost is bounded by the smaller set rather than the fleet.
    /// </summary>
    private static List<MacKey> Intersect(IReadOnlySet<MacKey> first, IReadOnlySet<MacKey> second)
    {
        var (small, large) = first.Count <= second.Count ? (first, second) : (second, first);
        var result = new List<MacKey>(small.Count);

        foreach (var mac in small)
        {
            if (large.Contains(mac))
            {
                result.Add(mac);
            }
        }

        return result;
    }

    private static readonly IReadOnlySet<MacKey> EmptySet = new HashSet<MacKey>();
}
