using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Api.Services.Streaming;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Insights;
using SmartX.Contracts.Stream;

namespace SmartX.Api.Tests;

/// <summary>
/// The command stream end to end: real HTTP into a real gateway, with the dispatch
/// worker, the health sweep and the device round trip all running as they do in
/// production.
/// </summary>
public class CommandStreamTests
{
    private const string Probe = "A4:CF:12:AA:00:01";
    private const string Meter = "A4:CF:12:AA:00:02";
    private const string Pump = "A4:CF:12:AA:00:03";

    [Fact]
    public async Task ACriticalPacket_JumpsAFloodedStandardLane()
    {
        // A 10 ms budget makes 300 routine packets take about three seconds to drain.
        await using var factory = new GatewayFactory(processingMs: 10);
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Meter, "Pump Feed Meter", "PowerConsumption", "Watt");

        var flood = Enumerable.Range(0, 300).Select(_ => Gateway.Packet(Meter, "int", "820")).ToArray();
        var routine = await Gateway.PublishAsync(http, flood);
        var critical = await Gateway.PublishAsync(http, Gateway.Packet(Meter, "int", "4100"));

        Assert.Equal(300, routine.Accepted);
        Assert.All(routine.Receipts, receipt => Assert.Equal("Standard", receipt.Lane));

        var receipt = Assert.Single(critical.Receipts);
        Assert.Equal("Priority", receipt.Lane);
        Assert.True(receipt.StandardWaiting > 100, $"Only {receipt.StandardWaiting} were waiting.");
        Assert.Contains("2500 W ceiling", receipt.Reason);

        await Gateway.EventuallyAsync(
            async () => (await Gateway.GetAsync<QueueStatsResponse>(http, "api/stream/queue")).PriorityProcessed == 1,
            "the critical packet has been processed");

        var stats = await Gateway.GetAsync<QueueStatsResponse>(http, "api/stream/queue");

        // Processed while most of the flood was still queued behind it.
        Assert.Equal(1, stats.Bypasses);
        Assert.True(stats.LastPriorityOvertook > 100);
        Assert.True(stats.StandardDepth > 0, "The flood should still be draining.");
        Assert.True(stats.LastPriorityWaitMs < 1000, $"The alert waited {stats.LastPriorityWaitMs} ms.");
    }

    [Fact]
    public async Task UnknownDevicesAndUnreadableValues_AreRefusedPerPacket()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Probe, "Soil Moisture Probe", "Environmental", "Percent");

        var response = await Gateway.PublishAsync(http,
            Gateway.Packet(Probe, "float", "41.5"),
            Gateway.Packet("A4:CF:12:FF:FF:FF", "float", "41.5"),
            Gateway.Packet(Probe, "float", "wet"));

        Assert.Equal(1, response.Accepted);
        Assert.Equal(2, response.Rejected);
        Assert.Equal(UplinkService.LookupStrategy, response.LookupStrategy);
        Assert.Contains("No device", response.Receipts[1].Reason);
        Assert.Contains("could not be read", response.Receipts[2].Reason);
    }

    [Fact]
    public async Task ACommand_IsCollectedAcknowledgedAndUndone()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Pump, "Water Pump A", "Actuator", "State");

        // The pump reports that it is off, so the command has a known prior state.
        await Gateway.PublishAsync(http, Gateway.Packet(Pump, "bool", "false"));
        await Gateway.EventuallyAsync(async () => (await Gateway.DeviceAsync(http, Pump)).ReportedState == false, "the pump reports OFF");

        var issued = await http.PostAsJsonAsync("api/commands", new IssueCommandRequest { Action = "SwitchOn", Targets = [Pump] }, Gateway.Json);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);

        var pending = await Gateway.DeviceAsync(http, Pump);
        Assert.True(pending.DesiredState);
        Assert.True(pending.AwaitingConfirmation);

        // The device collects the command...
        var downlink = await Gateway.GetAsync<DownlinkResponse>(http, $"api/devices/downlink?mac={Pump}");
        var command = Assert.Single(downlink.Commands);
        Assert.True(command.DesiredState);

        // ...and nothing is left for it on the next poll.
        Assert.Empty((await Gateway.GetAsync<DownlinkResponse>(http, $"api/devices/downlink?mac={Pump}")).Commands);

        var ack = await http.PostAsJsonAsync($"api/devices/downlink/{command.CommandId}/ack", new CommandAckRequest { AppliedState = true }, Gateway.Json);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var confirmed = await Gateway.DeviceAsync(http, Pump);
        Assert.True(confirmed.ReportedState);
        Assert.False(confirmed.AwaitingConfirmation);

        var history = await Gateway.GetAsync<CommandHistoryResponse>(http, "api/commands");
        Assert.Equal("Acknowledged", Assert.Single(history.UndoStack).Targets.Single().Status);

        // Undo sends a compensating command back to the state recorded beforehand.
        var undo = await http.PostAsync("api/commands/undo", null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);

        var compensating = Assert.Single((await Gateway.GetAsync<DownlinkResponse>(http, "api/devices/downlink")).Commands);
        Assert.False(compensating.DesiredState);
        Assert.Equal("Undo", compensating.Origin);

        var after = await Gateway.GetAsync<CommandHistoryResponse>(http, "api/commands");
        Assert.False(after.CanUndo);
        Assert.True(after.CanRedo);

        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("api/commands/undo", null)).StatusCode);
    }

    [Fact]
    public async Task ACommandToADeviceThatNeverCollectsIt_TimesOut()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Pump, "Water Pump A", "Actuator", "State");

        await http.PostAsJsonAsync("api/commands", new IssueCommandRequest { Action = "SwitchOff", Targets = [Pump] }, Gateway.Json);

        // The test host times commands out after two seconds; the sweep is driven directly.
        var sweep = factory.Services.GetRequiredService<HealthSweepService>();
        sweep.Sweep(DateTimeOffset.UtcNow.AddSeconds(5), TimeSpan.FromSeconds(2));

        var history = await Gateway.GetAsync<CommandHistoryResponse>(http, "api/commands");
        Assert.Equal("TimedOut", history.UndoStack.Single().Targets.Single().Status);
    }

    [Fact]
    public async Task ASensorIsNotACommandTarget()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Probe, "Soil Moisture Probe", "Environmental", "Percent");

        var response = await http.PostAsJsonAsync("api/commands", new IssueCommandRequest { Action = "SwitchOn", Targets = [Probe] }, Gateway.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ANodeThatStaysSilent_IsReportedOnce()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Probe, "Soil Moisture Probe", "Environmental", "Percent", heartbeatSeconds: 1);

        var sweep = factory.Services.GetRequiredService<HealthSweepService>();
        for (var i = 0; i < 5; i++)
        {
            sweep.Sweep(DateTimeOffset.UtcNow.AddMinutes(i + 1), TimeSpan.FromSeconds(30));
        }

        var downs = await Gateway.GetAsync<List<StreamEventResponse>>(http, $"api/stream/history?kind=NodeDown&mac={Probe}");
        Assert.Single(downs);

        var stats = await Gateway.GetAsync<QueueStatsResponse>(http, "api/stream/queue");
        Assert.Equal(1, stats.NodesDown);
        Assert.True(stats.OutageDuplicatesSuppressed >= 4);
    }

    [Fact]
    public async Task ARepeatedCriticalReading_RaisesOneAlert()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Probe, "Soil Moisture Probe", "Environmental", "Percent");

        await Gateway.PublishAsync(http, Gateway.Packet(Probe, "float", "6"), Gateway.Packet(Probe, "float", "5"), Gateway.Packet(Probe, "float", "5.5"));

        await Gateway.EventuallyAsync(
            async () => (await Gateway.GetAsync<QueueStatsResponse>(http, "api/stream/queue")).PriorityProcessed == 3,
            "all three critical packets are processed");

        var alerts = await Gateway.GetAsync<List<StreamEventResponse>>(http, "api/stream/history?kind=AlertRaised");
        Assert.Single(alerts);

        var stats = await Gateway.GetAsync<QueueStatsResponse>(http, "api/stream/queue");
        Assert.Equal(2, stats.AlertDuplicatesSuppressed);
        Assert.Equal("Critical", (await Gateway.DeviceAsync(http, Probe)).AlertState);
    }

    [Fact]
    public async Task ADeviceFilter_IntersectsCategoryAndAlertState()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Probe, "Soil Moisture Probe", "Environmental", "Percent");
        await Gateway.RegisterAsync(http, Meter, "Pump Feed Meter", "PowerConsumption", "Watt");
        await Gateway.RegisterAsync(http, Pump, "Water Pump A", "Actuator", "State");

        await Gateway.PublishAsync(http, Gateway.Packet(Probe, "float", "7"), Gateway.Packet(Meter, "int", "800"));
        await Gateway.EventuallyAsync(async () => (await Gateway.DeviceAsync(http, Probe)).AlertState == "Critical", "the probe is critical");

        var critical = await Gateway.GetAsync<DeviceFilterResponse>(http, "api/stream/devices?category=Environmental&alert=Critical");
        Assert.Equal(Probe, Assert.Single(critical.Devices).MacAddress);
        Assert.Contains("∩", critical.Strategy);

        var none = await Gateway.GetAsync<DeviceFilterResponse>(http, "api/stream/devices?category=PowerConsumption&alert=Critical");
        Assert.Empty(none.Devices);
    }

    [Fact]
    public async Task ALearnedHabit_IsSuggestedWhenItsTriggerRecurs()
    {
        await using var factory = new GatewayFactory();
        var http = factory.CreateClient();
        await Gateway.RegisterAsync(http, Probe, "Soil Moisture Probe B", "Environmental", "Percent");
        await Gateway.RegisterAsync(http, Pump, "Water Pump A", "Actuator", "State");

        // Twice: the probe crashes, the operator searches for the pump and switches it
        // on, the probe recovers and the pump goes back off.
        for (var incident = 0; incident < 2; incident++)
        {
            await CrashAsync(http);

            await http.PostAsJsonAsync("api/insights/activity", new RecordActivityRequest { Kind = "Search", Text = "Pump A" }, Gateway.Json);
            await http.PostAsJsonAsync("api/commands", new IssueCommandRequest { Action = "SwitchOn", Targets = [Pump] }, Gateway.Json);

            await RecoverAsync(http);
            await http.PostAsJsonAsync("api/commands", new IssueCommandRequest { Action = "SwitchOff", Targets = [Pump] }, Gateway.Json);
        }

        // The third time, the engine offers the habit before the operator asks.
        await CrashAsync(http);

        var insights = await Gateway.GetAsync<InsightsResponse>(http, "api/insights");
        var action = insights.Actions.Single(suggestion => suggestion.MacAddress == Pump);

        Assert.Equal("Switch ON Water Pump A", action.Title);
        Assert.True(action.DesiredState);
        Assert.True(action.Support >= 2);

        // Whichever trigger is strongest supplies the evidence. Here, seconds apart, the
        // operator's own "pump a" search (2 of 2) outranks the alert itself (2 of 3).
        Assert.Contains("you switched Water Pump A on", action.Reason);

        // Accepting it issues the command, which lands on the undo stack like any other.
        var accepted = await http.PostAsJsonAsync("api/insights/accept", new SuggestionKeyRequest { Key = action.Key }, Gateway.Json);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var history = await Gateway.GetAsync<CommandHistoryResponse>(http, "api/commands");
        Assert.Equal("Suggestion", history.UndoStack.First().Origin);

        var patterns = await Gateway.GetAsync<List<PatternRuleResponse>>(http, "api/insights/patterns");
        Assert.Contains(patterns, rule => rule.ConsequentLabel == "Switched Water Pump A ON" && rule.Trusted);
    }

    private static async Task CrashAsync(HttpClient http)
    {
        await Gateway.PublishAsync(http, Gateway.Packet(Probe, "float", "7"));
        await Gateway.EventuallyAsync(async () => (await Gateway.DeviceAsync(http, Probe)).AlertState == "Critical", "the probe is critical");
    }

    private static async Task RecoverAsync(HttpClient http)
    {
        await Gateway.PublishAsync(http, Gateway.Packet(Probe, "float", "44"));
        await Gateway.EventuallyAsync(async () => (await Gateway.DeviceAsync(http, Probe)).AlertState == "Clear", "the probe has recovered");
    }
}
