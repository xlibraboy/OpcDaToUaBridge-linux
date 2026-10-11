using OpcBridge.Client;
using OpcBridge.Hmi.ViewModels.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic page's wording: state words, marks, "should X · actual Y", timer and counter
/// progress, live chips and sequence progress — the strings the plant already reads on the
/// phone, pinned so the desktop cannot drift.
/// </summary>
public sealed class HmiLogicTextTests
{
    private static LogicElementStateDto Contact(string state, bool? matches, string should, string valueText) => new()
    {
        Id = Guid.NewGuid(),
        Kind = LogicElementKinds.Contact,
        State = state,
        Should = should,
        Matches = matches,
        ValueText = valueText
    };

    [Fact]
    public void BlockStateWords_AlwaysCarryTheWord()
    {
        Assert.Equal("READY", LogicText.StateWord(LogicBlockStates.Ready));
        Assert.Equal("BLOCKED", LogicText.StateWord(LogicBlockStates.Blocked));
        Assert.Equal("DISABLED", LogicText.StateWord(LogicBlockStates.Disabled));
        Assert.Equal("NO DATA", LogicText.StateWord(null));
        Assert.Equal("ok", LogicText.StateKey(LogicBlockStates.Ready));
        Assert.Equal("bad", LogicText.StateKey(LogicBlockStates.Blocked));
        Assert.Equal("muted", LogicText.StateKey(LogicBlockStates.Disabled));
        Assert.Equal("warn", LogicText.StateKey(LogicBlockStates.Unknown));
    }

    [Fact]
    public void ContactActuals_FlipAgainstTheContactForm()
    {
        LogicElementStateDto normallyOpen = Contact(LogicConditionStates.False, false, "1", "Blocked");
        Assert.Equal("0", LogicText.Actual(normallyOpen, LogicElementKinds.Contact, LogicConditionOps.On));
        Assert.Equal("should 1 · actual 0", LogicText.ShouldLine(normallyOpen, LogicElementKinds.Contact, LogicConditionOps.On));

        LogicElementStateDto normallyClosed = Contact(LogicConditionStates.True, true, "0", "Open");
        Assert.Equal("0", LogicText.Actual(normallyClosed, LogicElementKinds.Contact, LogicConditionOps.Off));
        Assert.Equal("should 0 · actual 0", LogicText.ShouldLine(normallyClosed, LogicElementKinds.Contact, LogicConditionOps.Off));

        LogicElementStateDto unknown = new() { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact, State = LogicConditionStates.Unknown, Should = "1" };
        Assert.Equal("?", LogicText.Actual(unknown, LogicElementKinds.Contact, LogicConditionOps.On));

        LogicElementStateDto numeric = Contact(LogicConditionStates.True, true, "> 50", "62.5 %");
        Assert.Equal("62.5 %", LogicText.Actual(numeric, LogicElementKinds.Contact, LogicConditionOps.GreaterThan));
        Assert.Equal("should > 50 · actual 62.5 %", LogicText.ShouldLine(numeric, LogicElementKinds.Contact, LogicConditionOps.GreaterThan));
    }

    [Fact]
    public void GatesAndBlocks_ReadAsTheirOutputBit()
    {
        var and = new LogicElementStateDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.And, State = LogicConditionStates.True, Depth = 0 };
        Assert.Equal("AND", LogicText.ElementKindLabel(LogicElementKinds.And, null));
        Assert.Equal("1", LogicText.Actual(and, LogicElementKinds.And, null));
        Assert.Equal(string.Empty, LogicText.ShouldLine(and, LogicElementKinds.And, null));

        var ton = new LogicElementStateDto
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.Ton,
            State = LogicConditionStates.False,
            PtMs = 3000,
            ElapsedMs = 1500
        };
        Assert.Equal("TON IN", LogicText.ElementKindLabel(LogicElementKinds.Ton, null));
        Assert.Equal("holding 1.5 s of 3 s", LogicText.Progress(ton, 0));

        ton.State = LogicConditionStates.True;
        Assert.Equal("holding done · 3 s", LogicText.Progress(ton, 0));

        var tof = new LogicElementStateDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Tof, State = LogicConditionStates.False, PtMs = 5000, ElapsedMs = 2000 };
        Assert.Equal("off-delay 2 s of 5 s", LogicText.Progress(tof, 0));

        var ctu = new LogicElementStateDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Ctu, State = LogicConditionStates.False, Count = 2 };
        Assert.Equal("count 2 of 5", LogicText.Progress(ctu, 5));
    }

    [Fact]
    public void LiveChips_ReadBitsAsOneOrZero_AndKeepNumericValues()
    {
        var on = new LogicElementStateDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact, State = LogicConditionStates.True, ValueText = "Permit" };
        Assert.Equal("1", LogicText.LiveChip(on, LogicConditionOps.On));

        var off = new LogicElementStateDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact, State = LogicConditionStates.False, ValueText = "Blocked" };
        Assert.Equal("0", LogicText.LiveChip(off, LogicConditionOps.Off));

        var noData = new LogicElementStateDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact, State = LogicConditionStates.Unknown };
        Assert.Equal("no data", LogicText.LiveChip(noData, LogicConditionOps.On));

        var numeric = new LogicElementStateDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact, State = LogicConditionStates.True, ValueText = "94.2 %" };
        Assert.Equal("94.2 %", LogicText.LiveChip(numeric, LogicConditionOps.GreaterThan));
    }

    [Fact]
    public void ReasonsAndSequenceProgress_FollowThePhone()
    {
        var ready = new LogicBlockStateDto { Id = Guid.NewGuid(), State = LogicBlockStates.Ready };
        Assert.Equal("all conditions met", LogicText.ReasonLine(ready));
        Assert.Equal("all conditions met", LogicText.DetailReason(ready));

        var blocked = new LogicBlockStateDto { Id = Guid.NewGuid(), State = LogicBlockStates.Blocked, Reason = "Line 01 start permit must be given" };
        Assert.Equal("Line 01 start permit must be given", LogicText.ReasonLine(blocked));
        Assert.Equal("Blocked by: Line 01 start permit must be given", LogicText.DetailReason(blocked));

        Assert.Equal("reading…", LogicText.ReasonLine(null));

        var block = new LogicBlockDto
        {
            Steps =
            {
                new LogicStepDto { Id = Guid.NewGuid(), Name = "Open valve" },
                new LogicStepDto { Id = Guid.NewGuid(), Name = "Wait flow" },
                new LogicStepDto { Id = Guid.NewGuid(), Name = "Close valve" }
            }
        };
        var state = new LogicBlockStateDto
        {
            Id = block.Id,
            State = LogicBlockStates.Blocked,
            Steps =
            {
                new LogicStepStateDto { Id = block.Steps[0].Id, State = LogicStepStates.Done },
                new LogicStepStateDto { Id = block.Steps[1].Id, State = LogicStepStates.Current },
                new LogicStepStateDto { Id = block.Steps[2].Id, State = LogicStepStates.Pending }
            }
        };
        Assert.Equal("1/3 steps · Wait flow", LogicText.SequenceProgress(block, state));

        state.Steps[1].State = LogicStepStates.Done;
        state.Steps[2].State = LogicStepStates.Done;
        Assert.Equal("complete", LogicText.SequenceProgress(block, state));

        Assert.Equal("Done", LogicText.StepStateWord(LogicStepStates.Done));
        Assert.Equal("Current", LogicText.StepStateWord(LogicStepStates.Current));
        Assert.Equal("Pending", LogicText.StepStateWord(LogicStepStates.Pending));
        Assert.Equal("No data", LogicText.StepStateWord(null));
        Assert.Equal("✗", LogicText.Mark(LogicConditionStates.False));
        Assert.Equal("✓", LogicText.Mark(LogicConditionStates.True));
        Assert.Equal("—", LogicText.Mark(null));
    }
}
