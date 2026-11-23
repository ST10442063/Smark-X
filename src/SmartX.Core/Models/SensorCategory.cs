namespace SmartX.Core.Models;

/// <summary>
/// The operational class of a Smart-X mesh node. The category drives which
/// telemetry unit a sensor is allowed to publish and how the anomaly detector
/// weights its readings.
/// </summary>
public enum SensorCategory
{
    /// <summary>Soil moisture, air temperature, humidity, CO2 - hydroponics and climate nodes.</summary>
    Environmental = 0,

    /// <summary>Smart meters and plugs reporting wattage, voltage or current draw.</summary>
    PowerConsumption = 1,

    /// <summary>Relays, valves and pumps. Publishes discrete on/off state rather than a scalar.</summary>
    Actuator = 2,

    /// <summary>Mesh repeaters and gateway bridges reporting link quality.</summary>
    Connectivity = 3
}
