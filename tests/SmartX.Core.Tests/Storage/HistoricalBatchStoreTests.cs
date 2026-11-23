using SmartX.Core.Models;
using SmartX.Core.Storage;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Tests.Storage;

/// <summary>
/// Covers the jagged batch store and the rectangular heat grid, including the
/// property that motivates using each layout.
/// </summary>
public class HistoricalBatchStoreTests
{
    private static readonly DateTimeOffset Base = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A capture time <paramref name="second"/> seconds after a fixed base instant.</summary>
    private static long Ms(int second) => Base.AddSeconds(second).ToUnixTimeMilliseconds();

    [Fact]
    public void ANewStore_IsEmpty()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Percent);

        Assert.Equal(0, store.BatchCount);
        Assert.Equal(0, store.TotalReadings);
        Assert.Empty(store.FlattenToList());
    }

    [Fact]
    public void BatchesOfDifferentLengths_EachAllocateExactlyWhatTheyNeed()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Percent);

        store.AppendBatch([1d, 2d, 3d], [Ms(1), Ms(2), Ms(3)]);
        store.AppendBatch([4d], [Ms(4)]);
        store.AppendBatch([5d, 6d, 7d, 8d, 9d], [Ms(5), Ms(6), Ms(7), Ms(8), Ms(9)]);

        Assert.Equal(3, store.BatchCount);
        Assert.Equal(9, store.TotalReadings);
        Assert.Equal([3, 1, 5], store.BatchLengths());
    }

    [Fact]
    public void JaggedSavesWhatARectangularArrayWouldHaveWasted()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Percent);

        // Two small batches and one large one - the shape that makes rectangular
        // storage expensive, since every row would size to the largest.
        store.AppendBatch([1d, 2d], [Ms(1), Ms(2)]);
        store.AppendBatch([3d, 4d], [Ms(3), Ms(4)]);
        store.AppendBatch(Enumerable.Repeat(5d, 100).ToArray(),
                          Enumerable.Range(0, 100).Select(i => Ms(i)).ToArray());

        // Jagged holds 104 doubles; rectangular would have needed 3 x 100 = 300.
        Assert.Equal(104, store.TotalReadings);
        Assert.Equal(300, store.RectangularEquivalentSlots());
    }

    [Fact]
    public void AppendBatch_RejectsMismatchedValueAndTimestampCounts()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Watt);

        var error = Assert.Throws<ArgumentException>(
            () => store.AppendBatch([1d, 2d, 3d], [Ms(1)]));

        Assert.Contains("one timestamp per reading", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOuterArrayDoublesRatherThanGrowingByOne()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Watt);

        for (var i = 0; i < 9; i++)
        {
            store.AppendBatch([i], [Ms(i)]);
        }

        // Nine batches from an initial four slots: 4 -> 8 -> 16.
        Assert.Equal(9, store.BatchCount);
        Assert.Equal(16, store.AllocatedBatchSlots);
    }

    [Fact]
    public void FlattenToList_HandsEveryReadingToAGenericCollectionInOrder()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Percent);
        store.AppendBatch([10d, 20d], [Ms(1), Ms(2)]);
        store.AppendBatch([30d], [Ms(3)]);

        var readings = store.FlattenToList();

        Assert.IsType<List<SensorReading>>(readings);
        Assert.Equal([10d, 20d, 30d], readings.Select(r => r.Value));
        Assert.All(readings, reading => Assert.Equal(TelemetryUnit.Percent, reading.Unit));
    }

    [Fact]
    public void FlattenToList_RestoresTheOriginalTimestamps()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Celsius);
        store.AppendBatch([21.5d], [Ms(7)]);

        var reading = Assert.Single(store.FlattenToList());

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Ms(7)), reading.TimestampUtc);
    }

    [Fact]
    public void AppendBatch_AcceptsPacketsDirectly()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Watt);
        var sensorId = Guid.NewGuid();

        List<ITelemetryPacket> packets =
        [
            TelemetryPacket.Create(sensorId, "A4:CF:12:9B:0E:77", 100, TelemetryUnit.Watt),
            TelemetryPacket.Create(sensorId, "A4:CF:12:9B:0E:77", 220, TelemetryUnit.Watt)
        ];

        store.AppendBatch(packets);

        Assert.Equal(1, store.BatchCount);
        Assert.Equal([100d, 220d], store.FlattenToList().Select(r => r.Value));
    }

    [Fact]
    public void AppendBatch_IgnoresAnEmptyPacketList()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Watt);

        store.AppendBatch([]);

        Assert.Equal(0, store.BatchCount);
    }

    [Fact]
    public void TakeNewest_WalksBatchesBackwardsAndReturnsOldestFirst()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Percent);
        store.AppendBatch([1d, 2d, 3d], [Ms(1), Ms(2), Ms(3)]);
        store.AppendBatch([4d, 5d], [Ms(4), Ms(5)]);

        Assert.Equal([3d, 4d, 5d], store.TakeNewest(3).Select(r => r.Value));
        Assert.Equal([1d, 2d, 3d, 4d, 5d], store.TakeNewest(99).Select(r => r.Value));
        Assert.Empty(store.TakeNewest(0));
    }

    [Fact]
    public void Summarise_ComputesMeanMinAndMaxAcrossEveryBatch()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Watt);
        store.AppendBatch([10d, 20d], [Ms(1), Ms(2)]);
        store.AppendBatch([30d, 40d], [Ms(3), Ms(4)]);

        var (mean, min, max) = store.Summarise();

        Assert.Equal(25d, mean);
        Assert.Equal(10d, min);
        Assert.Equal(40d, max);
    }

    [Fact]
    public void Summarise_OnAnEmptyStore_ReturnsZeroesRatherThanThrowing()
    {
        var (mean, min, max) = new HistoricalBatchStore(TelemetryUnit.Watt).Summarise();

        Assert.Equal(0d, mean);
        Assert.Equal(0d, min);
        Assert.Equal(0d, max);
    }

    [Fact]
    public void GetBatch_ReturnsOneBatchAndRejectsAnIndexOutsideTheRange()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Watt);
        store.AppendBatch([7d, 8d], [Ms(1), Ms(2)]);

        Assert.Equal([7d, 8d], store.GetBatch(0).ToArray());
        Assert.Equal(2, store.GetBatchLength(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetBatch(1).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetBatchLength(-1));
    }

    [Fact]
    public void UnderLoad_ItHoldsEveryReadingAcrossManyVariableBatches()
    {
        var store = new HistoricalBatchStore(TelemetryUnit.Percent);
        var random = new Random(Seed: 7312);
        long expected = 0;

        for (var batch = 0; batch < 500; batch++)
        {
            var length = random.Next(1, 120);
            expected += length;
            store.AppendBatch(
                Enumerable.Repeat(1d, length).ToArray(),
                Enumerable.Repeat(Ms(1), length).ToArray());
        }

        Assert.Equal(500, store.BatchCount);
        Assert.Equal(expected, store.TotalReadings);
        Assert.Equal(expected, store.FlattenToList().Count);
        Assert.True(store.RectangularEquivalentSlots() > store.TotalReadings);
    }
}

/// <summary>
/// Covers the rectangular grid - the layout chosen precisely because its shape
/// never varies, in contrast to the jagged batch store.
/// </summary>
public class TelemetryHeatMatrixTests
{
    private static TelemetryHeatMatrix Grid() => new(["Zone 1", "Zone 2", "Zone 3"]);

    [Fact]
    public void TheGridIsSizedZonesByTwentyFourHours()
    {
        var grid = Grid();

        Assert.Equal(3, grid.ZoneCount);
        Assert.Equal(24, TelemetryHeatMatrix.HoursPerDay);
        Assert.Equal(["Zone 1", "Zone 2", "Zone 3"], grid.Zones);
    }

    [Fact]
    public void DuplicateZoneLabels_CollapseToOneRow()
    {
        var grid = new TelemetryHeatMatrix(["Zone 1", "zone 1", "Zone 2"]);

        Assert.Equal(2, grid.ZoneCount);
    }

    [Fact]
    public void RecordedReadings_AverageWithinTheirCell()
    {
        var grid = Grid();

        grid.Record("Zone 1", 14, 20d);
        grid.Record("Zone 1", 14, 30d);
        grid.Record("Zone 1", 15, 50d);

        Assert.Equal(25d, grid.Mean("Zone 1", 14));
        Assert.Equal(50d, grid.Mean("Zone 1", 15));
        Assert.Equal(3, grid.SamplesRecorded);
    }

    [Fact]
    public void AnEmptyCell_IsNullRatherThanAMisleadingZero()
    {
        var grid = Grid();
        grid.Record("Zone 1", 14, 20d);

        Assert.Null(grid.Mean("Zone 1", 0));
        Assert.Null(grid.Mean("Zone 2", 14));
        Assert.Equal(0, grid.SampleCount(1, 14));
    }

    [Fact]
    public void AReadingForAnUnknownZone_IsIgnoredRatherThanThrowing()
    {
        var grid = Grid();

        Assert.False(grid.Record("Zone 9", 14, 20d));
        Assert.Equal(0, grid.SamplesRecorded);
        Assert.Null(grid.Mean("Zone 9", 14));
    }

    [Fact]
    public void AnHourOutsideTheDay_IsRejected()
    {
        var grid = Grid();

        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Record("Zone 1", 24, 1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Record("Zone 1", -1, 1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Mean(0, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Mean(3, 0));
    }

    [Fact]
    public void ToRows_ShapesTheGridForTheDashboard()
    {
        var grid = Grid();
        grid.Record("Zone 2", 9, 42d);

        var rows = grid.ToRows();

        Assert.Equal(3, rows.Length);
        Assert.All(rows, row => Assert.Equal(24, row.Length));
        Assert.Equal(42d, rows[1][9]);
        Assert.Null(rows[0][9]);
    }

    [Fact]
    public void Range_ScalesToTheDataActuallyPresent()
    {
        var grid = Grid();
        grid.Record("Zone 1", 1, 10d);
        grid.Record("Zone 2", 5, 90d);
        grid.Record("Zone 3", 9, 50d);

        var (min, max) = grid.Range();

        Assert.Equal(10d, min);
        Assert.Equal(90d, max);
    }

    [Fact]
    public void Range_OnAnEmptyGrid_IsNull()
    {
        var (min, max) = Grid().Range();

        Assert.Null(min);
        Assert.Null(max);
    }

    [Fact]
    public void Reset_EmptiesTheCellsButKeepsTheZoneRows()
    {
        var grid = Grid();
        grid.Record("Zone 1", 14, 20d);

        grid.Reset();

        Assert.Equal(0, grid.SamplesRecorded);
        Assert.Null(grid.Mean("Zone 1", 14));
        Assert.Equal(3, grid.ZoneCount);
    }
}
