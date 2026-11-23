using SmartX.Core.Collections;
using SmartX.Core.Models;

namespace SmartX.Core.Tests.Collections;

/// <summary>
/// Covers the registry: that the sort invariant holds through arbitrary insertion
/// order, that the binary search finds what is there and rejects what is not, and
/// that the comparison counts really do show the difference between the two
/// search strategies.
/// </summary>
public class SortedSensorIndexTests
{
    private static SensorRecord Sensor(
        string mac,
        string name = "Node",
        SensorCategory category = SensorCategory.Environmental,
        string facility = "Facility A",
        string zone = "Zone 1",
        string subZone = "Sub-Zone B",
        string nodeId = "NODE-01")
        => new()
        {
            MacAddress = MacAddress.Normalise(mac),
            DisplayName = name,
            Category = category,
            Location = new DeploymentLocation(facility, zone, subZone, nodeId),
            Unit = TelemetryUnit.Percent
        };

    private static SortedSensorIndex IndexOf(params string[] macs)
    {
        var index = new SortedSensorIndex();
        foreach (var mac in macs)
        {
            index.Add(Sensor(mac));
        }

        return index;
    }

    [Fact]
    public void SensorsInsertedOutOfOrder_StillEndUpSorted()
    {
        var index = IndexOf(
            "FF:FF:FF:FF:FF:FF",
            "00:00:00:00:00:01",
            "A4:CF:12:9B:0E:77",
            "0A:00:00:00:00:00");

        Assert.True(index.IsSorted());
        Assert.Equal(
            ["00:00:00:00:00:01", "0A:00:00:00:00:00", "A4:CF:12:9B:0E:77", "FF:FF:FF:FF:FF:FF"],
            index.Sensors.Select(sensor => sensor.MacAddress));
    }

    [Fact]
    public void ARepeatedMacAddress_IsRejected()
    {
        var index = new SortedSensorIndex();

        Assert.True(index.Add(Sensor("A4:CF:12:9B:0E:77")));
        Assert.False(index.Add(Sensor("A4:CF:12:9B:0E:77")));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void ARepeatedMacInADifferentFormat_IsStillRejected()
    {
        var index = new SortedSensorIndex();
        index.Add(Sensor("A4:CF:12:9B:0E:77"));

        // Normalisation at registration is what makes this a duplicate.
        Assert.False(index.Add(Sensor("a4-cf-12-9b-0e-77")));
    }

    [Fact]
    public void FindByMac_LocatesARegisteredSensor()
    {
        var index = IndexOf("00:00:00:00:00:01", "A4:CF:12:9B:0E:77", "FF:FF:FF:FF:FF:FF");

        var found = index.FindByMac("A4:CF:12:9B:0E:77");

        Assert.NotNull(found);
        Assert.Equal("A4:CF:12:9B:0E:77", found.MacAddress);
    }

    [Theory]
    [InlineData("a4:cf:12:9b:0e:77")]
    [InlineData("A4-CF-12-9B-0E-77")]
    [InlineData("a4cf.129b.0e77")]
    [InlineData("A4CF129B0E77")]
    public void FindByMac_AcceptsAnyOfTheFormsFirmwareReports(string candidate)
    {
        var index = IndexOf("A4:CF:12:9B:0E:77");

        Assert.NotNull(index.FindByMac(candidate));
    }

    [Fact]
    public void FindByMac_ReturnsNullForAnUnregisteredOrMalformedAddress()
    {
        var index = IndexOf("A4:CF:12:9B:0E:77");

        Assert.Null(index.FindByMac("11:22:33:44:55:66"));
        Assert.Null(index.FindByMac("not-a-mac"));
        Assert.Null(index.FindByMac(null));
        Assert.Null(index.FindByMac(""));
    }

    [Fact]
    public void FindByMac_OnAnEmptyIndex_ReturnsNull()
    {
        Assert.Null(new SortedSensorIndex().FindByMac("A4:CF:12:9B:0E:77"));
    }

    [Fact]
    public void EveryRegisteredSensor_IsFindable()
    {
        var macs = Enumerable.Range(0, 200)
            .Select(i => $"A4:CF:12:9B:{i / 256:X2}:{i % 256:X2}")
            .ToArray();

        var index = IndexOf(macs);

        Assert.True(index.IsSorted());
        Assert.All(macs, mac => Assert.NotNull(index.FindByMac(mac)));
    }

    [Fact]
    public void BinarySearch_UsesLogarithmicallyFewerComparisonsThanTheLinearScan()
    {
        // 1024 sensors: a binary search should need at most about 11 comparisons,
        // where a full linear scan looks at all 1024.
        var macs = Enumerable.Range(0, 1024)
            .Select(i => $"A4:CF:12:9B:{i / 256:X2}:{i % 256:X2}")
            .ToArray();

        var index = IndexOf(macs);

        index.FindByMac(macs[^1]);
        index.Search("no-such-sensor");

        Assert.True(index.LastBinarySearchComparisons <= 11,
            $"expected at most 11 comparisons, used {index.LastBinarySearchComparisons}");
        Assert.Equal(1024, index.LastLinearSearchComparisons);
    }

    [Fact]
    public void AFailedBinarySearch_StillOnlyCostsLogarithmicComparisons()
    {
        var index = IndexOf([.. Enumerable.Range(0, 512).Select(i => $"A4:CF:12:9B:{i / 256:X2}:{i % 256:X2}")]);

        index.FindByMac("FF:FF:FF:FF:FF:FF");

        Assert.True(index.LastBinarySearchComparisons <= 10);
    }

    [Fact]
    public void Search_MatchesAcrossNameMacCategoryAndEverySegmentOfTheLocation()
    {
        var index = new SortedSensorIndex();
        index.Add(Sensor("00:00:00:00:00:01", name: "Hydro Bay 3 Moisture",
            category: SensorCategory.Environmental, zone: "Zone 1", nodeId: "NODE-07"));
        index.Add(Sensor("00:00:00:00:00:02", name: "Main Feed Meter",
            category: SensorCategory.PowerConsumption, zone: "Zone 4", nodeId: "NODE-11"));

        Assert.Single(index.Search("hydro"));
        Assert.Single(index.Search("NODE-11"));
        Assert.Single(index.Search("PowerConsumption"));
        Assert.Single(index.Search("Zone 4"));
        Assert.Single(index.Search("00:00:00:00:00:02"));
        Assert.Equal(2, index.Search("Facility A").Count);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var index = new SortedSensorIndex();
        index.Add(Sensor("00:00:00:00:00:01", name: "Hydro Bay 3"));

        Assert.Single(index.Search("HYDRO"));
        Assert.Single(index.Search("hydro bay"));
    }

    [Fact]
    public void Search_WithABlankQuery_ReturnsEverything()
    {
        var index = IndexOf("00:00:00:00:00:01", "00:00:00:00:00:02");

        Assert.Equal(2, index.Search(null).Count);
        Assert.Equal(2, index.Search("   ").Count);
    }

    [Fact]
    public void Search_ReturnsAnEmptyListWhenNothingMatches()
    {
        var index = IndexOf("00:00:00:00:00:01");

        Assert.Empty(index.Search("nothing-like-this"));
    }

    [Fact]
    public void FindById_LocatesASensorByItsSurrogateKey()
    {
        var sensor = Sensor("A4:CF:12:9B:0E:77");
        var index = new SortedSensorIndex();
        index.Add(sensor);

        Assert.Same(sensor, index.FindById(sensor.Id));
        Assert.Null(index.FindById(Guid.NewGuid()));
    }

    [Fact]
    public void FindById_ForgetsARemovedSensor()
    {
        var sensor = Sensor("A4:CF:12:9B:0E:77");
        var index = new SortedSensorIndex();
        index.Add(sensor);

        index.Remove(sensor.MacAddress);
        Assert.Null(index.FindById(sensor.Id));

        index.Add(sensor);
        index.Clear();
        Assert.Null(index.FindById(sensor.Id));
    }

    [Fact]
    public void ByCategory_FiltersTheRegistry()
    {
        var index = new SortedSensorIndex();
        index.Add(Sensor("00:00:00:00:00:01", category: SensorCategory.Environmental));
        index.Add(Sensor("00:00:00:00:00:02", category: SensorCategory.PowerConsumption));
        index.Add(Sensor("00:00:00:00:00:03", category: SensorCategory.PowerConsumption));

        Assert.Equal(2, index.ByCategory(SensorCategory.PowerConsumption).Count);
        Assert.Empty(index.ByCategory(SensorCategory.Actuator));
    }

    [Fact]
    public void Zones_ReturnsDistinctLabelsAlphabetically()
    {
        var index = new SortedSensorIndex();
        index.Add(Sensor("00:00:00:00:00:01", zone: "Zone 3"));
        index.Add(Sensor("00:00:00:00:00:02", zone: "Zone 1"));
        index.Add(Sensor("00:00:00:00:00:03", zone: "Zone 1"));

        Assert.Equal(["Zone 1", "Zone 3"], index.Zones());
    }

    [Fact]
    public void Remove_TakesASensorOutAndKeepsTheOrdering()
    {
        var index = IndexOf("00:00:00:00:00:01", "A4:CF:12:9B:0E:77", "FF:FF:FF:FF:FF:FF");

        Assert.True(index.Remove("A4:CF:12:9B:0E:77"));
        Assert.False(index.Remove("A4:CF:12:9B:0E:77"));
        Assert.Equal(2, index.Count);
        Assert.True(index.IsSorted());
    }

    [Fact]
    public void Clear_EmptiesTheRegistry()
    {
        var index = IndexOf("00:00:00:00:00:01", "00:00:00:00:00:02");

        index.Clear();

        Assert.Equal(0, index.Count);
        Assert.Empty(index.Sensors);
    }
}

/// <summary>Covers MAC normalisation, which the sort invariant depends on.</summary>
public class MacAddressTests
{
    [Theory]
    [InlineData("A4:CF:12:9B:0E:77")]
    [InlineData("a4:cf:12:9b:0e:77")]
    [InlineData("A4-CF-12-9B-0E-77")]
    [InlineData("a4cf.129b.0e77")]
    [InlineData("A4CF129B0E77")]
    [InlineData("  a4cf129b0e77  ")]
    public void EveryAcceptedForm_NormalisesToTheCanonicalOne(string candidate)
    {
        Assert.True(MacAddress.TryNormalise(candidate, out var normalised));
        Assert.Equal("A4:CF:12:9B:0E:77", normalised);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-mac")]
    [InlineData("A4:CF:12:9B:0E")]
    [InlineData("A4:CF:12:9B:0E:77:88")]
    [InlineData("G4:CF:12:9B:0E:77")]
    public void AMalformedAddress_IsRejected(string? candidate)
    {
        Assert.False(MacAddress.TryNormalise(candidate, out var normalised));
        Assert.Equal(string.Empty, normalised);
        Assert.False(MacAddress.IsValid(candidate));
    }

    [Fact]
    public void Normalise_ThrowsForAMalformedAddress()
    {
        var error = Assert.Throws<FormatException>(() => MacAddress.Normalise("nope"));

        Assert.Contains(MacAddress.ExampleFormat, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VendorPrefix_IsTheFirstThreeOctets()
    {
        Assert.Equal("A4:CF:12", MacAddress.VendorPrefix("A4:CF:12:9B:0E:77"));
    }
}
