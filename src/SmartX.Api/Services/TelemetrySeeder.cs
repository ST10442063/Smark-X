using Microsoft.Extensions.Options;
using SmartX.Api.Models;
using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Populates the gateway with a mock fleet and its telemetry history at startup.
/// <para>
/// The brief asks for the application to be heavily seeded so the data structures
/// can be shown working under load. Seeding also makes the dashboard honest on
/// first open: an empty Anomaly Radar proves nothing, and a reviewer should not
/// have to generate several thousand readings by hand before the ring buffers,
/// the jagged batch store and the detector have anything to say.
/// </para>
/// <para>
/// Three faults are planted deliberately, because each exercises a different part
/// of the system: a power spike (a positive z-score against a strong baseline), a
/// moisture crash (a negative one), and a node that never reports at all - which
/// no amount of scoring would catch, since it is detected by the absence of data
/// rather than by anything in it.
/// </para>
/// </summary>
public sealed class TelemetrySeeder(
    GatewayStore store,
    AnomalyDetectionService detector,
    IOptions<GatewayOptions> options,
    ILogger<TelemetrySeeder> logger) : IHostedService
{
    private readonly GatewayOptions _options = options.Value;

    private static readonly (string Facility, string Zone, string SubZone)[] Sites =
    [
        ("Facility A", "Zone 1", "Sub-Zone A"),
        ("Facility A", "Zone 1", "Sub-Zone B"),
        ("Facility A", "Zone 2", "Sub-Zone A"),
        ("Facility B", "Zone 1", "Sub-Zone A"),
        ("Facility B", "Zone 3", string.Empty),
        ("Facility C", "Zone 1", "Sub-Zone C")
    ];

    private static readonly (SensorCategory Category, string Name)[] Blueprints =
    [
        (SensorCategory.Environmental, "Soil Moisture Probe"),
        (SensorCategory.Environmental, "Canopy Humidity"),
        (SensorCategory.Environmental, "Nutrient EC Probe"),
        (SensorCategory.PowerConsumption, "Pump Feed Meter"),
        (SensorCategory.PowerConsumption, "Lighting Rail Meter"),
        (SensorCategory.Actuator, "Irrigation Valve"),
        (SensorCategory.Actuator, "Circulation Fan Relay"),
        (SensorCategory.Connectivity, "Mesh Repeater")
    ];

    /// <summary>
    /// The actuators the Part 2 command stream switches - the "Pump A" of the brief.
    /// Each shares a zone with a sensor it affects: Pump A irrigates the bay Soil
    /// Moisture Probe 01 sits in, and the grow-light relay feeds Lighting Rail Meter 13.
    /// </summary>
    private static readonly (string Name, string Facility, string Zone, string SubZone, bool StartsOn)[] CommandActuators =
    [
        ("Water Pump A", "Facility A", "Zone 1", "Sub-Zone A", false),
        ("Water Pump B", "Facility A", "Zone 2", "Sub-Zone A", false),
        ("Grow Light Relay", "Facility A", "Zone 1", "Sub-Zone A", true)
    ];

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.SeedOnStartup)
        {
            logger.LogInformation("Seeding is disabled; the gateway is starting empty.");
            return Task.CompletedTask;
        }

        // A fixed seed, so every run produces the same fleet. A demo that shows
        // different numbers each time is much harder to talk through, and a bug
        // that only appears for one random draw is much harder to reproduce.
        var random = new Random(Seed: 7312);
        var sensors = new List<SensorRecord>();

        for (var i = 0; i < _options.SeedSensorCount; i++)
        {
            var blueprint = Blueprints[i % Blueprints.Length];
            var site = Sites[i % Sites.Length];

            var sensor = new SensorRecord
            {
                MacAddress = $"A4:CF:12:{i / 256:X2}:{i % 256:X2}:{(i * 37) % 256:X2}",
                DisplayName = $"{blueprint.Name} {i + 1:00}",
                Category = blueprint.Category,
                Unit = TelemetrySimulator.UnitFor(blueprint.Category),
                Location = new DeploymentLocation(site.Facility, site.Zone, site.SubZone, $"NODE-{i + 1:00}"),
                FirmwareVersion = i % 5 == 0 ? "1.3.9" : "1.4.2",

                // Sixty seconds. The device emulator republishes every few
                // seconds, so this stays comfortably inside the window while a node
                // is healthy - and tightens the window enough that the deliberately
                // silent node, and anything that genuinely stops, is flagged within
                // about two minutes rather than ten.
                HeartbeatIntervalSeconds = 60
            };

            if (store.TryRegister(sensor))
            {
                sensors.Add(sensor);
            }
        }

        // The last node is registered but never publishes, so it is Silent from the
        // moment the gateway starts - the disconnect case.
        var silentNode = sensors[^1];
        var publishing = sensors.Take(sensors.Count - 1).ToList();
        store.MarkSimulated(publishing.Select(sensor => sensor.Id));

        // Part 2's actuators come after the Part 1 fleet is settled, so the silent
        // node and the planted faults stay exactly where the Part 1 walkthrough
        // expects them. They get their own MAC block for the same reason.
        var actuators = RegisterCommandActuators();
        store.MarkSimulated(actuators.Select(pair => pair.Sensor.Id));

        // Faults are planted on a power meter and a moisture probe specifically, so
        // one shows as a spike and the other as a crash.
        var spikeNode = publishing.LastOrDefault(s => s.Category == SensorCategory.PowerConsumption);
        var crashNode = publishing.FirstOrDefault(s => s.Category == SensorCategory.Environmental);

        foreach (var sensor in publishing)
        {
            SeedReadings(sensor, random, plantFault: sensor == spikeNode || sensor == crashNode);
        }

        foreach (var (sensor, startsOn) in actuators)
        {
            SeedFixedState(sensor, startsOn);
        }

        // Score the seeded history so the radar has something on it at first open.
        foreach (var sensor in publishing)
        {
            detector.InspectLatest(sensor, _options.SeedReadingsPerSensor);
        }

        var (batches, readings) = store.FleetHistoryState();
        var (_, openAnomalies) = store.AnomalyCounts();

        logger.LogInformation(
            "Seeded {Sensors} sensors, {Readings} readings across {Batches} batches, " +
            "{Anomalies} anomalies open. {Silent} is deliberately silent.",
            sensors.Count + actuators.Count, readings, batches, openAnomalies, silentNode.MacAddress);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Publishes a sensor history as several batches of differing lengths, which is
    /// what gives the jagged store a genuinely ragged shape to hold rather than a
    /// rectangle in disguise.
    /// </summary>
    private void SeedReadings(SensorRecord sensor, Random random, bool plantFault)
    {
        var payloadType = TelemetrySimulator.PayloadTypeFor(sensor.Category);
        var total = _options.SeedReadingsPerSensor;
        var offset = sensor.Id.GetHashCode() % 10;

        // Spaced a minute apart and ending at the present, so the history spans a
        // plausible window and the newest reading is fresh enough to leave the node
        // Online rather than immediately stale.
        var start = DateTimeOffset.UtcNow.AddMinutes(-(total - 1));
        var produced = 0;

        while (produced < total)
        {
            // Batch sizes vary the way real flush sizes do - a timer flush is small,
            // a reconnect after an outage is large.
            var batchSize = Math.Min(random.Next(20, 160), total - produced);
            var packets = new List<ITelemetryPacket>(batchSize);

            for (var i = 0; i < batchSize; i++)
            {
                var step = produced + i;
                var value = TelemetrySimulator.Next(sensor.Category, step, random, offset);

                // Placed near the end so the readings before it have established the
                // baseline the detector will measure it against.
                if (plantFault && step == total - 3)
                {
                    value = TelemetrySimulator.Distort(sensor.Category, value);
                }

                packets.Add(BuildPacket(sensor, payloadType, value, step, start.AddMinutes(step)));
            }

            store.Accept(sensor.Id, packets);
            produced += batchSize;
        }
    }

    private List<(SensorRecord Sensor, bool StartsOn)> RegisterCommandActuators()
    {
        var registered = new List<(SensorRecord, bool)>();

        for (var i = 0; i < CommandActuators.Length; i++)
        {
            var (name, facility, zone, subZone, startsOn) = CommandActuators[i];

            var sensor = new SensorRecord
            {
                MacAddress = $"A4:CF:12:01:00:{i + 1:X2}",
                DisplayName = name,
                Category = SensorCategory.Actuator,
                Unit = TelemetryUnit.State,
                Location = new DeploymentLocation(facility, zone, subZone, $"ACT-{i + 1:00}"),
                FirmwareVersion = "1.4.2",
                HeartbeatIntervalSeconds = 60
            };

            if (store.TryRegister(sensor))
            {
                registered.Add((sensor, startsOn));
            }
        }

        return registered;
    }

    /// <summary>
    /// A relay history that holds one state - a pump that has been off all morning.
    /// Following the irrigation waveform like the Part 1 valves would leave these in
    /// an arbitrary state at startup, and the walkthrough needs Pump A off.
    /// </summary>
    private void SeedFixedState(SensorRecord sensor, bool on)
    {
        var total = _options.SeedReadingsPerSensor;
        var start = DateTimeOffset.UtcNow.AddMinutes(-(total - 1));
        var packets = new List<ITelemetryPacket>(total);

        for (var step = 0; step < total; step++)
        {
            packets.Add(TelemetryPacket.Create(sensor, on, step, start.AddMinutes(step)));
        }

        store.Accept(sensor.Id, packets);
    }

    /// <summary>
    /// Builds the closed generic for the category - the same discriminator switch
    /// the ingestion endpoints make, so seeded data travels the identical path.
    /// </summary>
    private static ITelemetryPacket BuildPacket(
        SensorRecord sensor,
        string payloadType,
        double value,
        long sequence,
        DateTimeOffset timestamp) => payloadType switch
        {
            "int" => TelemetryPacket.Create(sensor, (int)Math.Round(value), sequence, timestamp),
            "bool" => TelemetryPacket.Create(sensor, value > 0.5d, sequence, timestamp),
            _ => TelemetryPacket.Create(sensor, (float)value, sequence, timestamp)
        };
}
