using SmartX.Core.Collections;

namespace SmartX.Core.Tests.Collections;

/// <summary>
/// Covers the ring buffer. The wrap-around is the part worth testing hardest:
/// every index translation has to keep working once the head has passed the end
/// of the backing array, and enumeration has to stay oldest-first regardless.
/// </summary>
public class IngestionBufferTests
{
    [Fact]
    public void CapacityBelowOne_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestionBuffer<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestionBuffer<int>(-5));
    }

    [Fact]
    public void ANewBuffer_IsEmpty()
    {
        var buffer = new IngestionBuffer<int>(4);

        Assert.Empty(buffer);
        Assert.False(buffer.IsFull);
        Assert.Equal(4, buffer.Capacity);
        Assert.Equal(0, buffer.TotalAccepted);
        Assert.Equal(0, buffer.OverwrittenCount);
    }

    [Fact]
    public void BelowCapacity_ItBehavesLikeAnOrdinaryAppendOnlyList()
    {
        var buffer = new IngestionBuffer<int>(5);
        buffer.AddRange([1, 2, 3]);

        Assert.Equal(3, buffer.Count);
        Assert.False(buffer.IsFull);
        Assert.Equal([1, 2, 3], buffer);
        Assert.Equal(1, buffer.Oldest);
        Assert.Equal(3, buffer.Newest);
    }

    [Fact]
    public void AtCapacity_ItReportsFull()
    {
        var buffer = new IngestionBuffer<int>(3);
        buffer.AddRange([1, 2, 3]);

        Assert.True(buffer.IsFull);
        Assert.Equal(3, buffer.Count);
        Assert.Equal(0, buffer.OverwrittenCount);
    }

    [Fact]
    public void PastCapacity_TheOldestElementIsOverwritten()
    {
        var buffer = new IngestionBuffer<int>(3);
        buffer.AddRange([1, 2, 3, 4]);

        // 1 has been overwritten by 4; the newest three survive, oldest first.
        Assert.Equal([2, 3, 4], buffer);
        Assert.Equal(3, buffer.Count);
        Assert.Equal(4, buffer.TotalAccepted);
        Assert.Equal(1, buffer.OverwrittenCount);
    }

    [Fact]
    public void AfterManyWraps_EnumerationIsStillOldestFirst()
    {
        var buffer = new IngestionBuffer<int>(4);

        // Ten full laps of a four-slot buffer, so the head lands mid-array.
        buffer.AddRange(Enumerable.Range(1, 42));

        Assert.Equal([39, 40, 41, 42], buffer);
        Assert.Equal(39, buffer.Oldest);
        Assert.Equal(42, buffer.Newest);
        Assert.Equal(42, buffer.TotalAccepted);
        Assert.Equal(38, buffer.OverwrittenCount);
    }

    [Fact]
    public void Indexer_CountsFromTheOldestEvenAfterAWrap()
    {
        var buffer = new IngestionBuffer<string>(3);
        buffer.AddRange(["a", "b", "c", "d", "e"]);

        Assert.Equal("c", buffer[0]);
        Assert.Equal("d", buffer[1]);
        Assert.Equal("e", buffer[2]);
    }

    [Fact]
    public void Indexer_RejectsAnIndexOutsideTheLiveRange()
    {
        var buffer = new IngestionBuffer<int>(4);
        buffer.AddRange([1, 2]);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[2]);
    }

    [Fact]
    public void TakeNewest_ReturnsTheTailOldestFirst()
    {
        var buffer = new IngestionBuffer<int>(5);
        buffer.AddRange([1, 2, 3, 4, 5, 6, 7]);

        Assert.Equal([5, 6, 7], buffer.TakeNewest(3));
    }

    [Fact]
    public void TakeNewest_AskingForMoreThanAreHeldReturnsEverything()
    {
        var buffer = new IngestionBuffer<int>(5);
        buffer.AddRange([1, 2]);

        Assert.Equal([1, 2], buffer.TakeNewest(50));
        Assert.Empty(buffer.TakeNewest(0));
    }

    [Fact]
    public void Drain_HandsTheContentsToAListAndEmptiesTheBuffer()
    {
        var buffer = new IngestionBuffer<int>(4);
        buffer.AddRange([1, 2, 3, 4, 5]);

        var drained = buffer.Drain();

        Assert.IsType<List<int>>(drained);
        Assert.Equal([2, 3, 4, 5], drained);
        Assert.Empty(buffer);
        Assert.False(buffer.IsFull);
    }

    [Fact]
    public void Drain_KeepsTheLifetimeCountersBecauseTheyDescribeThePipelineNotTheContents()
    {
        var buffer = new IngestionBuffer<int>(2);
        buffer.AddRange([1, 2, 3]);

        buffer.Drain();

        Assert.Equal(3, buffer.TotalAccepted);
        Assert.Equal(1, buffer.OverwrittenCount);
    }

    [Fact]
    public void AfterDraining_TheBufferKeepsWorkingFromAFreshHead()
    {
        var buffer = new IngestionBuffer<int>(3);
        buffer.AddRange([1, 2, 3, 4]);
        buffer.Drain();

        buffer.AddRange([9, 8]);

        Assert.Equal([9, 8], buffer);
        Assert.Equal(9, buffer.Oldest);
        Assert.Equal(8, buffer.Newest);
    }

    [Fact]
    public void Contains_FindsALiveElementAndNotAnOverwrittenOne()
    {
        var buffer = new IngestionBuffer<int>(3);
        buffer.AddRange([1, 2, 3, 4]);

        // Assert.Contains is suppressed here on purpose: it would enumerate the
        // buffer itself, and the method under test is the buffer's own Contains.
#pragma warning disable xUnit2017
        Assert.True(buffer.Contains(4));
        Assert.True(buffer.Contains(2));
        Assert.False(buffer.Contains(1));
#pragma warning restore xUnit2017
    }

    [Fact]
    public void CopyTo_WritesTheLiveElementsOldestFirst()
    {
        var buffer = new IngestionBuffer<int>(3);
        buffer.AddRange([1, 2, 3, 4]);

        var destination = new int[5];
        buffer.CopyTo(destination, 1);

        Assert.Equal([0, 2, 3, 4, 0], destination);
    }

    [Fact]
    public void CopyTo_RejectsADestinationWithoutRoom()
    {
        var buffer = new IngestionBuffer<int>(4);
        buffer.AddRange([1, 2, 3, 4]);

        Assert.Throws<ArgumentException>(() => buffer.CopyTo(new int[3], 0));
        Assert.Throws<ArgumentException>(() => buffer.CopyTo(new int[4], 2));
        Assert.Throws<ArgumentNullException>(() => buffer.CopyTo(null!, 0));
    }

    [Fact]
    public void Remove_IsRefusedBecauseItWouldBreakTheWrapArithmetic()
    {
        var buffer = new IngestionBuffer<int>(3);
        buffer.Add(1);

        var error = Assert.Throws<NotSupportedException>(() => buffer.Remove(1));
        Assert.Contains("Drain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_ReleasesReferencesSoTheyDoNotStayPinned()
    {
        var buffer = new IngestionBuffer<string>(2);
        buffer.AddRange(["first", "second"]);

        buffer.Clear();

        Assert.Empty(buffer);
        Assert.Null(buffer.Oldest);
        Assert.Null(buffer.Newest);
    }

    [Fact]
    public void OnAnEmptyBuffer_OldestAndNewestAreDefault()
    {
        var buffer = new IngestionBuffer<int>(3);

        Assert.Equal(0, buffer.Oldest);
        Assert.Equal(0, buffer.Newest);
    }

    [Fact]
    public void ACapacityOfOne_AlwaysHoldsOnlyTheLatestReading()
    {
        var buffer = new IngestionBuffer<int>(1);
        buffer.AddRange([1, 2, 3]);

        Assert.Single(buffer);
        Assert.Equal(3, buffer.Newest);
        Assert.Equal(3, buffer.Oldest);
        Assert.Equal(2, buffer.OverwrittenCount);
    }

    [Fact]
    public void UnderSustainedLoad_ItNeverGrowsBeyondItsCapacity()
    {
        // The property the whole design exists for: 100k packets through a
        // 512-slot buffer must leave exactly 512 live elements.
        var buffer = new IngestionBuffer<int>(512);
        buffer.AddRange(Enumerable.Range(1, 100_000));

        Assert.Equal(512, buffer.Count);
        Assert.Equal(512, buffer.Capacity);
        Assert.Equal(100_000, buffer.TotalAccepted);
        Assert.Equal(100_000 - 512, buffer.OverwrittenCount);
        Assert.Equal(100_000, buffer.Newest);
        Assert.Equal(100_000 - 511, buffer.Oldest);
    }

    [Fact]
    public void ToString_ReportsOccupancyAndBackPressure()
    {
        var buffer = new IngestionBuffer<int>(2);
        buffer.AddRange([1, 2, 3]);

        var description = buffer.ToString();

        Assert.Contains("2/2", description, StringComparison.Ordinal);
        Assert.Contains("accepted 3", description, StringComparison.Ordinal);
        Assert.Contains("overwritten 1", description, StringComparison.Ordinal);
    }
}
