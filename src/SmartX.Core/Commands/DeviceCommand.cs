using SmartX.Core.Devices;

namespace SmartX.Core.Commands;

/// <summary>What an operator asked an actuator to do.</summary>
public enum CommandAction
{
    /// <summary>Energise the relay - open the valve, start the pump.</summary>
    SwitchOn = 0,

    /// <summary>De-energise it.</summary>
    SwitchOff = 1,

    /// <summary>Invert whatever state it is in now.</summary>
    Toggle = 2
}

/// <summary>Where a command is in its round trip to the device.</summary>
public enum CommandStatus
{
    /// <summary>Waiting in the device's downlink mailbox.</summary>
    Pending = 0,

    /// <summary>Collected by the device, not yet confirmed.</summary>
    Delivered = 1,

    /// <summary>The device confirmed it applied the state.</summary>
    Acknowledged = 2,

    /// <summary>The device never confirmed, usually because it is offline.</summary>
    TimedOut = 3,

    /// <summary>A newer command to the same device replaced it before delivery.</summary>
    Superseded = 4
}

/// <summary>Why a command was sent.</summary>
public enum CommandOrigin
{
    /// <summary>An operator issued it directly.</summary>
    Operator = 0,

    /// <summary>It reverts an earlier command, from the undo stack.</summary>
    Undo = 1,

    /// <summary>It re-applies an undone command, from the redo stack.</summary>
    Redo = 2,

    /// <summary>An operator accepted it from Suggested Actions.</summary>
    Suggestion = 3
}

/// <summary>
/// One instruction to one device, sitting in that device's downlink mailbox until
/// the device collects it.
/// <para>
/// A command carries the <i>state</i> to apply, not the action, so delivering it
/// twice or late is harmless: "be off" applied twice is still off, where "toggle"
/// applied twice would undo itself. The action an operator chose is resolved to a
/// state when the command is created.
/// </para>
/// </summary>
public sealed class DeviceCommand
{
    /// <summary>Stable key the device acknowledges against.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The history record this command belongs to.</summary>
    public required Guid RecordId { get; init; }

    /// <summary>Device it is addressed to.</summary>
    public required MacKey Target { get; init; }

    /// <summary>The relay state to apply.</summary>
    public required bool DesiredState { get; init; }

    /// <summary>Why it was sent.</summary>
    public required CommandOrigin Origin { get; init; }

    /// <summary>Where it is in the round trip.</summary>
    public CommandStatus Status { get; set; } = CommandStatus.Pending;

    /// <summary>When it was queued.</summary>
    public required DateTimeOffset IssuedUtc { get; init; }

    /// <summary>When the device collected it.</summary>
    public DateTimeOffset? DeliveredUtc { get; set; }

    /// <summary>When the device confirmed it.</summary>
    public DateTimeOffset? AcknowledgedUtc { get; set; }

    /// <summary>True once nothing further will happen to it.</summary>
    public bool IsFinished => Status is CommandStatus.Acknowledged or CommandStatus.TimedOut or CommandStatus.Superseded;
}
