namespace SmartX.Core.Commands;

/// <summary>
/// The manual override history behind the console's Undo and Redo buttons.
/// <para>
/// <b>Why a <see cref="Stack{T}"/>.</b> Undo always reverts the most recent
/// command first, which is exactly last-in-first-out. Pushing and popping are
/// O(1), and the top of the stack is always the command an operator means when
/// they reach for undo in a hurry - reverting "shut down all valves" seconds
/// after pressing it by mistake.
/// </para>
/// <para>
/// A second stack holds undone commands for redo. Issuing a new command clears
/// it, the same as every editor: once history has branched, re-applying an
/// undone command from the abandoned branch would act on a state that no longer
/// exists.
/// </para>
/// <para>
/// <b>Bounded.</b> A <see cref="Stack{T}"/> cannot drop from its bottom, so the
/// undo stack is allowed to grow a quarter past <see cref="MaxDepth"/> and is then
/// trimmed back in one pass. Each trim costs O(n), but happens only once every
/// <c>MaxDepth / 4</c> pushes, so pushing stays O(1) amortised. Not thread-safe:
/// the stream store serialises access.
/// </para>
/// </summary>
public sealed class CommandHistory
{
    private Stack<CommandRecord> _undo = new();
    private readonly Stack<CommandRecord> _redo = new();

    /// <summary>Creates a history.</summary>
    /// <param name="maxDepth">How many commands undo can reach back through.</param>
    public CommandHistory(int maxDepth = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        MaxDepth = maxDepth;
    }

    /// <summary>How many commands undo can reach back through.</summary>
    public int MaxDepth { get; }

    /// <summary>Commands that can be undone.</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>Commands that can be redone.</summary>
    public int RedoDepth => _redo.Count;

    /// <summary>Whether there is anything to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether there is anything to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The undo stack, top (most recent) first.</summary>
    public IReadOnlyCollection<CommandRecord> UndoStack => _undo;

    /// <summary>The redo stack, top first.</summary>
    public IReadOnlyCollection<CommandRecord> RedoStack => _redo;

    /// <summary>The command undo would revert, without reverting it.</summary>
    public CommandRecord? PeekUndo() => _undo.TryPeek(out var top) ? top : null;

    /// <summary>The command redo would re-apply, without re-applying it.</summary>
    public CommandRecord? PeekRedo() => _redo.TryPeek(out var top) ? top : null;

    /// <summary>
    /// Pushes a newly issued command. This branches history, so the redo stack
    /// is cleared.
    /// </summary>
    public void Record(CommandRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _undo.Push(record);
        _redo.Clear();
        TrimIfOverDepth();
    }

    /// <summary>
    /// Pops the most recent command so the caller can revert it, and moves it to
    /// the redo stack.
    /// </summary>
    public bool TryUndo(out CommandRecord undone)
    {
        if (!_undo.TryPop(out undone!))
        {
            return false;
        }

        _redo.Push(undone);
        return true;
    }

    /// <summary>
    /// Pops the most recently undone command so the caller can re-apply it, and
    /// moves it back onto the undo stack.
    /// </summary>
    public bool TryRedo(out CommandRecord redone)
    {
        if (!_redo.TryPop(out redone!))
        {
            return false;
        }

        _undo.Push(redone);
        return true;
    }

    private void TrimIfOverDepth()
    {
        if (_undo.Count <= MaxDepth + (MaxDepth / 4))
        {
            return;
        }

        // Enumerating a stack yields top first, so Take keeps the newest. They are
        // reversed before pushing back so the newest ends up on top again.
        var keep = _undo.Take(MaxDepth).Reverse().ToArray();
        _undo = new Stack<CommandRecord>(keep);
    }
}
