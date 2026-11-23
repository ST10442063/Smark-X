using SmartX.Core.Models;

namespace SmartX.Core.Collections;

/// <summary>
/// The gateway's registry of known nodes, held as a list kept permanently sorted
/// by MAC address so that the lookup on the hot path is a binary search.
/// <para>
/// <b>Why sorted.</b> Every telemetry packet that arrives has to be matched to a
/// registered sensor before it can be accepted. Scanning an unordered list makes
/// that O(n) per packet, so on a fleet of a thousand nodes ingesting a thousand
/// packets a second the gateway burns a million comparisons a second on lookup
/// alone. Keeping the list ordered turns each lookup into roughly ten comparisons
/// instead of five hundred.
/// </para>
/// <para>
/// The trade is on the other side: inserting into a sorted list has to shift the
/// tail, so registration is O(n). That is the right way round for this workload -
/// a node registers once and then publishes for months.
/// </para>
/// <para>
/// Free-text search deliberately stays linear. A query like "hydro bay" has to
/// look at display names, zones and categories, and no single ordering can serve
/// all three, so that path scans. Both methods record how many comparisons they
/// used, which the dashboard displays to make the difference visible.
/// </para>
/// <para><b>Thread safety.</b> Not thread-safe; the repository serialises access.</para>
/// </summary>
public sealed class SortedSensorIndex
{
    private readonly List<SensorRecord> _sensors = [];

    // Added in Part 2. Every accepted packet looks its sensor up by key, and the
    // MAC-ordered list says nothing about keys, so that lookup was a linear scan on
    // the hot path. A dictionary beside the list makes it O(1).
    private readonly Dictionary<Guid, SensorRecord> _byId = [];

    /// <summary>Number of registered sensors.</summary>
    public int Count => _sensors.Count;

    /// <summary>Comparisons used by the most recent <see cref="FindByMac"/> call.</summary>
    public int LastBinarySearchComparisons { get; private set; }

    /// <summary>Comparisons used by the most recent <see cref="Search"/> call.</summary>
    public int LastLinearSearchComparisons { get; private set; }

    /// <summary>The registry in MAC order. The list itself is not exposed for mutation.</summary>
    public IReadOnlyList<SensorRecord> Sensors => _sensors;

    /// <summary>
    /// Inserts a sensor at its sorted position, found by binary search.
    /// </summary>
    /// <param name="sensor">The sensor to register.</param>
    /// <returns>False when a sensor with that MAC address is already registered.</returns>
    public bool Add(SensorRecord sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        var position = FindInsertionPoint(sensor.MacAddress);

        // A non-negative result means an exact match already sits there.
        if (position >= 0)
        {
            return false;
        }

        // List.BinarySearch returns the bitwise complement of the insertion index
        // when there is no match, so ~position is where this sensor belongs.
        _sensors.Insert(~position, sensor);
        _byId[sensor.Id] = sensor;
        return true;
    }

    /// <summary>
    /// Finds a sensor by MAC address in O(log n).
    /// </summary>
    /// <param name="macAddress">Address in any accepted form; it is normalised first.</param>
    /// <returns>The sensor, or null when it is not registered.</returns>
    public SensorRecord? FindByMac(string? macAddress)
    {
        LastBinarySearchComparisons = 0;

        if (!MacAddress.TryNormalise(macAddress, out var normalised) || _sensors.Count == 0)
        {
            return null;
        }

        // Written out rather than delegating to List.BinarySearch so the comparison
        // count is observable - the dashboard shows it beside the linear count.
        var low = 0;
        var high = _sensors.Count - 1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            LastBinarySearchComparisons++;

            var order = string.CompareOrdinal(_sensors[middle].MacAddress, normalised);

            if (order == 0)
            {
                return _sensors[middle];
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    /// <summary>True when a sensor with that MAC address is registered.</summary>
    public bool Contains(string? macAddress) => FindByMac(macAddress) is not null;

    /// <summary>
    /// Finds a sensor by its surrogate key in O(1). The list is not ordered by id,
    /// so this goes through the key dictionary rather than either search.
    /// </summary>
    public SensorRecord? FindById(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Free-text search across display name, MAC address, category and every segment
    /// of the deployment location.
    /// <para>
    /// This one is a linear scan by design. The list is ordered by MAC address, and
    /// that ordering says nothing about display names or zones, so there is no
    /// sorted dimension to bisect on. Every record has to be looked at.
    /// </para>
    /// </summary>
    /// <param name="query">Case-insensitive substring. Blank returns everything.</param>
    /// <returns>Matching sensors, in registry order.</returns>
    public List<SensorRecord> Search(string? query)
    {
        LastLinearSearchComparisons = 0;

        if (string.IsNullOrWhiteSpace(query))
        {
            return [.. _sensors];
        }

        var needle = query.Trim();
        var matches = new List<SensorRecord>();

        foreach (var sensor in _sensors)
        {
            LastLinearSearchComparisons++;

            if (Matches(sensor, needle))
            {
                matches.Add(sensor);
            }
        }

        return matches;
    }

    /// <summary>Sensors in one category, in registry order.</summary>
    public List<SensorRecord> ByCategory(SensorCategory category)
        => _sensors.Where(sensor => sensor.Category == category).ToList();

    /// <summary>Distinct zone labels across the registry, alphabetically.</summary>
    public List<string> Zones()
        => _sensors
            .Select(sensor => sensor.Location.Zone)
            .Where(zone => !string.IsNullOrWhiteSpace(zone))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(zone => zone, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Removes a sensor by MAC address.</summary>
    /// <returns>True when a sensor was removed.</returns>
    public bool Remove(string? macAddress)
    {
        var sensor = FindByMac(macAddress);
        if (sensor is null || !_sensors.Remove(sensor))
        {
            return false;
        }

        _byId.Remove(sensor.Id);
        return true;
    }

    /// <summary>Empties the registry.</summary>
    public void Clear()
    {
        _sensors.Clear();
        _byId.Clear();
    }

    /// <summary>
    /// Confirms the list is still in ascending MAC order. Used by the tests to prove
    /// the ordering invariant every insertion depends on actually holds.
    /// </summary>
    public bool IsSorted()
    {
        for (var i = 1; i < _sensors.Count; i++)
        {
            if (string.CompareOrdinal(_sensors[i - 1].MacAddress, _sensors[i].MacAddress) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private int FindInsertionPoint(string macAddress)
        => _sensors.BinarySearch(
            new SensorRecord
            {
                MacAddress = macAddress,
                DisplayName = string.Empty,
                Category = SensorCategory.Environmental,
                Location = new DeploymentLocation(string.Empty, string.Empty, string.Empty, string.Empty),
                Unit = TelemetryUnit.None
            },
            MacComparer.Instance);

    private static bool Matches(SensorRecord sensor, string needle)
        => Contains(sensor.DisplayName, needle)
           || Contains(sensor.MacAddress, needle)
           || Contains(sensor.Category.ToString(), needle)
           || Contains(sensor.Location.Facility, needle)
           || Contains(sensor.Location.Zone, needle)
           || Contains(sensor.Location.SubZone, needle)
           || Contains(sensor.Location.NodeId, needle);

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Orders sensors by MAC address, which is the invariant the index maintains.</summary>
    private sealed class MacComparer : IComparer<SensorRecord>
    {
        public static readonly MacComparer Instance = new();

        public int Compare(SensorRecord? x, SensorRecord? y)
            => string.CompareOrdinal(x?.MacAddress, y?.MacAddress);
    }
}
