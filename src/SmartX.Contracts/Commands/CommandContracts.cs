using System.ComponentModel.DataAnnotations;

namespace SmartX.Contracts.Commands;

/// <summary>A manual override issued from the console.</summary>
public sealed class IssueCommandRequest
{
    /// <summary>"SwitchOn", "SwitchOff" or "Toggle".</summary>
    [Required]
    [RegularExpression("^(SwitchOn|SwitchOff|Toggle)$", ErrorMessage = "Action must be SwitchOn, SwitchOff or Toggle.")]
    public string Action { get; set; } = "SwitchOn";

    /// <summary>The actuators to switch. More than one makes a bulk command that undoes in one step.</summary>
    [Required]
    [MinLength(1, ErrorMessage = "Name at least one device.")]
    [MaxLength(500)]
    public List<string> Targets { get; set; } = [];

    /// <summary>Operator-facing description, e.g. "Shut down all valves". Generated when omitted.</summary>
    [StringLength(80)]
    public string? Label { get; set; }
}

/// <summary>One device's part in a command.</summary>
public sealed class CommandTargetResponse
{
    public string MacAddress { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>State before the command; null if it had never reported one.</summary>
    public bool? PreviousState { get; set; }

    public bool NewState { get; set; }

    /// <summary>The downlink command carrying this change.</summary>
    public Guid? CommandId { get; set; }

    /// <summary>"Pending", "Delivered", "Acknowledged", "TimedOut" or "Superseded".</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>A command as it sits on the undo or redo stack.</summary>
public sealed class CommandRecordResponse
{
    public Guid Id { get; set; }

    public long Number { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    /// <summary>"Operator", "Undo", "Redo" or "Suggestion".</summary>
    public string Origin { get; set; } = string.Empty;

    public DateTimeOffset IssuedUtc { get; set; }

    public List<CommandTargetResponse> Targets { get; set; } = [];
}

/// <summary>Both command stacks, top first.</summary>
public sealed class CommandHistoryResponse
{
    public List<CommandRecordResponse> UndoStack { get; set; } = [];

    public List<CommandRecordResponse> RedoStack { get; set; } = [];

    public bool CanUndo { get; set; }

    public bool CanRedo { get; set; }

    /// <summary>How far back undo can reach.</summary>
    public int MaxDepth { get; set; }
}

/// <summary>What issuing, undoing or redoing a command did.</summary>
public sealed class CommandOutcomeResponse
{
    public string Message { get; set; } = string.Empty;

    /// <summary>The record that was issued, undone or redone.</summary>
    public CommandRecordResponse? Record { get; set; }

    /// <summary>The stacks after the operation.</summary>
    public CommandHistoryResponse History { get; set; } = new();
}
