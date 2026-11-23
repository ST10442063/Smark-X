using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Contracts.Telemetry;
using SmartX.Core.Models;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Controllers;

/// <summary>
/// Telemetry ingestion and history.
/// <para>
/// Single and batch ingestion share one path deliberately. A single reading is a
/// batch of one, so the ring buffer, the jagged historical store and the heartbeat
/// update all behave identically whichever endpoint was called - there is no
/// second code path to drift out of step.
/// </para>
/// </summary>
[ApiController]
[Route("api/telemetry")]
[Produces("application/json")]
[Tags("Telemetry")]
public sealed class TelemetryController(
    GatewayStore store,
    TelemetryIngestionService ingestion,
    AnomalyDetectionService detector,
    TelemetryPipeline pipeline,
    ILogger<TelemetryController> logger) : ControllerBase
{
    /// <summary>Accepts a single reading from a node.</summary>
    /// <param name="request">The reading.</param>
    /// <response code="202">The reading was accepted.</response>
    /// <response code="400">The payload could not be read as its declared type.</response>
    /// <response code="404">No node is registered under that MAC address.</response>
    [HttpPost]
    [ProducesResponseType(typeof(IngestionResultResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<IngestionResultResponse> Ingest([FromBody] TelemetryIngestRequest request)
    {
        var outcome = ingestion.BuildSingle(
            request.MacAddress,
            request.PayloadType,
            request.Value,
            request.SequenceNumber,
            request.TimestampUtc);

        return Complete(outcome);
    }

    /// <summary>
    /// Accepts a batch of readings flushed by a node in one call. The batch is
    /// appended to the historical store as a single jagged row, so the stored shape
    /// mirrors how the data actually arrived.
    /// </summary>
    /// <param name="request">The batch.</param>
    /// <response code="202">The batch was accepted.</response>
    /// <response code="400">A payload could not be read as its declared type.</response>
    /// <response code="404">No node is registered under that MAC address.</response>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(IngestionResultResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<IngestionResultResponse> IngestBatch([FromBody] TelemetryBatchRequest request)
    {
        var outcome = ingestion.BuildBatch(
            request.MacAddress,
            request.PayloadType,
            request.Values,
            request.StartedAtUtc,
            request.IntervalSeconds);

        return Complete(outcome);
    }

    /// <summary>
    /// Synthesises readings for a node, so the pipeline can be driven under load
    /// without physical hardware. Optionally distorts one reading so the anomaly
    /// detector has a genuine spike to catch.
    /// </summary>
    /// <param name="request">What to generate.</param>
    /// <response code="202">The readings were generated and accepted.</response>
    /// <response code="404">No node is registered under that MAC address.</response>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(IngestionResultResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<IngestionResultResponse> Generate([FromBody] GenerateTelemetryRequest request)
    {
        var outcome = ingestion.BuildGenerated(request.MacAddress, request.Count, request.InjectAnomaly);
        return Complete(outcome);
    }

    /// <summary>
    /// A node's telemetry history, with the aggregates the dashboard charts and the
    /// jagged-storage figures that show what the layout is buying.
    /// </summary>
    /// <param name="sensorId">Sensor key.</param>
    /// <param name="take">How many of the most recent readings to return.</param>
    /// <response code="200">The history.</response>
    /// <response code="404">No node holds that key.</response>
    [HttpGet("{sensorId:guid}")]
    [ProducesResponseType(typeof(TelemetryHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<TelemetryHistoryResponse> History(Guid sensorId, [FromQuery] int take = 120)
    {
        var sensor = store.FindById(sensorId);
        if (sensor is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No such sensor.",
                Detail = $"No sensor with key {sensorId} is registered with this gateway.",
                Status = StatusCodes.Status404NotFound,
                Instance = Request.Path
            });
        }

        take = Math.Clamp(take, 1, 2000);

        var history = store.ReadHistory(sensorId, batchStore =>
        {
            var (mean, min, max) = batchStore.Summarise();

            return new
            {
                Readings = batchStore.TakeNewest(take),
                batchStore.BatchCount,
                batchStore.TotalReadings,
                BatchLengths = batchStore.BatchLengths(),
                Rectangular = batchStore.RectangularEquivalentSlots(),
                Mean = mean,
                Min = min,
                Max = max
            };
        });

        var response = new TelemetryHistoryResponse
        {
            SensorId = sensor.Id,
            MacAddress = sensor.MacAddress,
            DisplayName = sensor.DisplayName,
            Unit = sensor.Unit.ToString(),
            TotalReadings = history.TotalReadings,
            BatchCount = history.BatchCount,
            BatchLengths = [.. history.BatchLengths],
            RectangularEquivalentSlots = history.Rectangular,
            Mean = history.Mean,
            Minimum = history.Min,
            Maximum = history.Max,
            Readings = detector.Score(history.Readings).Select(scored => new TelemetryReadingResponse
            {
                TimestampUtc = scored.Reading.TimestampUtc,
                Value = scored.Reading.Value,
                Unit = SensorReading.UnitSymbol(scored.Reading.Unit),
                Formatted = scored.Reading.ToString(),
                ZScore = scored.ZScore,
                IsAnomalous = scored.IsAnomalous
            }).ToList()
        };

        // The overloaded operators, used for real rather than demonstrated in a
        // comment: the last two readings aggregated and delta'd.
        if (history.Readings.Count >= 2)
        {
            var previous = history.Readings[^2];
            var latest = history.Readings[^1];

            response.AggregateOfLastTwo = (latest + previous).ToString();
            response.DeltaOfLastTwo = (latest - previous).ToString();
        }

        return Ok(response);
    }

    /// <summary>
    /// Aggregates the current draw of two nodes using the overloaded + operator,
    /// which is the <c>Meter3 = Meter1 + Meter2</c> case from the brief.
    /// </summary>
    /// <param name="firstMac">First node.</param>
    /// <param name="secondMac">Second node.</param>
    /// <response code="200">The aggregate.</response>
    /// <response code="404">One of the nodes is not registered.</response>
    /// <response code="409">The two nodes publish in units that cannot be combined.</response>
    [HttpGet("aggregate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult Aggregate([FromQuery] string firstMac, [FromQuery] string secondMac)
    {
        var (first, _) = store.FindByMac(firstMac);
        var (second, _) = store.FindByMac(secondMac);

        if (first is null || second is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No such sensor.",
                Detail = $"'{(first is null ? firstMac : secondMac)}' is not registered with this gateway.",
                Status = StatusCodes.Status404NotFound,
                Instance = Request.Path
            });
        }

        var firstReading = store.RecentReadings(first.Id, 1).FirstOrDefault();
        var secondReading = store.RecentReadings(second.Id, 1).FirstOrDefault();

        try
        {
            var combined = firstReading + secondReading;
            var delta = firstReading - secondReading;

            return Ok(new
            {
                first = new { first.MacAddress, first.DisplayName, reading = firstReading.ToString() },
                second = new { second.MacAddress, second.DisplayName, reading = secondReading.ToString() },
                aggregate = combined.ToString(),
                delta = delta.ToString(),
                expression = $"{firstReading} + {secondReading} = {combined}"
            });
        }
        catch (UnitMismatchException mismatch)
        {
            // The operator refusing is the correct outcome here, not a bug. Surfacing
            // it as a 409 tells the caller the request was coherent but the
            // combination is not physically meaningful.
            logger.LogInformation(
                "Refused to aggregate {First} and {Second}: {Message}",
                first.MacAddress, second.MacAddress, mismatch.Message);

            return Conflict(new ProblemDetails
            {
                Title = "Those readings cannot be combined.",
                Detail = mismatch.Message,
                Status = StatusCodes.Status409Conflict,
                Instance = Request.Path
            });
        }
    }

    /// <summary>Stores the packets an outcome produced and shapes the response.</summary>
    private ActionResult<IngestionResultResponse> Complete(IngestionOutcome outcome)
    {
        if (outcome.Failure == IngestionFailure.UnknownSensor)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No such sensor.",
                Detail = outcome.Message,
                Status = StatusCodes.Status404NotFound,
                Instance = Request.Path
            });
        }

        if (outcome.Failure == IngestionFailure.UnparseablePayload)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "The payload could not be read.",
                Detail = outcome.Message,
                Status = StatusCodes.Status400BadRequest,
                Instance = Request.Path
            });
        }

        var sensor = outcome.Sensor!;

        // The same pipeline the Part 2 dispatch worker uses, so a reading published
        // here also reaches the live command stream - its timeline, its alerts.
        var (snapshot, anomalies) = pipeline.ProcessDirect(sensor, outcome.Packets);

        logger.LogInformation(
            "Accepted {Count} readings from {Mac}; buffer {Used}/{Capacity}.",
            outcome.Packets.Count, sensor.MacAddress, snapshot.BufferCount, snapshot.BufferCapacity);

        return Accepted(new IngestionResultResponse
        {
            Accepted = true,
            SensorId = sensor.Id,
            MacAddress = sensor.MacAddress,
            ReadingsAccepted = outcome.Packets.Count,
            PacketType = DescribePacketType(outcome.Packets),
            BufferOccupancy = snapshot.BufferCount,
            BufferCapacity = snapshot.BufferCapacity,
            BufferOverwritten = snapshot.BufferOverwritten,
            HistoricalBatches = snapshot.BatchCount,
            AnomaliesRaised = anomalies.Count,
            Message = anomalies.Count == 0
                ? outcome.Message
                : $"{outcome.Message} {anomalies.Count} anomal{(anomalies.Count == 1 ? "y" : "ies")} raised." 
        });
    }

    /// <summary>
    /// Names the closed generic the packets were built as, e.g.
    /// "TelemetryPacket&lt;Single&gt;". Returned so a caller can confirm the
    /// discriminator produced the type it expected.
    /// </summary>
    private static string DescribePacketType(IReadOnlyList<ITelemetryPacket> packets)
        => packets.Count == 0
            ? "none"
            : $"TelemetryPacket<{packets[0].PayloadType.Name}>";
}
