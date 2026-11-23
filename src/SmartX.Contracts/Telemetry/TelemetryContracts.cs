using System.ComponentModel.DataAnnotations;
using SmartX.Contracts.Validation;

namespace SmartX.Contracts.Telemetry;

/// <summary>
/// One telemetry reading arriving at the gateway, as a device would publish it.
/// <para>
/// The payload is carried as a string with a separate <see cref="PayloadType"/>
/// discriminator rather than as a JSON number. That is what lets the gateway wrap
/// it in the right closed generic - <c>TelemetryPacket&lt;float&gt;</c> for a
/// moisture probe, <c>TelemetryPacket&lt;int&gt;</c> for a meter,
/// <c>TelemetryPacket&lt;bool&gt;</c> for a relay - instead of deserialising
/// everything to double and losing the distinction the whole generic design exists
/// to preserve.
/// </para>
/// </summary>
public sealed class TelemetryIngestRequest
{
    /// <summary>Publishing node.</summary>
    [Required(ErrorMessage = "A MAC address is required to attribute the reading.")]
    [MacAddress]
    [Display(Name = "MAC address")]
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>
    /// The reading, as text. Parsed according to <see cref="PayloadType"/>:
    /// "21.4" for a float, "1450" for an int, "true" for a bool.
    /// </summary>
    [Required(ErrorMessage = "A value is required.")]
    [StringLength(32)]
    public string Value { get; set; } = string.Empty;

    /// <summary>Which closed generic the gateway should wrap the value in.</summary>
    [Required]
    [RegularExpression("^(float|int|bool)$", ErrorMessage = "Payload type must be float, int or bool.")]
    [Display(Name = "Payload type")]
    public string PayloadType { get; set; } = "float";

    /// <summary>Device-side capture time. Defaults to arrival time when omitted.</summary>
    public DateTimeOffset? TimestampUtc { get; set; }

    /// <summary>Monotonic per-sensor sequence number, used to spot dropped packets.</summary>
    [Range(0, long.MaxValue)]
    public long SequenceNumber { get; set; }
}

/// <summary>A batch of readings flushed by a node in one call.</summary>
public sealed class TelemetryBatchRequest
{
    /// <summary>Publishing node.</summary>
    [Required]
    [MacAddress]
    [Display(Name = "MAC address")]
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Which closed generic every reading in the batch should be wrapped in.</summary>
    [Required]
    [RegularExpression("^(float|int|bool)$", ErrorMessage = "Payload type must be float, int or bool.")]
    public string PayloadType { get; set; } = "float";

    /// <summary>The readings, oldest first.</summary>
    [Required(ErrorMessage = "A batch needs at least one reading.")]
    [MinLength(1, ErrorMessage = "A batch needs at least one reading.")]
    [MaxLength(5000, ErrorMessage = "A batch may carry at most 5000 readings.")]
    public List<string> Values { get; set; } = [];

    /// <summary>
    /// Capture time of the first reading. Later readings are spaced by
    /// <see cref="IntervalSeconds"/>. Defaults to working backwards from arrival.
    /// </summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>Seconds between consecutive readings in the batch.</summary>
    [Range(0.001, 3600)]
    public double IntervalSeconds { get; set; } = 1d;
}

/// <summary>
/// Asks the gateway to synthesise readings for a node, so the pipeline can be
/// exercised under load without physical hardware attached.
/// </summary>
public sealed class GenerateTelemetryRequest
{
    /// <summary>Node to generate for.</summary>
    [Required]
    [MacAddress]
    [Display(Name = "MAC address")]
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>How many readings to synthesise.</summary>
    [Range(1, 5000, ErrorMessage = "Generate between 1 and 5000 readings at a time.")]
    [Display(Name = "Reading count")]
    public int Count { get; set; } = 50;

    /// <summary>
    /// When true, one reading in the batch is pushed far outside the nominal band so
    /// the anomaly detector has something to catch.
    /// </summary>
    [Display(Name = "Inject an anomaly")]
    public bool InjectAnomaly { get; set; }
}

/// <summary>What the gateway did with an ingestion call.</summary>
public sealed class IngestionResultResponse
{
    /// <summary>Whether the readings were accepted.</summary>
    public bool Accepted { get; set; }

    /// <summary>Sensor the readings were attributed to.</summary>
    public Guid SensorId { get; set; }

    /// <summary>Node the readings came from.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>How many readings were taken in.</summary>
    public int ReadingsAccepted { get; set; }

    /// <summary>Which closed generic the gateway wrapped them in, e.g. "TelemetryPacket&lt;Single&gt;".</summary>
    public string PacketType { get; set; } = string.Empty;

    /// <summary>Live elements in the ingestion ring buffer after this call.</summary>
    public int BufferOccupancy { get; set; }

    /// <summary>Ring-buffer capacity.</summary>
    public int BufferCapacity { get; set; }

    /// <summary>Readings overwritten by the ring buffer so far - the back-pressure signal.</summary>
    public long BufferOverwritten { get; set; }

    /// <summary>Historical batches held for this sensor.</summary>
    public int HistoricalBatches { get; set; }

    /// <summary>Anomalies this call raised.</summary>
    public int AnomaliesRaised { get; set; }

    /// <summary>Human-readable summary for the client toast.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>One stored reading as the API reports it.</summary>
public sealed class TelemetryReadingResponse
{
    /// <summary>Capture time.</summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>Magnitude.</summary>
    public double Value { get; set; }

    /// <summary>Unit symbol, e.g. "W".</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Magnitude and unit rendered for display, e.g. "1450 W".</summary>
    public string Formatted { get; set; } = string.Empty;

    /// <summary>Standard deviations from this sensor's rolling mean.</summary>
    public double ZScore { get; set; }

    /// <summary>True when the reading is far enough out to be flagged.</summary>
    public bool IsAnomalous { get; set; }
}

/// <summary>A sensor's telemetry history, with the aggregates the dashboard needs.</summary>
public sealed class TelemetryHistoryResponse
{
    /// <summary>Sensor the history belongs to.</summary>
    public Guid SensorId { get; set; }

    /// <summary>Node address.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Operator-facing label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Unit every reading carries.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>The readings, oldest first.</summary>
    public List<TelemetryReadingResponse> Readings { get; set; } = [];

    /// <summary>Total readings held, which may exceed the page returned.</summary>
    public long TotalReadings { get; set; }

    /// <summary>Historical batches held.</summary>
    public int BatchCount { get; set; }

    /// <summary>Lengths of each stored batch - evidence that batch sizes genuinely vary.</summary>
    public List<int> BatchLengths { get; set; } = [];

    /// <summary>Slots a rectangular array would have needed for the same data.</summary>
    public long RectangularEquivalentSlots { get; set; }

    /// <summary>Mean across the whole history.</summary>
    public double Mean { get; set; }

    /// <summary>Lowest reading held.</summary>
    public double Minimum { get; set; }

    /// <summary>Highest reading held.</summary>
    public double Maximum { get; set; }

    /// <summary>Sum of the two most recent readings, via the overloaded + operator.</summary>
    public string? AggregateOfLastTwo { get; set; }

    /// <summary>Delta between the two most recent readings, via the overloaded - operator.</summary>
    public string? DeltaOfLastTwo { get; set; }
}
