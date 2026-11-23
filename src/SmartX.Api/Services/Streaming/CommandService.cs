using SmartX.Contracts.Commands;
using SmartX.Contracts.Stream;
using SmartX.Core.Commands;
using SmartX.Core.Devices;
using SmartX.Core.Insights;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services.Streaming;

/// <summary>Outcome of a command operation, with the HTTP status it maps to.</summary>
/// <param name="Status">200 on success, otherwise the status to return.</param>
/// <param name="Message">What happened, for the operator.</param>
/// <param name="Outcome">The resulting record and stacks, on success.</param>
public sealed record CommandResult(int Status, string Message, CommandOutcomeResponse? Outcome)
{
    /// <summary>Whether it worked.</summary>
    public bool Succeeded => Status is >= 200 and < 300;
}

/// <summary>
/// Manual overrides: issuing them, undoing and redoing them, and the round trip
/// to the device.
/// <para>
/// <b>The round trip.</b> A command does not switch anything by itself. It is
/// placed in the target device's downlink mailbox - a <see cref="Queue{T}"/>, so a
/// device applies its commands in the order they were issued - and the device
/// collects it on its next poll, applies it, and acknowledges. Only the
/// acknowledgement moves the device's reported state. Until then the console shows
/// the command as pending, and if no acknowledgement arrives within the timeout it
/// is marked timed out: the honest answer for a pump that is offline.
/// </para>
/// <para>
/// <b>Undo is a new command.</b> Popping the undo stack does not rewind time; the
/// valves have physically moved. Undo sends compensating commands that put each
/// device back in the state the record says it was in before, through the same
/// mailbox, with the same acknowledgement.
/// </para>
/// </summary>
public sealed class CommandService(CommandStreamStore store)
{
    /// <summary>Issues an override to one or more actuators.</summary>
    public CommandResult Issue(IssueCommandRequest request, CommandOrigin origin = CommandOrigin.Operator)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<CommandAction>(request.Action, ignoreCase: true, out var action))
        {
            return new CommandResult(400, $"'{request.Action}' is not a command action.", null);
        }

        var targets = new List<MacKey>();
        foreach (var raw in request.Targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!MacKey.TryParse(raw, out var mac))
            {
                return new CommandResult(400, $"'{raw}' is not a valid MAC address.", null);
            }

            if (!targets.Contains(mac))
            {
                targets.Add(mac);
            }
        }

        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            var devices = new List<DeviceState>(targets.Count);
            foreach (var mac in targets)
            {
                if (!state.Devices.TryGet(mac, out var device))
                {
                    return new CommandResult(404, $"No device is registered under {mac}.", null);
                }

                if (!device.IsActuator)
                {
                    return new CommandResult(400,
                        $"{device.DisplayName} is not an actuator ({device.Category}) - it has no relay to switch.", null);
                }

                devices.Add(device);
            }

            var record = new CommandRecord
            {
                Number = ++state.CommandNumber,
                Label = string.IsNullOrWhiteSpace(request.Label) ? DefaultLabel(action, devices) : request.Label.Trim(),
                Action = action,
                Origin = origin,
                IssuedUtc = now,
                Changes = [.. devices.Select(device =>
                {
                    var previous = device.EffectiveState;
                    return new TargetChange(device.Mac, previous, CommandRecord.Resolve(action, previous));
                })]
            };

            foreach (var change in record.Changes)
            {
                Dispatch(state, change.Target, change.NewState, record.Id, origin, now);

                // Recorded as the state produced, not the button pressed - see ActivityToken.
                state.Miner.Observe(ActivityToken.Command(change.NewState, change.Target), now);
            }

            state.History.Record(record);

            state.Log(
                StreamEventKind.CommandIssued,
                StreamEventLevel.Info,
                now,
                devices.Count == 1 ? devices[0].Mac : null,
                $"#{record.Number} {record.Label}",
                Describe(state, record) + (origin == CommandOrigin.Suggestion ? " Issued from Suggested Actions." : string.Empty));

            state.CommandsChanged = true;
            state.SuggestionsDirty = true;

            return new CommandResult(200, $"Command #{record.Number} sent: {record.Label}.", Outcome(state, record, $"Sent: {record.Label}."));
        });
    }

    /// <summary>Reverts the most recent command.</summary>
    public CommandResult Undo()
    {
        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            if (!state.History.TryUndo(out var record))
            {
                return new CommandResult(409, "There is nothing to undo.", null);
            }

            var restored = 0;
            foreach (var (target, previous) in record.RestoreStates())
            {
                if (state.Devices.Contains(target))
                {
                    Dispatch(state, target, previous, record.Id, CommandOrigin.Undo, now);
                    restored++;
                }
            }

            var detail = restored == 0
                ? "No device needed to change: every target was already in its earlier state, or had never reported one."
                : $"Sent {restored} compensating command{(restored == 1 ? string.Empty : "s")} restoring the state before #{record.Number}.";

            state.Log(StreamEventKind.CommandUndone, StreamEventLevel.Warning, now,
                record.Changes.Count == 1 ? record.Changes[0].Target : null,
                $"Undid #{record.Number} {record.Label}", detail);

            state.CommandsChanged = true;
            state.SuggestionsDirty = true;

            return new CommandResult(200, $"Undid #{record.Number}: {record.Label}.", Outcome(state, record, detail));
        });
    }

    /// <summary>Re-applies the most recently undone command.</summary>
    public CommandResult Redo()
    {
        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            if (!state.History.TryRedo(out var record))
            {
                return new CommandResult(409, "There is nothing to redo.", null);
            }

            foreach (var (target, desired) in record.ReapplyStates())
            {
                if (state.Devices.Contains(target))
                {
                    Dispatch(state, target, desired, record.Id, CommandOrigin.Redo, now);
                }
            }

            state.Log(StreamEventKind.CommandRedone, StreamEventLevel.Info, now,
                record.Changes.Count == 1 ? record.Changes[0].Target : null,
                $"Redid #{record.Number} {record.Label}", Describe(state, record));

            state.CommandsChanged = true;
            state.SuggestionsDirty = true;

            var message = $"Re-applied #{record.Number}: {record.Label}.";
            return new CommandResult(200, message, Outcome(state, record, message));
        });
    }

    /// <summary>Both stacks, top first.</summary>
    public CommandHistoryResponse History() => store.Read(StreamMapper.History);

    /// <summary>
    /// Hands devices the commands waiting for them and marks them delivered. Only
    /// devices with something waiting are visited - the poll walks the set of
    /// non-empty mailboxes, not the fleet.
    /// </summary>
    /// <param name="only">Restrict to these devices, e.g. the ones an emulator currently has online. Null for all.</param>
    public DownlinkResponse CollectDownlink(IReadOnlyCollection<MacKey>? only)
    {
        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            var response = new DownlinkResponse();
            var emptied = new List<MacKey>();

            foreach (var mac in state.PendingMailboxes)
            {
                if ((only is not null && !only.Contains(mac)) || !state.Devices.TryGet(mac, out var device))
                {
                    continue;
                }

                while (device.Downlink.TryDequeue(out var command))
                {
                    // A superseded command was replaced before the device ever saw it.
                    if (command.Status != CommandStatus.Pending)
                    {
                        continue;
                    }

                    command.Status = CommandStatus.Delivered;
                    command.DeliveredUtc = now;

                    response.Commands.Add(new DownlinkCommandResponse
                    {
                        CommandId = command.Id,
                        MacAddress = mac.ToString(),
                        DesiredState = command.DesiredState,
                        IssuedUtc = command.IssuedUtc,
                        Origin = command.Origin.ToString()
                    });
                }

                emptied.Add(mac);
                state.ChangedDevices.Add(mac);
            }

            state.PendingMailboxes.ExceptWith(emptied);

            if (response.Commands.Count > 0)
            {
                state.CommandsChanged = true;
            }

            return response;
        });
    }

    /// <summary>Records a device confirming it applied a command.</summary>
    /// <returns>404 when the command is unknown, 409 when it already finished.</returns>
    public CommandResult Acknowledge(Guid commandId, bool appliedState)
    {
        var now = DateTimeOffset.UtcNow;

        return store.Write(state =>
        {
            if (!state.Commands.TryGetValue(commandId, out var command))
            {
                return new CommandResult(404, "No such command.", null);
            }

            if (command.Status is CommandStatus.Acknowledged)
            {
                return new CommandResult(200, "Already acknowledged.", null);
            }

            // A late acknowledgement still tells the truth about the relay, so it is
            // recorded even for a command that had been given up on.
            var wasTimedOut = command.Status == CommandStatus.TimedOut;
            command.Status = CommandStatus.Acknowledged;
            command.AcknowledgedUtc = now;

            if (state.Devices.TryGet(command.Target, out var device))
            {
                device.ConfirmState(appliedState);
                state.ChangedDevices.Add(device.Mac);

                var latency = (now - command.IssuedUtc).TotalMilliseconds;
                state.Log(StreamEventKind.CommandAcknowledged, StreamEventLevel.Info, now, device.Mac,
                    $"{device.DisplayName} confirmed {(appliedState ? "ON" : "OFF")}",
                    $"Round trip {latency:0} ms from issue to acknowledgement{(wasTimedOut ? ", after it had timed out" : string.Empty)}.");
            }

            state.CommandsChanged = true;
            state.SuggestionsDirty = true;
            return new CommandResult(200, "Acknowledged.", null);
        });
    }

    /// <summary>
    /// Marks commands that were never confirmed as timed out, and forgets finished
    /// commands old enough that no stack entry still shows them. The caller holds the lock.
    /// </summary>
    /// <returns>Commands that timed out on this pass.</returns>
    public static int ExpireStale(StreamState state, DateTimeOffset now, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(state);

        var expired = 0;
        var forget = new List<Guid>();

        foreach (var command in state.Commands.Values)
        {
            if (!command.IsFinished && now - command.IssuedUtc > timeout)
            {
                command.Status = CommandStatus.TimedOut;
                expired++;

                state.ChangedDevices.Add(command.Target);
                state.Log(StreamEventKind.CommandTimedOut, StreamEventLevel.Warning, now, command.Target,
                    $"{state.NameOf(command.Target)} did not confirm {(command.DesiredState ? "ON" : "OFF")}",
                    command.DeliveredUtc is null
                        ? $"The device never collected the command in {timeout.TotalSeconds:0} s - it is probably offline."
                        : $"The device collected the command but did not acknowledge it within {timeout.TotalSeconds:0} s.");
            }
            else if (command.IsFinished && now - command.IssuedUtc > TimeSpan.FromHours(1))
            {
                forget.Add(command.Id);
            }
        }

        foreach (var id in forget)
        {
            state.Commands.Remove(id);
        }

        if (expired > 0)
        {
            state.CommandsChanged = true;
            state.SuggestionsDirty = true;
        }

        return expired;
    }

    // Callers hold the lock.
    private static void Dispatch(StreamState state, MacKey target, bool desired, Guid recordId, CommandOrigin origin, DateTimeOffset now)
    {
        if (!state.Devices.TryGet(target, out var device))
        {
            return;
        }

        // Anything still waiting for this device is replaced: the device should go
        // straight to the newest state, not replay a sequence nobody wants any more.
        foreach (var waiting in device.Downlink)
        {
            if (waiting.Status == CommandStatus.Pending)
            {
                waiting.Status = CommandStatus.Superseded;
            }
        }

        device.Downlink.Clear();

        var command = new DeviceCommand
        {
            RecordId = recordId,
            Target = target,
            DesiredState = desired,
            Origin = origin,
            IssuedUtc = now
        };

        device.Downlink.Enqueue(command);
        device.DesiredState = desired;
        state.Commands[command.Id] = command;
        state.PendingMailboxes.Add(target);
        state.ChangedDevices.Add(target);
    }

    private static CommandOutcomeResponse Outcome(StreamState state, CommandRecord record, string message) => new()
    {
        Message = message,
        Record = StreamMapper.Record(state, record),
        History = StreamMapper.History(state)
    };

    private static string DefaultLabel(CommandAction action, IReadOnlyList<DeviceState> devices)
    {
        var verb = action switch
        {
            CommandAction.SwitchOn => "Switch ON",
            CommandAction.SwitchOff => "Switch OFF",
            _ => "Toggle"
        };

        return devices.Count == 1 ? $"{verb} {devices[0].DisplayName}" : $"{verb} {devices.Count} actuators";
    }

    private static string Describe(StreamState state, CommandRecord record)
        => string.Join("; ", record.Changes.Select(change =>
            $"{state.NameOf(change.Target)} {Word(change.PreviousState)} → {Word(change.NewState)}")) + ".";

    private static string Word(bool? state) => state switch
    {
        true => "ON",
        false => "OFF",
        null => "unknown"
    };
}
