namespace SmartX.DeviceEmulator;

/// <summary>
/// Runs the virtual fleet: publishes every few seconds, collects and acknowledges
/// commands every second, and turns key presses into field incidents.
/// </summary>
internal sealed class Emulator(GatewayClient gateway, EmulatorOptions options)
{
    public const string Usage = """
        Smart-X device emulator - a virtual ESP32 fleet that talks to the gateway over HTTP.

          --api <url>          Gateway address (default http://localhost:5080, or SMARTX_API)
          --interval <s>       Seconds between publishes (default 5)
          --no-keys            Do not read the keyboard (automatic when input is redirected)
          --incidents <min>    Trigger a random spike or moisture crash every <min> minutes
          --gateway-id <id>    Name reported for the border router (default border-router-01)

        Keys while running:
          S  power spike on Lighting Rail Meter 13      (priority lane)
          M  moisture crash on Soil Moisture Probe 01   (priority lane; recovers once Water Pump A is on)
          D  take Mesh Repeater 08 offline / bring it back   (one disconnect alert, however long it stays down)
          F  flood the standard lane with 500 routine packets
          L  list the fleet
          Q  quit
        """;

    private readonly VirtualFleet _fleet = new();
    private readonly Random _random = new();
    private DateTimeOffset? _gatewayStarted;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Smart-X device emulator -> {gateway.BaseAddress}");
        await WaitForGatewayAsync(cancellationToken);
        await ProvisionAsync(cancellationToken);

        if (options.Interactive)
        {
            Console.WriteLine("Keys: [S]pike  [M]oisture crash  [D]rop node  [F]lood  [L]ist  [Q]uit");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var loops = new List<Task>
        {
            PublishLoopAsync(stop.Token),
            DownlinkLoopAsync(stop.Token),
            ProvisionLoopAsync(stop.Token)
        };

        if (options.Interactive)
        {
            loops.Add(KeyLoopAsync(stop));
        }

        if (options.IncidentEvery is { } every)
        {
            loops.Add(IncidentLoopAsync(every, stop.Token));
        }

        await Task.WhenAny(loops);
        await stop.CancelAsync();

        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private async Task WaitForGatewayAsync(CancellationToken cancellationToken)
    {
        var announced = false;
        while (!await gateway.IsHealthyAsync(cancellationToken))
        {
            if (!announced)
            {
                Console.WriteLine("Waiting for the gateway - start it with: dotnet run --project src/SmartX.Api");
                announced = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        _gatewayStarted ??= await gateway.StartedAtAsync(cancellationToken);

        var manifest = await gateway.ManifestAsync(cancellationToken);
        if (manifest is null)
        {
            return;
        }

        var added = _fleet.Merge(manifest);
        if (added > 0)
        {
            Console.WriteLine($"Provisioned {added} node{(added == 1 ? string.Empty : "s")} " +
                              $"({manifest.Withheld} registered node{(manifest.Withheld == 1 ? " is" : "s are")} withheld, e.g. the deliberately silent one).");
        }
    }

    private async Task ProvisionLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await ProvisionAsync(cancellationToken);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The publish loop reports an unreachable gateway; no need to say it twice.
            }
        }
    }

    /// <summary>Publish ticks between neighbour-table reports: every 30 s at the default 5 s interval.</summary>
    private const int NeighbourReportEveryTicks = 6;

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        var tick = 0;
        var reportedDown = false;

        do
        {
            tick++;

            try
            {
                await ReprovisionIfRestartedAsync(cancellationToken);

                var frame = _fleet.Tick(options.GatewayId);
                if (frame.Packets.Count == 0)
                {
                    continue;
                }

                var receipt = await gateway.PublishAsync(frame, cancellationToken);
                reportedDown = false;

                if (receipt is null)
                {
                    continue;
                }

                var priority = receipt.Priority > 0 ? $", {receipt.Priority} PRIORITY" : string.Empty;
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] tick {tick,4}: {receipt.Received} packets -> {receipt.Accepted} queued{priority}" +
                    $" | lookup {receipt.MeanLookupNanoseconds:0} ns over {receipt.FleetSize} devices");

                // Neighbour tables are advertised far less often than telemetry, as
                // mesh protocols do: links change slowly, and the reports cost airtime.
                if (tick % NeighbourReportEveryTicks == 1 && _fleet.NeighbourTables(options.GatewayId) is { } tables)
                {
                    var applied = await gateway.ReportNeighboursAsync(tables, cancellationToken);
                    if (applied is not null)
                    {
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] neighbour tables: {applied.ReportsAccepted} nodes, " +
                            $"{applied.LinksUpdated} links refreshed, {applied.LinksDiscovered} discovered");
                    }
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (!reportedDown)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] gateway unreachable - will keep trying.");
                    reportedDown = true;
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    /// <summary>
    /// A gateway that restarts comes back with freshly seeded history. Carrying on from
    /// the values the old one held would put a step into every node's signal, and the
    /// anomaly detector would rightly flag it on every packet until its window caught
    /// up. So before each publish the emulator checks the gateway's start time, and if
    /// it has changed, takes its starting values from the new manifest instead.
    /// </summary>
    private async Task ReprovisionIfRestartedAsync(CancellationToken cancellationToken)
    {
        var started = await gateway.StartedAtAsync(cancellationToken);
        if (started is null || started == _gatewayStarted)
        {
            return;
        }

        var manifest = await gateway.ManifestAsync(cancellationToken);
        if (manifest is null)
        {
            return;
        }

        var restarted = _gatewayStarted is not null;
        _gatewayStarted = started;

        if (restarted)
        {
            _fleet.Reset(manifest);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] gateway restarted - re-provisioned {manifest.Devices.Count} nodes from its new history.");
        }
    }

    /// <summary>
    /// Polls once a second for commands addressed to the nodes that are online -
    /// a node the operator has taken offline does not collect its commands, so
    /// they time out at the gateway exactly as they would for a dead board.
    /// </summary>
    private async Task DownlinkLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var downlink = await gateway.CollectAsync(_fleet.OnlineMacs(), cancellationToken);

                foreach (var command in downlink?.Commands ?? [])
                {
                    // A relay takes a moment to throw.
                    await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);

                    if (!_fleet.Apply(command.MacAddress, command.DesiredState))
                    {
                        continue;
                    }

                    var acknowledged = await gateway.AcknowledgeAsync(command.CommandId, command.DesiredState, cancellationToken);
                    var name = _fleet.Find(command.MacAddress)?.DisplayName ?? command.MacAddress;

                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] downlink: {name} -> {(command.DesiredState ? "ON" : "OFF")} " +
                        $"({command.Origin}){(acknowledged ? ", acknowledged" : ", ack refused")}");
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Reported by the publish loop.
            }
        }
    }

    private async Task KeyLoopAsync(CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(50, stop.Token);
                continue;
            }

            var key = Console.ReadKey(intercept: true).Key;

            switch (key)
            {
                case ConsoleKey.S:
                    await SpikeAsync(stop.Token);
                    break;
                case ConsoleKey.M:
                    await CrashAsync(stop.Token);
                    break;
                case ConsoleKey.D:
                    ToggleNode();
                    break;
                case ConsoleKey.F:
                    await FloodAsync(stop.Token);
                    break;
                case ConsoleKey.L:
                    List();
                    break;
                case ConsoleKey.H:
                    Console.WriteLine(Usage);
                    break;
                case ConsoleKey.Q or ConsoleKey.Escape:
                    await stop.CancelAsync();
                    return;
            }
        }
    }

    private async Task IncidentLoopAsync(TimeSpan every, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(every);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (_random.Next(2) == 0)
            {
                await SpikeAsync(cancellationToken);
            }
            else
            {
                await CrashAsync(cancellationToken);
            }
        }
    }

    private async Task SpikeAsync(CancellationToken cancellationToken)
    {
        var meter = _fleet.Pick(device => device.IsPowerMeter && device.Online, "Lighting Rail Meter 13");
        if (meter is null)
        {
            Console.WriteLine("No power meter is online to spike.");
            return;
        }

        meter.Mode = DeviceMode.Spiking;
        meter.SpikeTicks = 6;
        meter.Value = meter.Baseline * 3.4d;

        await PublishNowAsync(meter, $"POWER SPIKE on {meter.DisplayName}: {meter.Describe()} " +
                                     "(ends after ~30 s, or at once if its grow-light relay is switched off)", cancellationToken);
    }

    private async Task CrashAsync(CancellationToken cancellationToken)
    {
        var probe = _fleet.Pick(device => device.IsMoistureProbe && device.Online, "Soil Moisture Probe 01");
        if (probe is null)
        {
            Console.WriteLine("No moisture probe is online to crash.");
            return;
        }

        probe.Mode = DeviceMode.Crashed;
        probe.Value = 7d;

        await PublishNowAsync(probe, $"MOISTURE CRASH on {probe.DisplayName}: {probe.Describe()} " +
                                     "(stays low until a pump or valve in its zone is switched on)", cancellationToken);
    }

    private void ToggleNode()
    {
        var node = _fleet.Pick(device => !device.IsActuator, "Mesh Repeater 08");
        if (node is null)
        {
            return;
        }

        node.Online = !node.Online;
        Console.WriteLine(node.Online
            ? $"[{DateTime.Now:HH:mm:ss}] {node.DisplayName} is back online."
            : $"[{DateTime.Now:HH:mm:ss}] {node.DisplayName} is OFFLINE - the gateway flags it after two missed heartbeats (~2 min), once.");
    }

    private async Task FloodAsync(CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await gateway.PublishAsync(_fleet.Flood(options.GatewayId, 500), cancellationToken);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FLOOD: {receipt?.Accepted ?? 0} routine packets queued. Press S or M now to watch an alert jump them.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Flood failed - the gateway is unreachable.");
        }
    }

    private async Task PublishNowAsync(VirtualDevice device, string message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        try
        {
            var receipt = await gateway.PublishAsync(_fleet.Immediate(options.GatewayId, device), cancellationToken);
            if (receipt?.Receipts.FirstOrDefault() is { } first)
            {
                Console.WriteLine($"           -> {first.Lane} lane, ahead of {first.StandardWaiting} waiting packets: {first.Reason}");
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("           -> gateway unreachable.");
        }
    }

    private void List()
    {
        Console.WriteLine();
        foreach (var device in _fleet.Devices)
        {
            var mode = device.Mode == DeviceMode.Normal ? string.Empty : $"  [{device.Mode}]";
            Console.WriteLine($"  {device.MacAddress}  {device.DisplayName,-24} {device.Zone,-22} {device.Describe(),9}" +
                              $"{(device.Online ? string.Empty : "  OFFLINE")}{mode}");
        }

        Console.WriteLine();
    }
}
