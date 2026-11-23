using SmartX.Core.Devices;

namespace SmartX.Core.Commands;

/// <summary>
/// One device's part in a command: the state it was in before, and the state it
/// was told to take.
/// </summary>
/// <param name="Target">The device.</param>
/// <param name="PreviousState">Its state before the command, or null if it had never reported one.</param>
/// <param name="NewState">The state it was told to take.</param>
public readonly record struct TargetChange(MacKey Target, bool? PreviousState, bool NewState);

/// <summary>
/// An operator command as it is kept on the undo stack: what was done, to which
/// devices, and what each of them looked like beforehand.
/// <para>
/// <b>Why it snapshots the previous state.</b> Undo is not "do the opposite". If
/// an operator shuts every valve and two of the six were already shut, the
/// opposite - open all six - would open two valves that nobody had opened. The
/// record keeps each target's prior state, so undo puts back exactly what was
/// there, and one record covers a bulk action across the whole fleet, so a single
/// undo reverts "shut down all valves" in one step.
/// </para>
/// </summary>
public sealed class CommandRecord
{
    /// <summary>Stable key.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Running number, so the console can say "command #12".</summary>
    public required long Number { get; init; }

    /// <summary>Operator-facing description, e.g. "Shut down all valves".</summary>
    public required string Label { get; init; }

    /// <summary>The action the operator chose.</summary>
    public required CommandAction Action { get; init; }

    /// <summary>Why it was issued.</summary>
    public required CommandOrigin Origin { get; init; }

    /// <summary>Every device it touched, with that device's prior state.</summary>
    public required IReadOnlyList<TargetChange> Changes { get; init; }

    /// <summary>When it was issued.</summary>
    public required DateTimeOffset IssuedUtc { get; init; }

    /// <summary>
    /// The state each target should go back to on undo. Targets whose prior state
    /// was never reported are left out: there is nothing known to restore them to,
    /// and guessing "off" could cut power to something that was running.
    /// </summary>
    public IEnumerable<(MacKey Target, bool State)> RestoreStates()
    {
        foreach (var change in Changes)
        {
            if (change.PreviousState is { } previous && previous != change.NewState)
            {
                yield return (change.Target, previous);
            }
        }
    }

    /// <summary>The state each target should go to again on redo.</summary>
    public IEnumerable<(MacKey Target, bool State)> ReapplyStates()
    {
        foreach (var change in Changes)
        {
            yield return (change.Target, change.NewState);
        }
    }

    /// <summary>
    /// Resolves an action to the state it means for a device in
    /// <paramref name="currentState"/>. A toggle of a device that has never reported
    /// a state switches it on, the safe assumption being that it is idle.
    /// </summary>
    public static bool Resolve(CommandAction action, bool? currentState) => action switch
    {
        CommandAction.SwitchOn => true,
        CommandAction.SwitchOff => false,
        CommandAction.Toggle => !(currentState ?? false),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown command action.")
    };
}
