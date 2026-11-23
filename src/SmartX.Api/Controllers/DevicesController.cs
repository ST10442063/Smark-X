using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Api.Services.Streaming;
using SmartX.Api.Services.Topology;
using SmartX.Contracts.Stream;
using SmartX.Contracts.Topology;
using SmartX.Core.Devices;

namespace SmartX.Api.Controllers;

/// <summary>
/// The device-facing side of the command stream: what a microcontroller - or the
/// SmartX.DeviceEmulator standing in for one - talks to.
/// <para>
/// Devices publish telemetry up the uplink, collect their commands from the
/// downlink, and acknowledge each one once it is applied. That is the shape of
/// LwM2M and of an MQTT command topic, carried over plain HTTP so it can be
/// exercised with nothing more than curl or Swagger.
/// </para>
/// </summary>
[ApiController]
[Route("api/devices")]
[Produces("application/json")]
[Tags("Devices (uplink and downlink)")]
public sealed class DevicesController(
    UplinkService uplink,
    CommandService commands,
    GatewayStore gateway,
    CommandStreamStore stream,
    MeshTopologyService topology,
    ILogger<DevicesController> logger) : ControllerBase
{
    /// <summary>
    /// Publishes a frame of readings. Each packet's device is found by MAC in O(1),
    /// triaged against the critical bands, and queued: critical packets in the
    /// priority lane, everything else at the back of the FIFO queue. Returns as soon
    /// as every packet has a place - processing happens behind the queue.
    /// </summary>
    /// <param name="envelope">The readings, as a mesh border router would forward them.</param>
    /// <response code="202">At least one packet was queued. The receipts say which lane each took.</response>
    /// <response code="422">No packet could be queued - every receipt says why.</response>
    /// <response code="503">The standard lane is full and every packet was routine telemetry that was shed.</response>
    [HttpPost("uplink")]
    [ProducesResponseType(typeof(UplinkEnvelopeResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(UplinkEnvelopeResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(UplinkEnvelopeResponse), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<UplinkEnvelopeResponse> Uplink([FromBody] UplinkEnvelopeRequest envelope)
    {
        var response = uplink.Accept(envelope);

        if (response.Priority > 0)
        {
            logger.LogInformation("Uplink from {Gateway}: {Priority} of {Received} packets triaged critical.",
                envelope.GatewayId ?? "unknown", response.Priority, response.Received);
        }

        if (response.Accepted > 0)
        {
            return Accepted(response);
        }

        var shed = response.Receipts.All(receipt => receipt.Reason?.Contains("full", StringComparison.Ordinal) == true);
        return StatusCode(shed ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status422UnprocessableEntity, response);
    }

    /// <summary>
    /// The fleet handed to the device emulator: every node the seeder provisioned for
    /// it, with the last value the gateway holds so the emulator can carry on from it.
    /// Nodes registered by an operator, and the deliberately silent node, are withheld.
    /// </summary>
    /// <summary>
    /// Accepts devices' neighbour tables - each node listing the neighbours it can
    /// hear and at what signal strength, as a Zigbee Link Status message or a
    /// Thread MLE advertisement would. The gateway smooths each link's RSSI towards
    /// the reports, so the mesh graph's weights track what the radios actually see.
    /// </summary>
    /// <param name="envelope">The tables, as a border router would forward them.</param>
    /// <response code="200">The tables were applied; the counts say how.</response>
    [HttpPost("neighbours")]
    [ProducesResponseType(typeof(NeighbourEnvelopeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<NeighbourEnvelopeResponse> Neighbours([FromBody] NeighbourEnvelopeRequest envelope)
        => Ok(topology.ApplyNeighbourReports(envelope));

    [HttpGet("manifest")]
    [ProducesResponseType(typeof(DeviceManifestResponse), StatusCodes.Status200OK)]
    public ActionResult<DeviceManifestResponse> Manifest()
    {
        var provisioned = gateway.SimulatedSensors();

        var devices = stream.Read(state => provisioned
            .Select(sensor =>
            {
                double? last = MacKey.TryParse(sensor.MacAddress, out var mac) && state.Devices.TryGet(mac, out var device)
                    ? device.LatestValue
                    : null;

                return new ManifestDeviceResponse
                {
                    MacAddress = sensor.MacAddress,
                    DisplayName = sensor.DisplayName,
                    Category = sensor.Category.ToString(),
                    PayloadType = TelemetrySimulator.PayloadTypeFor(sensor.Category),
                    Unit = sensor.Unit.ToString(),
                    Zone = $"{sensor.Location.Facility} / {sensor.Location.Zone}",
                    LastValue = last,
                    HeartbeatIntervalSeconds = sensor.HeartbeatIntervalSeconds,
                    X = topology.PositionOf(sensor.MacAddress)?.X,
                    Y = topology.PositionOf(sensor.MacAddress)?.Y
                };
            })
            .ToList());

        return Ok(new DeviceManifestResponse
        {
            GatewayMac = topology.GatewayMac,
            GatewayX = MeshSitePlan.GatewayPosition.X,
            GatewayY = MeshSitePlan.GatewayPosition.Y,
            Devices = devices,
            Withheld = gateway.SensorCount - devices.Count
        });
    }

    /// <summary>
    /// Collects the commands waiting for devices and marks them delivered. Each
    /// command must then be acknowledged once applied.
    /// </summary>
    /// <param name="mac">Only collect for these devices - an emulator passes the nodes it currently has online. Omit for all.</param>
    [HttpGet("downlink")]
    [ProducesResponseType(typeof(DownlinkResponse), StatusCodes.Status200OK)]
    public ActionResult<DownlinkResponse> Downlink([FromQuery] string[]? mac)
    {
        HashSet<MacKey>? only = null;

        if (mac is { Length: > 0 })
        {
            only = [];
            foreach (var raw in mac.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                if (MacKey.TryParse(raw, out var key))
                {
                    only.Add(key);
                }
            }
        }

        return Ok(commands.CollectDownlink(only));
    }

    /// <summary>Confirms a device applied a command.</summary>
    /// <param name="commandId">The command collected from the downlink.</param>
    /// <param name="request">The state the relay is now in.</param>
    /// <response code="200">Recorded.</response>
    /// <response code="404">No such command.</response>
    [HttpPost("downlink/{commandId:guid}/ack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult Acknowledge(Guid commandId, [FromBody] CommandAckRequest request)
    {
        var result = commands.Acknowledge(commandId, request.AppliedState);

        return result.Succeeded
            ? Ok(new { result.Message })
            : NotFound(new ProblemDetails
            {
                Title = "No such command.",
                Detail = result.Message,
                Status = StatusCodes.Status404NotFound,
                Instance = Request.Path
            });
    }
}
