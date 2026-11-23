using SmartX.Core.Commands;
using SmartX.Core.Devices;

namespace SmartX.Core.Tests.Commands;

/// <summary>
/// Covers the undo and redo stacks behind manual overrides, including the bulk
/// case: one undo must put every valve back the way it was, not simply invert them.
/// </summary>
public class CommandHistoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static MacKey Valve(int n) => MacKey.FromValue((0xA4CF12UL << 24) | (uint)n);

    private static CommandRecord Record(long number, params TargetChange[] changes) => new()
    {
        Number = number,
        Label = $"command {number}",
        Action = CommandAction.SwitchOff,
        Origin = CommandOrigin.Operator,
        Changes = changes,
        IssuedUtc = Noon.AddSeconds(number)
    };

    [Fact]
    public void Undo_RevertsTheMostRecentCommandFirst()
    {
        var history = new CommandHistory();
        history.Record(Record(1, new TargetChange(Valve(1), false, true)));
        history.Record(Record(2, new TargetChange(Valve(2), false, true)));
        history.Record(Record(3, new TargetChange(Valve(3), false, true)));

        Assert.True(history.TryUndo(out var first));
        Assert.True(history.TryUndo(out var second));

        Assert.Equal(3, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal(1, history.UndoDepth);
        Assert.Equal(2, history.RedoDepth);
    }

    [Fact]
    public void Redo_ReappliesInTheReverseOrderOfUndo()
    {
        var history = new CommandHistory();
        history.Record(Record(1));
        history.Record(Record(2));
        history.TryUndo(out _);
        history.TryUndo(out _);

        Assert.True(history.TryRedo(out var redone));

        Assert.Equal(1, redone.Number);
        Assert.Equal(1, history.PeekUndo()!.Number);
        Assert.Equal(2, history.PeekRedo()!.Number);
    }

    [Fact]
    public void ANewCommand_ClearsTheRedoStack()
    {
        var history = new CommandHistory();
        history.Record(Record(1));
        history.TryUndo(out _);
        Assert.True(history.CanRedo);

        history.Record(Record(2));

        Assert.False(history.CanRedo);
        Assert.False(history.TryRedo(out _));
    }

    [Fact]
    public void UndoOnAnEmptyHistory_ReportsNothingToDo()
    {
        var history = new CommandHistory();

        Assert.False(history.CanUndo);
        Assert.False(history.TryUndo(out _));
        Assert.Null(history.PeekUndo());
    }

    [Fact]
    public void UndoingABulkShutdown_RestoresOnlyWhatItChanged()
    {
        // "Shut down all valves": 1 and 2 were open, 3 was already shut, and 4 has
        // never reported a state at all.
        var shutdown = Record(1,
            new TargetChange(Valve(1), true, false),
            new TargetChange(Valve(2), true, false),
            new TargetChange(Valve(3), false, false),
            new TargetChange(Valve(4), null, false));

        var restore = shutdown.RestoreStates().ToList();

        Assert.Equal([(Valve(1), true), (Valve(2), true)], restore);
        Assert.Equal(4, shutdown.ReapplyStates().Count());
    }

    [Fact]
    public void TheStack_IsTrimmedToItsDepthKeepingTheNewest()
    {
        var history = new CommandHistory(maxDepth: 8);
        for (var n = 1; n <= 40; n++)
        {
            history.Record(Record(n));
        }

        Assert.InRange(history.UndoDepth, 8, 10);
        Assert.Equal(40, history.PeekUndo()!.Number);

        // Still strictly newest-first all the way down after the trims.
        var numbers = history.UndoStack.Select(record => record.Number).ToList();
        Assert.Equal(numbers.OrderByDescending(number => number), numbers);
    }

    [Theory]
    [InlineData(CommandAction.SwitchOn, null, true)]
    [InlineData(CommandAction.SwitchOff, true, false)]
    [InlineData(CommandAction.Toggle, true, false)]
    [InlineData(CommandAction.Toggle, false, true)]
    [InlineData(CommandAction.Toggle, null, true)]
    public void AnAction_ResolvesToAState(CommandAction action, bool? current, bool expected)
        => Assert.Equal(expected, CommandRecord.Resolve(action, current));
}
