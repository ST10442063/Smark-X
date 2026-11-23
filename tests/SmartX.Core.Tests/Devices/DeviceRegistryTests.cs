using SmartX.Core.Devices;
using SmartX.Core.Models;

namespace SmartX.Core.Tests.Devices;

/// <summary>
/// Covers the live device registry: O(1) lookup by MAC and by sensor key, and
/// filters built from set intersections rather than fleet scans.
/// </summary>
public class DeviceRegistryTests
{
    [Fact]
    public void ADevice_IsFoundByMacAndBySensorKey()
    {
        var registry = TestFleet.Registry(out var fleet);

        Assert.True(registry.TryGet(MacKey.Parse("a4cf.1200.0003"), out var byMac));
        Assert.Same(fleet.PumpA, byMac);

        Assert.True(registry.TryGet(fleet.PumpA.SensorId, out var byId));
        Assert.Same(fleet.PumpA, byId);

        Assert.False(registry.TryGet(MacKey.Parse("A4:CF:12:FF:FF:FF"), out _));
    }

    [Fact]
    public void ADuplicateMac_IsRefused()
    {
        var registry = TestFleet.Registry(out _);
        var clone = TestFleet.Device("A4:CF:12:00:00:03", "Impostor", SensorCategory.Actuator);

        Assert.False(registry.TryAdd(clone));
        Assert.Equal(5, registry.Count);
    }

    [Fact]
    public void ACategoryFilter_ReadsOnlyThatCategorysSet()
    {
        var registry = TestFleet.Registry(out _);

        var actuators = registry.Query(new DeviceQuery(SensorCategory.Actuator), out var cost);

        Assert.Equal(["Water Pump A", "Water Pump B"], actuators.Select(device => device.DisplayName));
        Assert.Equal(5, cost.FleetSize);
        Assert.Equal(2, cost.CandidatesExamined);
    }

    [Fact]
    public void CategoryAndAlertState_AreIntersected()
    {
        var registry = TestFleet.Registry(out var fleet);
        var alerting = new HashSet<MacKey> { fleet.Probe.Mac, fleet.PumpB.Mac };

        var result = registry.Query(new DeviceQuery(SensorCategory.Actuator, alerting), out var cost);

        Assert.Same(fleet.PumpB, Assert.Single(result));
        Assert.Equal(1, cost.CandidatesExamined);
    }

    [Fact]
    public void TheTextMatch_CoversNamePathAndAddress()
    {
        var registry = TestFleet.Registry(out var fleet);

        Assert.Equal(3, registry.Query(new DeviceQuery(Text: "pump"), out _).Count);
        Assert.Equal(2, registry.Query(new DeviceQuery(Text: "WATER PUMP"), out _).Count);
        Assert.Same(fleet.PumpB, Assert.Single(registry.Query(new DeviceQuery(Text: "zone 2"), out _)));
        Assert.Same(fleet.Repeater, Assert.Single(registry.Query(new DeviceQuery(Text: "00:00:05"), out _)));
    }

    [Fact]
    public void ARestrictionToKeysTheRegistryDoesNotHold_MatchesNothing()
    {
        var registry = TestFleet.Registry(out _);
        var stale = new HashSet<MacKey> { MacKey.Parse("A4:CF:12:77:77:77") };

        Assert.Empty(registry.Query(new DeviceQuery(RestrictTo: stale), out _));
    }

    [Fact]
    public void CategoryCounts_ReflectTheIndex()
    {
        var registry = TestFleet.Registry(out _);
        var counts = registry.CategoryCounts();

        Assert.Equal(2, counts[SensorCategory.Actuator]);
        Assert.Equal(1, counts[SensorCategory.Environmental]);
    }
}
