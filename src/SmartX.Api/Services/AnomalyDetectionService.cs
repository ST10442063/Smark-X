using Microsoft.Extensions.Options;
using SmartX.Api.Models;
using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>A reading with its deviation from the sensor rolling mean.</summary>
/// <param name="Reading">The reading.</param>
/// <param name="ZScore">Standard deviations from the rolling mean. Zero when unscored.</param>
/// <param name="IsAnomalous">True when the deviation crosses the warning threshold.</param>
public readonly record struct ScoredReading(SensorReading Reading, double ZScore, bool IsAnomalous);

/// <summary>
/// Flags readings that are out of character for the sensor that produced them, and
/// nodes that have stopped producing readings at all.
/// <para>
/// <b>Why a rolling z-score rather than fixed thresholds.</b> A fixed band has to
/// be configured per sensor and is wrong the moment anything changes: 40% moisture
/// is healthy in one bay and a drought in another, and a meter that normally draws
/// 800 W is a different sensor after a pump is added. Measuring each reading
/// against the recent behaviour of that same sensor needs no per-node configuration
/// and adapts as the deployment changes.
/// </para>
/// <para>
/// <b>Why the window matters.</b> Too short and ordinary variation reads as a
/// spike; too long and a slow drift never registers because the mean drifts with
/// it. The window and both thresholds are configurable, and nothing is scored at
/// all until enough samples exist for a standard deviation to mean anything -
/// otherwise the first two readings of a node life would flag each other.
/// </para>
/// </summary>
public sealed class AnomalyDetectionService(GatewayStore store, IOptions<GatewayOptions> options)
{
    private readonly GatewayOptions _options = options.Value;

    /// <summary>
    /// Scores the readings just accepted for a sensor and raises an anomaly for each
    /// one that crosses a threshold.
    /// </summary>
    /// <param name="sensor">Sensor that produced them.</param>
    /// <param name="newReadingCount">How many readings were just accepted.</param>
    /// <returns>The anomalies raised.</returns>
    public List<Anomaly> InspectLatest(SensorRecord sensor, int newReadingCount)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        if (newReadingCount <= 0 || !IsContinuouslyScored(sensor))
        {
            return [];
        }

        // Pull the new readings plus a full window of the readings before them, so
        // each new reading can be measured against what preceded it rather than
        // against itself.
        var window = _options.RollingWindow;
        var readings = store.RecentReadings(sensor.Id, newReadingCount + window);
        var firstNewIndex = Math.Max(0, readings.Count - newReadingCount);

        var raised = new List<Anomaly>();

        for (var i = firstNewIndex; i < readings.Count; i++)
        {
            var start = Math.Max(0, i - window);
            var baseline = readings.GetRange(start, i - start);

            if (baseline.Count < _options.MinimumSamplesBeforeScoring)
            {
                continue;
            }

            var (mean, deviation) = MeanAndDeviation(baseline);
            if (deviation <= double.Epsilon)
            {
                // A perfectly flat signal has no scale to measure against. Treating
                // that as infinite deviation would flag every actuator toggle.
                continue;
            }

            var reading = readings[i];
            var z = (reading.Value - mean) / deviation;

            if (!CrossesThresholds(z, reading.Value, mean))
            {
                continue;
            }

            var severity = Math.Abs(z) >= _options.CriticalZScore
                ? AnomalySeverity.Critical
                : AnomalySeverity.Warning;

            var kind = z > 0 ? AnomalyKind.Spike : AnomalyKind.Drop;

            // The overloaded - operator, used for real: the delta against the
            // baseline mean, carrying the unit through so the message reads in the
            // sensor own units rather than as a bare number.
            var delta = reading - new SensorReading(mean, reading.Unit, reading.TimestampUtc);

            var anomaly = new Anomaly
            {
                SensorId = sensor.Id,
                MacAddress = sensor.MacAddress,
                Kind = kind,
                Severity = severity,
                Reading = reading.ToString(),
                ZScore = Math.Round(z, 2),
                DetectedAtUtc = reading.TimestampUtc,
                Explanation =
                    $"{sensor.DisplayName} read {reading}, which is {delta} against a recent average of " +
                    $"{new SensorReading(mean, reading.Unit)} - {Math.Abs(z):0.#} standard deviations " +
                    $"{(z > 0 ? "above" : "below")} normal for this node."
            };

            store.RaiseAnomaly(anomaly);
            raised.Add(anomaly);
        }

        return raised;
    }

    /// <summary>
    /// Whether a sensor publishes a continuous quantity that a rolling z-score can
    /// meaningfully describe.
    /// <para>
    /// Actuators are excluded. A relay reports 0 or 1, and a valve opening is the
    /// single most normal thing it ever does - but against a window that is mostly
    /// zeroes it is a large departure on both tests, so it scores as a critical
    /// spike every cycle. A first seeded run produced 60 such alerts from two
    /// valves, against 2 for the faults that had actually been planted.
    /// </para>
    /// <para>
    /// The right answer is not a higher threshold, which would only hide real
    /// faults on the continuous sensors too. It is that a discrete state simply is
    /// not a quantity whose deviation from a mean carries meaning. Actuators are
    /// still watched for disconnects, which is the failure that does matter for
    /// them.
    /// </para>
    /// </summary>
    private static bool IsContinuouslyScored(SensorRecord sensor)
        => sensor.Unit != TelemetryUnit.State && sensor.Category != SensorCategory.Actuator;

    /// <summary>
    /// Raises a disconnect anomaly for every node that has missed two or more
    /// heartbeats, once per outage.
    /// <para>
    /// The already-open check is what stops the dashboard filling with hundreds of
    /// identical alerts for one node that went offline overnight. The operator
    /// wants to know it is down, once.
    /// </para>
    /// </summary>
    /// <returns>The anomalies raised by this sweep.</returns>
    public List<Anomaly> SweepDisconnects()
    {
        var now = DateTimeOffset.UtcNow;
        var raised = new List<Anomaly>();

        foreach (var sensor in store.AllSensors())
        {
            if (sensor.EvaluateStatus(now) != SensorStatus.Silent)
            {
                continue;
            }

            var silence = sensor.LastSeenUtc is { } seen
                ? $"has not reported for {(now - seen).TotalSeconds:0} seconds"
                : "has never reported since registration";

            var anomaly = new Anomaly
            {
                SensorId = sensor.Id,
                MacAddress = sensor.MacAddress,
                Kind = AnomalyKind.Disconnect,
                Severity = AnomalySeverity.Critical,
                Reading = "no signal",
                ZScore = 0d,
                DetectedAtUtc = now,
                Explanation =
                    $"{sensor.DisplayName} at {sensor.Location.QualifiedPath} {silence}, " +
                    $"against an expected heartbeat every {sensor.HeartbeatIntervalSeconds} seconds."
            };

            // Check-and-add in one atomic step. Several endpoints sweep, and the
            // client polls them concurrently, so a separate "is one open?" call
            // would let two sweeps both decide to raise the same alert.
            if (store.TryRaiseDisconnect(anomaly, sensor.LastSeenUtc))
            {
                raised.Add(anomaly);
            }
        }

        return raised;
    }

    /// <summary>
    /// Scores a sequence of readings for display, without raising anything. Used by
    /// the history endpoint so the chart can colour each point.
    /// </summary>
    public List<ScoredReading> Score(IReadOnlyList<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var window = _options.RollingWindow;
        var scored = new List<ScoredReading>(readings.Count);

        for (var i = 0; i < readings.Count; i++)
        {
            var start = Math.Max(0, i - window);
            var baselineCount = i - start;

            if (baselineCount < _options.MinimumSamplesBeforeScoring)
            {
                scored.Add(new ScoredReading(readings[i], 0d, false));
                continue;
            }

            var baseline = new List<SensorReading>(baselineCount);
            for (var j = start; j < i; j++)
            {
                baseline.Add(readings[j]);
            }

            var (mean, deviation) = MeanAndDeviation(baseline);

            if (deviation <= double.Epsilon)
            {
                scored.Add(new ScoredReading(readings[i], 0d, false));
                continue;
            }

            var z = (readings[i].Value - mean) / deviation;
            scored.Add(new ScoredReading(
                readings[i],
                Math.Round(z, 2),
                CrossesThresholds(z, readings[i].Value, mean)));
        }

        return scored;
    }

    /// <summary>
    /// A reading is anomalous only when it is both statistically surprising - past
    /// the z-score threshold - and materially different from the recent level.
    /// <para>
    /// The second test is what keeps the queue usable. A trending signal compresses
    /// its own rolling standard deviation, so mid-swing a reading that moved two
    /// percent on a forty percent baseline can score three sigma. That is true and
    /// useless: nobody should be paged because a moisture probe drifted the way
    /// moisture probes drift.
    /// </para>
    /// </summary>
    private bool CrossesThresholds(double z, double value, double mean)
    {
        if (Math.Abs(z) < _options.WarningZScore)
        {
            return false;
        }

        // Against a mean of effectively zero there is no proportion to take, so the
        // z-score is left to stand on its own.
        var scale = Math.Abs(mean);
        if (scale <= double.Epsilon)
        {
            return true;
        }

        return Math.Abs(value - mean) / scale >= _options.MinimumRelativeDeparture;
    }

    /// <summary>
    /// Mean and population standard deviation of a window, in one pass.
    /// </summary>
    private static (double Mean, double Deviation) MeanAndDeviation(IReadOnlyList<SensorReading> window)
    {
        if (window.Count == 0)
        {
            return (0d, 0d);
        }

        var sum = 0d;
        foreach (var reading in window)
        {
            sum += reading.Value;
        }

        var mean = sum / window.Count;

        var squaredError = 0d;
        foreach (var reading in window)
        {
            var error = reading.Value - mean;
            squaredError += error * error;
        }

        return (mean, Math.Sqrt(squaredError / window.Count));
    }
}
