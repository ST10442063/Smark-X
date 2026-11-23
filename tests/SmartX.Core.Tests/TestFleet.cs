using SmartX.Core.Devices;
using SmartX.Core.Models;

namespace SmartX.Core.Tests;

/// <summary>Builds small device fleets for the Part 2 tests.</summary>
internal static class TestFleet
{
    public static SensorRecord Sensor(
        string mac,
        string name,
        SensorCategory category,
        TelemetryUnit? unit = null,
        string zone = "Zone 1") => new()
    {
        MacAddress = MacAddress.Normalise(mac),
        DisplayName = name,
        Category = category,
        Unit = unit ?? category switch
        {
            SensorCategory.PowerConsumption => TelemetryUnit.Watt,
            SensorCategory.Actuator => TelemetryUnit.State,
            SensorCategory.Connectivity => TelemetryUnit.Dbm,
            _ => TelemetryUnit.Percent
        },
        Location = new DeploymentLocation("Facility A", zone, "Sub-Zone B", "NODE-01"),
        HeartbeatIntervalSeconds = 60
    };

    public static DeviceState Device(
        string mac,
        string name,
        SensorCategory category,
        TelemetryUnit? unit = null,
        string zone = "Zone 1")
        => new(MacKey.Parse(mac), Sensor(mac, name, category, unit, zone));

    /// <summary>A registry holding a moisture probe, a power meter, two pumps and a repeater.</summary>
    public static DeviceRegistry Registry(out Fleet fleet)
    {
        fleet = new Fleet(
            Device("A4:CF:12:00:00:01", "Soil Moisture Probe B", SensorCategory.Environmental),
            Device("A4:CF:12:00:00:02", "Pump Feed Meter", SensorCategory.PowerConsumption),
            Device("A4:CF:12:00:00:03", "Water Pump A", SensorCategory.Actuator),
            Device("A4:CF:12:00:00:04", "Water Pump B", SensorCategory.Actuator, zone: "Zone 2"),
            Device("A4:CF:12:00:00:05", "Mesh Repeater", SensorCategory.Connectivity));

        var registry = new DeviceRegistry();
        foreach (var device in fleet.All)
        {
            registry.TryAdd(device);
        }

        return registry;
    }

    internal sealed record Fleet(
        DeviceState Probe,
        DeviceState Meter,
        DeviceState PumpA,
        DeviceState PumpB,
        DeviceState Repeater)
    {
        public IEnumerable<DeviceState> All => [Probe, Meter, PumpA, PumpB, Repeater];
    }
}
