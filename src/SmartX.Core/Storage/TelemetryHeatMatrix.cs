namespace SmartX.Core.Storage;

/// <summary>
/// A zone-by-hour aggregate grid backing the dashboard heat map: each cell holds
/// the mean reading for one zone during one hour of the day.
/// <para>
/// <b>Why rectangular here, when the batch store is jagged.</b> The two layouts
/// answer different shapes of data. Batches vary in length, so a jagged array
/// sizes each row to its own batch and wastes nothing. This grid does not vary:
/// every zone has exactly 24 hours, always. A true two-dimensional
/// <c>double[zones, 24]</c> allocates that as one contiguous block with one object
/// header, rather than the one-array-per-row plus a row of references that jagged
/// would cost - and indexing is a single multiply-and-add instead of a pointer
/// hop per row.
/// </para>
/// <para>
/// Sums and counts are tracked alongside the means so a new reading updates a cell
/// in O(1) without replaying the hour.
/// </para>
/// </summary>
public sealed class TelemetryHeatMatrix
{
    /// <summary>Columns in the grid - one per hour of the day.</summary>
    public const int HoursPerDay = 24;

    private readonly double[,] _sums;
    private readonly int[,] _counts;
    private readonly string[] _zones;
    private readonly Dictionary<string, int> _zoneIndex;

    /// <summary>Creates a grid covering the supplied zones.</summary>
    /// <param name="zones">Zone labels, in the order they should appear.</param>
    public TelemetryHeatMatrix(IEnumerable<string> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);

        _zones = zones.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _zoneIndex = _zones
            .Select((zone, index) => (zone, index))
            .ToDictionary(pair => pair.zone, pair => pair.index, StringComparer.OrdinalIgnoreCase);

        // One contiguous block, rows * 24 doubles, allocated once.
        _sums = new double[_zones.Length, HoursPerDay];
        _counts = new int[_zones.Length, HoursPerDay];
    }

    /// <summary>Zone labels, in row order.</summary>
    public IReadOnlyList<string> Zones => _zones;

    /// <summary>Number of rows in the grid.</summary>
    public int ZoneCount => _zones.Length;

    /// <summary>Total readings folded into the grid.</summary>
    public long SamplesRecorded { get; private set; }

    /// <summary>
    /// Folds one reading into its cell in O(1). Readings for a zone the grid does
    /// not cover are ignored rather than throwing, since a sensor may be registered
    /// into a new zone between grid rebuilds.
    /// </summary>
    /// <param name="zone">Zone the reading came from.</param>
    /// <param name="hourOfDay">Hour of capture, 0-23.</param>
    /// <param name="value">The reading.</param>
    /// <returns>True when the reading was folded in.</returns>
    public bool Record(string zone, int hourOfDay, double value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hourOfDay);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(hourOfDay, HoursPerDay);

        if (zone is null || !_zoneIndex.TryGetValue(zone, out var row))
        {
            return false;
        }

        _sums[row, hourOfDay] += value;
        _counts[row, hourOfDay]++;
        SamplesRecorded++;
        return true;
    }

    /// <summary>
    /// Mean for one cell, or null when no reading has landed in that hour - which
    /// the dashboard renders as an empty cell rather than as a misleading zero.
    /// </summary>
    public double? Mean(int zoneRow, int hourOfDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zoneRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(zoneRow, _zones.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(hourOfDay);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(hourOfDay, HoursPerDay);

        var count = _counts[zoneRow, hourOfDay];
        return count == 0 ? null : _sums[zoneRow, hourOfDay] / count;
    }

    /// <summary>Mean for one cell, addressed by zone label.</summary>
    public double? Mean(string zone, int hourOfDay)
        => _zoneIndex.TryGetValue(zone, out var row) ? Mean(row, hourOfDay) : null;

    /// <summary>How many readings landed in one cell.</summary>
    public int SampleCount(int zoneRow, int hourOfDay) => _counts[zoneRow, hourOfDay];

    /// <summary>
    /// The whole grid as means, with nulls for empty cells. Shaped for JSON, where a
    /// jagged array of rows is what the dashboard expects to consume.
    /// </summary>
    public double?[][] ToRows()
    {
        var rows = new double?[_zones.Length][];

        for (var row = 0; row < _zones.Length; row++)
        {
            rows[row] = new double?[HoursPerDay];
            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                rows[row][hour] = Mean(row, hour);
            }
        }

        return rows;
    }

    /// <summary>
    /// Highest and lowest cell means across the whole grid, so the dashboard can
    /// scale its colour ramp to the data actually present. Returns nulls when the
    /// grid holds no readings at all.
    /// </summary>
    public (double? Min, double? Max) Range()
    {
        double? min = null;
        double? max = null;

        for (var row = 0; row < _zones.Length; row++)
        {
            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                if (Mean(row, hour) is not { } mean)
                {
                    continue;
                }

                min = min is null ? mean : Math.Min(min.Value, mean);
                max = max is null ? mean : Math.Max(max.Value, mean);
            }
        }

        return (min, max);
    }

    /// <summary>Empties every cell, keeping the zone rows.</summary>
    public void Reset()
    {
        Array.Clear(_sums);
        Array.Clear(_counts);
        SamplesRecorded = 0;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"TelemetryHeatMatrix {_zones.Length}x{HoursPerDay}, {SamplesRecorded} samples";
}
