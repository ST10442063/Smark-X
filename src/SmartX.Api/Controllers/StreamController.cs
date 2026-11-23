using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services.Streaming;
using SmartX.Contracts.Stream;
using SmartX.Core.Devices;
using SmartX.Core.Streaming;
using SmartX.Core.Timeline;

namespace SmartX.Api.Controllers;

/// <summary>
/// The operator side of the Real-Time Command Stream: the live device grid and its
/// filters, timelines, the event history, the queue, and the test tools.
/// </summary>
[ApiController]
[Route("api/stream")]
[Produces("application/json")]
[Tags("Command stream")]
public sealed class StreamController(
    CommandStreamStore store,
    DualLaneDispatchQueue<QueuedPacket> queue,
    StreamTestTools tests) : ControllerBase
{
    /// <summary>
    /// Everything the Command Stream page needs to draw itself, in one request. The
    /// page calls this on load and after a reconnect; after that, SignalR keeps it current.
    /// </summary>
    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(StreamSnapshotResponse), StatusCodes.Status200OK)]
    public ActionResult<StreamSnapshotResponse> Snapshot(
        [FromQuery] string? category = null,
        [FromQuery] string? alert = null,
        [FromQuery] string? q = null)
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = queue.Snapshot();

        return Ok(store.Write(state =>
        {
            if (state.SuggestionsDirty)
            {
                store.RefreshSuggestions(state, now);
            }

            return new StreamSnapshotResponse
            {
                Queue = StreamMapper.QueueStats(metrics, state, now, store.Options.SimulatedProcessingMs),
                Devices = StreamMapper.Filter(state, category, alert, q, now),
                Events = [.. state.Events.Latest(60).Select(entry => StreamMapper.Event(state, entry))],
                RecentPackets = [.. state.RecentPackets.TakeNewest(40)],
                Commands = StreamMapper.History(state),
                Insights = StreamMapper.Insights(state, now, store.Options)
            };
        }));
    }

    /// <summary>
    /// The device grid, filtered. Category and alert state each select a set and the
    /// sets are intersected; only the survivors are text-matched. The response says
    /// how many devices the text match had to look at.
    /// </summary>
    /// <param name="category">Environmental, PowerConsumption, Actuator or Connectivity.</param>
    /// <param name="alert">Critical, Warning, Disconnected, Alerting (any) or Clear.</param>
    /// <param name="q">Free text against name, location and address.</param>
    [HttpGet("devices")]
    [ProducesResponseType(typeof(DeviceFilterResponse), StatusCodes.Status200OK)]
    public ActionResult<DeviceFilterResponse> Devices(
        [FromQuery] string? category = null,
        [FromQuery] string? alert = null,
        [FromQuery] string? q = null)
        => Ok(store.Read(state => StreamMapper.Filter(state, category, alert, q, DateTimeOffset.UtcNow)));

    /// <summary>
    /// A device's per-minute timeline, oldest first, read in order straight from its
    /// sorted dictionary.
    /// </summary>
    /// <param name="mac">The device.</param>
    /// <param name="minutes">How much history, up to six hours.</param>
    /// <response code="404">No such device.</response>
    [HttpGet("devices/{mac}/timeline")]
    [ProducesResponseType(typeof(TimelineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<TimelineResponse> Timeline(string mac, [FromQuery] int minutes = 120)
    {
        if (!MacKey.TryParse(mac, out var key))
        {
            return NotFoundDevice(mac);
        }

        var window = TimeSpan.FromMinutes(Math.Clamp(minutes, 5, 360));
        var timeline = store.Read(state => state.Devices.TryGet(key, out var device)
            ? StreamMapper.Timeline(device, window)
            : null);

        return timeline is null ? NotFoundDevice(mac) : Ok(timeline);
    }

    /// <summary>
    /// The event history, newest first, optionally filtered and bounded in time.
    /// </summary>
    /// <param name="take">How many entries, up to 500.</param>
    /// <param name="kind">One event kind, e.g. AlertRaised or CommandIssued.</param>
    /// <param name="level">Info, Warning or Critical.</param>
    /// <param name="mac">Only events about this device.</param>
    /// <param name="fromUtc">Oldest timestamp to include.</param>
    /// <param name="toUtc">Newest timestamp to include.</param>
    [HttpGet("history")]
    [ProducesResponseType(typeof(List<StreamEventResponse>), StatusCodes.Status200OK)]
    public ActionResult<List<StreamEventResponse>> History(
        [FromQuery] int take = 100,
        [FromQuery] string? kind = null,
        [FromQuery] string? level = null,
        [FromQuery] string? mac = null,
        [FromQuery] DateTimeOffset? fromUtc = null,
        [FromQuery] DateTimeOffset? toUtc = null)
    {
        StreamEventKind? wantedKind = Enum.TryParse<StreamEventKind>(kind, true, out var k) ? k : null;
        StreamEventLevel? wantedLevel = Enum.TryParse<StreamEventLevel>(level, true, out var l) ? l : null;
        string? wantedMac = MacKey.TryParse(mac, out var parsed) ? parsed.ToString() : null;

        bool Matches(StreamEvent entry) =>
            (wantedKind is null || entry.Kind == wantedKind)
            && (wantedLevel is null || entry.Level == wantedLevel)
            && (wantedMac is null || entry.Mac == wantedMac);

        return Ok(store.Read(state => state.Events
            .Between(fromUtc ?? DateTimeOffset.MinValue, toUtc ?? DateTimeOffset.MaxValue, Math.Clamp(take, 1, 500), Matches)
            .Select(entry => StreamMapper.Event(state, entry))
            .ToList()));
    }

    /// <summary>Queue depths, throughput, the bypass record and the duplicate counters.</summary>
    [HttpGet("queue")]
    [ProducesResponseType(typeof(QueueStatsResponse), StatusCodes.Status200OK)]
    public ActionResult<QueueStatsResponse> Queue()
    {
        var metrics = queue.Snapshot();
        return Ok(store.Read(state =>
            StreamMapper.QueueStats(metrics, state, DateTimeOffset.UtcNow, store.Options.SimulatedProcessingMs)));
    }

    /// <summary>
    /// Injects a critical packet - a power spike, a moisture crash or a link loss -
    /// through the normal uplink. Triage, not this endpoint, puts it in the priority lane.
    /// </summary>
    /// <response code="202">Queued.</response>
    /// <response code="404">No device of the right category is registered.</response>
    [HttpPost("test/critical")]
    [ProducesResponseType(typeof(TestInjectionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<TestInjectionResponse> SimulateCritical([FromBody] SimulateCriticalRequest request)
    {
        var result = tests.InjectCritical(request);

        return result is null
            ? NotFound(new ProblemDetails
            {
                Title = "No suitable device.",
                Detail = $"No registered device can carry a {request.Kind} test.",
                Status = StatusCodes.Status404NotFound,
                Instance = Request.Path
            })
            : Accepted(result);
    }

    /// <summary>
    /// Floods the standard lane with routine packets, so the bypass is visible:
    /// inject a critical packet while the backlog drains and it is processed first.
    /// </summary>
    [HttpPost("test/flood")]
    [ProducesResponseType(typeof(TestInjectionResponse), StatusCodes.Status202Accepted)]
    public ActionResult<TestInjectionResponse> Flood([FromBody] FloodRequest request)
        => Accepted(tests.Flood(request.Count));

    private NotFoundObjectResult NotFoundDevice(string mac) => NotFound(new ProblemDetails
    {
        Title = "No such device.",
        Detail = $"'{mac}' is not registered with this gateway.",
        Status = StatusCodes.Status404NotFound,
        Instance = Request.Path
    });
}
