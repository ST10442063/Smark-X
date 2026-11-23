using Microsoft.Extensions.Options;
using SmartX.Api.Models;
using SmartX.Contracts.Dashboard;
using SmartX.Core.Models;
using SmartX.Core.Storage;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Builds the Anomaly Radar feed: the heartbeat strips, the composite gateway
/// health score with its badges, and the fleet statistics panel.
/// <para>
/// This is the engagement layer the brief asks for, and the shape of it is chosen
/// to answer one question an operator actually has - <i>which node should I look at
/// first, and is anything wrong that I have not noticed?</i> A progress bar answers
/// neither. The score gives the whole mesh a single number that moves when
/// something degrades; the strips show the shape of each signal so a spike and a
/// disconnect are visually distinct; the badges give a reason to drive the score
/// back up rather than leaving alerts open.
/// </para>
/// </summary>
public sealed class DashboardService(
    GatewayStore store,
    AnomalyDetectionService detector,
    IOptions<GatewayOptions> options)
{
    private readonly GatewayOptions _options = options.Value;

    /// <summary>How many recent readings each heartbeat strip carries.</summary>
    public const int StripLength = 40;

    /// <summary>
    /// One strip per sensor, ordered so the nodes needing attention float to the
    /// top - silent first, then most open anomalies, then everything else.
    /// </summary>
    public List<HeartbeatStripResponse> HeartbeatStrips()
    {
        var now = DateTimeOffset.UtcNow;

        return store.AllSensors()
            .Select(sensor => BuildStrip(sensor, now))
            .OrderByDescending(strip => strip.Status == nameof(SensorStatus.Silent))
            .ThenByDescending(strip => strip.OpenAnomalies)
            .ThenBy(strip => strip.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private HeartbeatStripResponse BuildStrip(SensorRecord sensor, DateTimeOffset now)
    {
        var readings = store.RecentReadings(sensor.Id, StripLength);
        var scored = detector.Score(readings);

        // Normalised to 0-1 against this sensor own range, so a strip is readable
        // whatever the unit - a -58 dBm link and a 46% moisture probe both render
        // as a legible shape rather than one flattening the other.
        var values = readings.Select(reading => reading.Value).ToList();
        var min = values.Count > 0 ? values.Min() : 0d;
        var max = values.Count > 0 ? values.Max() : 0d;
        var span = max - min;

        var points = values
            .Select(value => span <= double.Epsilon ? 0.5d : (value - min) / span)
            .ToList();

        return new HeartbeatStripResponse
        {
            SensorId = sensor.Id,
            MacAddress = sensor.MacAddress,
            DisplayName = sensor.DisplayName,
            QualifiedPath = sensor.Location.QualifiedPath,
            Category = sensor.Category.ToString(),
            Status = sensor.EvaluateStatus(now).ToString(),
            Points = points,
            ZScores = scored.Select(item => item.ZScore).ToList(),
            LatestReading = readings.Count > 0 ? readings[^1].ToString() : "no data",
            SecondsSinceLastSeen = sensor.LastSeenUtc is { } seen ? (now - seen).TotalSeconds : null,
            HeartbeatIntervalSeconds = sensor.HeartbeatIntervalSeconds,
            OpenAnomalies = store.OpenAnomaliesFor(sensor.Id)
        };
    }

    /// <summary>
    /// The composite health score, its tier, and the badges earned against it.
    /// </summary>
    public GatewayHealthResponse Health()
    {
        var now = DateTimeOffset.UtcNow;
        var sensors = store.AllSensors();
        var scoring = sensors.Where(sensor => sensor.Status != SensorStatus.Decommissioned).ToList();

        var silent = scoring.Count(sensor => sensor.EvaluateStatus(now) == SensorStatus.Silent);
        var degraded = scoring.Count(sensor => sensor.EvaluateStatus(now) == SensorStatus.Degraded);
        var (totalAnomalies, openAnomalies) = store.AnomalyCounts();

        var uptimeRatio = scoring.Count == 0
            ? 1d
            : (scoring.Count - silent - (degraded * 0.5d)) / scoring.Count;

        var anomalyRatio = store.TotalReadingsAccepted == 0
            ? 0d
            : Math.Min(1d, (double)totalAnomalies / store.TotalReadingsAccepted * 20d);

        // Three contributions rather than one: a mesh with every node up but alerts
        // piling up unread is not healthy, and neither is a quiet one with a third
        // of its nodes dark. Weighting uptime heaviest reflects that a node which
        // has stopped reporting cannot report anything worse.
        var score = (int)Math.Round(Math.Clamp(
            (uptimeRatio * 60d)
            + ((1d - anomalyRatio) * 25d)
            + (openAnomalies == 0 ? 15d : Math.Max(0d, 15d - (openAnomalies * 1.5d))),
            0d, 100d));

        var tier = score switch
        {
            >= 90 => "Healthy",
            >= 70 => "Watch",
            >= 45 => "Degraded",
            _ => "Critical"
        };

        // A single dark node out of fourteen barely moves a weighted average, so the
        // arithmetic alone will happily rate a mesh with a dead sensor as Healthy -
        // directly contradicting the headline sitting beside it. A node that is not
        // reporting is not a rounding error, so the tier is capped while any node is
        // silent. The score still shows how close to healthy the rest of the mesh is.
        if (silent > 0 && tier == "Healthy")
        {
            tier = "Watch";
        }

        return new GatewayHealthResponse
        {
            Score = score,
            Tier = tier,
            UptimeRatio = Math.Round(uptimeRatio, 3),
            AnomalyRatio = Math.Round(anomalyRatio, 3),
            SilentNodes = silent,
            OpenAnomalies = openAnomalies,
            CleanIngestStreak = store.CleanIngestStreak,
            Badges = Badges(silent, openAnomalies, scoring.Count),
            Headline = Headline(silent, openAnomalies, degraded, tier)
        };
    }

    private List<BadgeResponse> Badges(int silent, int openAnomalies, int nodeCount)
    {
        var streak = store.CleanIngestStreak;
        var attachments = store.AttachmentCount;

        return
        [
            new BadgeResponse
            {
                Name = "Mesh Guardian",
                Description = "Every registered node reporting inside its heartbeat window.",
                Earned = nodeCount > 0 && silent == 0,
                Progress = nodeCount == 0 ? 0d : Math.Clamp((nodeCount - silent) / (double)nodeCount, 0d, 1d),
                Icon = "\U0001F6E1️"
            },
            new BadgeResponse
            {
                Name = "Inbox Zero",
                Description = "No anomalies left unacknowledged.",
                Earned = openAnomalies == 0,
                Progress = openAnomalies == 0 ? 1d : Math.Clamp(1d - (openAnomalies / 20d), 0d, 0.95d),
                Icon = "✅"
            },
            new BadgeResponse
            {
                Name = "Clean Ingest x1000",
                Description = "1000 consecutive readings accepted without an anomaly.",
                Earned = streak >= 1000,
                Progress = Math.Clamp(streak / 1000d, 0d, 1d),
                Icon = "\U0001F4C8"
            },
            new BadgeResponse
            {
                Name = "Field Documented",
                Description = "Ten configuration files or field photos attached across the fleet.",
                Earned = attachments >= 10,
                Progress = Math.Clamp(attachments / 10d, 0d, 1d),
                Icon = "\U0001F4CE"
            }
        ];
    }

    private static string Headline(int silent, int openAnomalies, int degraded, string tier)
    {
        if (silent > 0)
        {
            return $"{silent} node{(silent == 1 ? " has" : "s have")} stopped reporting. " +
                   "Check power and mesh range before anything else.";
        }

        if (openAnomalies > 0)
        {
            return $"{openAnomalies} anomal{(openAnomalies == 1 ? "y is" : "ies are")} waiting on review.";
        }

        if (degraded > 0)
        {
            return $"{degraded} node{(degraded == 1 ? " is" : "s are")} a heartbeat behind - " +
                   "usually a sign of mesh congestion rather than a fault.";
        }

        return tier == "Healthy"
            ? "Every node is reporting on schedule and nothing is outside its normal band."
            : "No open alerts, but the mesh has not been quiet long enough to rate as healthy.";
    }

    /// <summary>Fleet-wide counters for the statistics panel.</summary>
    public DashboardStatsResponse Stats()
    {
        var now = DateTimeOffset.UtcNow;
        var sensors = store.AllSensors();
        var (bufferCount, bufferCapacity, overwritten) = store.FleetBufferState();
        var (batches, readings) = store.FleetHistoryState();
        var (totalAnomalies, openAnomalies) = store.AnomalyCounts();
        var (binary, linear) = store.LastSearchCosts();

        var statuses = sensors
            .GroupBy(sensor => sensor.EvaluateStatus(now))
            .ToDictionary(group => group.Key, group => group.Count());

        var matrix = BuildHeatMatrix(sensors);

        return new DashboardStatsResponse
        {
            RegisteredSensors = sensors.Count,
            OnlineSensors = statuses.GetValueOrDefault(SensorStatus.Online),
            DegradedSensors = statuses.GetValueOrDefault(SensorStatus.Degraded),
            SilentSensors = statuses.GetValueOrDefault(SensorStatus.Silent),
            TotalReadings = readings,
            TotalBatches = batches,
            BufferOccupancy = bufferCount,
            BufferCapacity = bufferCapacity,
            BufferOverwritten = overwritten,
            Attachments = store.AttachmentCount,
            TotalAnomalies = totalAnomalies,
            OpenAnomalies = openAnomalies,
            AggregatePowerDraw = AggregatePowerDraw(sensors).ToString(),
            SensorsByCategory = sensors
                .GroupBy(sensor => sensor.Category.ToString())
                .ToDictionary(group => group.Key, group => group.Count()),
            HeatZones = [.. matrix.Zones],
            HeatRows = matrix.ToRows().Select(row => row.ToList()).ToList(),
            LastBinarySearchComparisons = binary,
            LastLinearSearchComparisons = linear,
            GeneratedAtUtc = now
        };
    }

    /// <summary>
    /// Total draw across every power sensor, folded with the overloaded + operator.
    /// <para>
    /// The unit check inside the operator is doing real work here: if a sensor were
    /// ever mis-registered as PowerConsumption while publishing in percent, this
    /// fold would throw rather than quietly adding a percentage to a wattage. The
    /// mismatch is caught and reported instead of being summed.
    /// </para>
    /// </summary>
    private SensorReading AggregatePowerDraw(IEnumerable<SensorRecord> sensors)
    {
        var total = SensorReading.Zero;

        foreach (var sensor in sensors.Where(s => s.Category == SensorCategory.PowerConsumption))
        {
            var latest = store.RecentReadings(sensor.Id, 1);
            if (latest.Count == 0)
            {
                continue;
            }

            try
            {
                total += latest[0];
            }
            catch (UnitMismatchException)
            {
                // A mis-registered node should not take the whole dashboard down.
                continue;
            }
        }

        return total;
    }

    /// <summary>
    /// Folds the fleet recent readings into the zone-by-hour grid the heat map draws.
    /// </summary>
    private TelemetryHeatMatrix BuildHeatMatrix(IReadOnlyList<SensorRecord> sensors)
    {
        var zones = sensors
            .Select(sensor => sensor.Location.Zone)
            .Where(zone => !string.IsNullOrWhiteSpace(zone))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(zone => zone, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matrix = new TelemetryHeatMatrix(zones);

        foreach (var sensor in sensors)
        {
            // Environmental readings are the ones worth comparing across zones; a
            // grid mixing watts and percentages would have no meaningful colour scale.
            if (sensor.Category != SensorCategory.Environmental)
            {
                continue;
            }

            foreach (var reading in store.RecentReadings(sensor.Id, _options.RollingWindow * 4))
            {
                matrix.Record(sensor.Location.Zone, reading.TimestampUtc.Hour, reading.Value);
            }
        }

        return matrix;
    }
}
