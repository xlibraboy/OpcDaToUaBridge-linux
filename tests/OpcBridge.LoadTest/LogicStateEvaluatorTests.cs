using OpcBridge.App;
using OpcBridge.Client;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class LogicStateEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static Func<string, string, BridgeValueSnapshot?> Values(
        params (string SourceId, string ItemId, object? Value, bool Good)[] entries)
    {
        Dictionary<string, BridgeValueSnapshot> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string sourceId, string itemId, object? value, bool good) in entries)
        {
            map[LogicStateEvaluator.Key(sourceId, itemId)] =
                new BridgeValueSnapshot(sourceId, itemId, value, Now, good ? 192 : 0, good, Now);
        }

        return (sourceId, itemId) =>
            map.TryGetValue(LogicStateEvaluator.Key(sourceId, itemId), out BridgeValueSnapshot snapshot) ? snapshot : null;
    }

    private static Func<string, string, TagMapping?> Mappings(params TagMapping[] mappings)
    {
        IReadOnlyDictionary<string, TagMapping> index = LogicStateEvaluator.BuildMappingIndex(mappings);
        return (sourceId, itemId) =>
            index.TryGetValue(LogicStateEvaluator.Key(sourceId, itemId), out TagMapping mapping) ? mapping : null;
    }

    /// <summary>
    /// The contacts of a block's evaluated network, in authored order — the simple form's
    /// leaves, whatever the expansion wrapped them in.
    /// </summary>
    private static IReadOnlyList<LogicElementStateDto> Contacts(LogicBlockStateDto state) =>
        state.Elements.Where(element => element.Kind == LogicElementKinds.Contact).ToList();

    private static LogicConditionDto Condition(
        string text = "Line 01 start permit must be given",
        string op = LogicConditionOps.On,
        double? value = null,
        string sourceId = "sim",
        string itemId = "Permit",
        string severity = LogicConditionSeverities.Block,
        string? nextStep = null,
        string group = "",
        int holdMs = 0,
        string tag = "")
    {
        return new LogicConditionDto
        {
            Id = Guid.NewGuid(),
            Text = text,
            SourceId = tag.Length > 0 ? string.Empty : sourceId,
            ItemId = tag.Length > 0 ? string.Empty : itemId,
            Tag = tag,
            Op = op,
            Value = value,
            Severity = severity,
            NextStepText = nextStep,
            Group = group,
            HoldMs = holdMs
        };
    }

    private static LogicBlockDto Flat(params LogicConditionDto[] conditions)
    {
        return new LogicBlockDto
        {
            Id = Guid.NewGuid(),
            Name = "Line 01 Start",
            Kind = LogicBlockKinds.Interlock,
            Conditions = conditions.ToList()
        };
    }

    private static LogicBlockDto Sequence(params LogicStepDto[] steps)
    {
        return new LogicBlockDto
        {
            Id = Guid.NewGuid(),
            Name = "Line 01 Start",
            Kind = LogicBlockKinds.Sequence,
            Steps = steps.ToList()
        };
    }

    private static LogicStepDto Step(string name, string? completionItemId = null, params LogicConditionDto[] conditions)
    {
        return new LogicStepDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Conditions = conditions.ToList(),
            CompletionSourceId = completionItemId is null ? null : "sim",
            CompletionItemId = completionItemId
        };
    }

    [Fact]
    public void Flat_Ready_WhenEveryBlockConditionIsTrue()
    {
        LogicBlockDto block = Flat(Condition(), Condition("Valve 02 must be open", itemId: "Valve02"));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Permit", true, true), ("sim", "Valve02", true, true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Ready, state.State);
        Assert.Null(state.Reason);
        Assert.All(Contacts(state), condition => Assert.Equal(LogicConditionStates.True, condition.State));
    }

    [Fact]
    public void Flat_Blocked_WithFirstFailingConditionTextAsReason()
    {
        LogicBlockDto block = Flat(
            Condition("Line 01 start permit must be given"),
            Condition("Valve 02 must be open", itemId: "Valve02"));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Permit", false, true), ("sim", "Valve02", false, true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Blocked, state.State);
        Assert.Equal("Line 01 start permit must be given", state.Reason);
        Assert.Equal(LogicConditionStates.False, Contacts(state)[0].State);
    }

    [Fact]
    public void Flat_Unknown_WhenTagHasNoValue_AndReasonNamesTheTag()
    {
        LogicBlockDto block = Flat(Condition(itemId: "Permit"));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(),
            Mappings(new TagMapping { SourceId = "sim", ItemId = "Permit", DisplayName = "Line 01 Start Permit" }));

        Assert.Equal(LogicBlockStates.Unknown, state.State);
        Assert.Equal("no data for Line 01 Start Permit", state.Reason);
        Assert.Equal(LogicConditionStates.Unknown, Contacts(state)[0].State);
        Assert.Equal("—", Contacts(state)[0].ValueText);
        Assert.Null(Contacts(state)[0].TimestampUtc);
    }

    [Fact]
    public void Flat_Unknown_WhenValueQualityIsBad()
    {
        LogicBlockDto block = Flat(Condition());

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Permit", true, false)),
            Mappings());

        Assert.Equal(LogicBlockStates.Unknown, state.State);
        Assert.Equal("no data for Permit", state.Reason);
    }

    [Fact]
    public void Flat_WarnConditionDoesNotBlock()
    {
        LogicBlockDto block = Flat(
            Condition("Line 01 start permit must be given"),
            Condition("High level alarm should be normal", itemId: "Alarm01", severity: LogicConditionSeverities.Warn));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Permit", true, true), ("sim", "Alarm01", false, true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Ready, state.State);
        Assert.Null(state.Reason);
        Assert.Equal(LogicConditionStates.False, Contacts(state)[1].State);
    }

    [Fact]
    public void Digital_ByteZeroAndOneResolveOnOff()
    {
        LogicBlockDto onBlock = Flat(Condition(op: LogicConditionOps.On, itemId: "Mode"));
        LogicBlockDto offBlock = Flat(Condition("Plant must be in manual", op: LogicConditionOps.Off, itemId: "Mode"));

        LogicBlockStateDto onState = LogicStateEvaluator.EvaluateBlock(
            onBlock, Values(("sim", "Mode", (byte)1, true)), Mappings());
        LogicBlockStateDto offState = LogicStateEvaluator.EvaluateBlock(
            offBlock, Values(("sim", "Mode", (byte)0, true)), Mappings());

        Assert.Equal(LogicBlockStates.Ready, onState.State);
        Assert.Equal(LogicBlockStates.Ready, offState.State);
    }

    [Fact]
    public void NumericComparisons_BehaveAndNonNumericIsUnknown()
    {
        LogicBlockStateDto above = LogicStateEvaluator.EvaluateBlock(
            Flat(Condition(op: LogicConditionOps.GreaterThan, value: 5.0, itemId: "Level")),
            Values(("sim", "Level", 5.5, true)),
            Mappings());
        LogicBlockStateDto below = LogicStateEvaluator.EvaluateBlock(
            Flat(Condition(op: LogicConditionOps.LessThan, value: 5.0, itemId: "Level")),
            Values(("sim", "Level", 4.0, true)),
            Mappings());
        LogicBlockStateDto equal = LogicStateEvaluator.EvaluateBlock(
            Flat(Condition(op: LogicConditionOps.Equal, value: 2.5, itemId: "Level")),
            Values(("sim", "Level", 2.5, true)),
            Mappings());
        LogicBlockStateDto notEqual = LogicStateEvaluator.EvaluateBlock(
            Flat(Condition(op: LogicConditionOps.Equal, value: 2.5, itemId: "Level")),
            Values(("sim", "Level", 2.6, true)),
            Mappings());
        LogicBlockStateDto nonNumeric = LogicStateEvaluator.EvaluateBlock(
            Flat(Condition(op: LogicConditionOps.GreaterThan, value: 5.0, itemId: "Level")),
            Values(("sim", "Level", "not-a-number", true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Ready, above.State);
        Assert.Equal(LogicBlockStates.Ready, below.State);
        Assert.Equal(LogicBlockStates.Ready, equal.State);
        Assert.Equal(LogicBlockStates.Blocked, notEqual.State);
        Assert.Equal(LogicBlockStates.Unknown, nonNumeric.State);
    }

    [Fact]
    public void ValueText_UsesDigitalTextsAnalogDecimalsAndUnit()
    {
        LogicBlockDto block = Flat(
            Condition("Pump must be running", itemId: "PumpRun"),
            Condition(op: LogicConditionOps.GreaterThan, value: 1, itemId: "Flow"));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "PumpRun", true, true), ("sim", "Flow", 12.3456, true)),
            Mappings(
                new TagMapping { SourceId = "sim", ItemId = "PumpRun", Digital = true, OnText = "Running", OffText = "Stopped" },
                new TagMapping { SourceId = "sim", ItemId = "Flow", DataType = "Double", Decimals = 1, Unit = "m³/h" }));

        Assert.Equal("Running", Contacts(state)[0].ValueText);
        Assert.Equal("12.3 m³/h", Contacts(state)[1].ValueText);
    }

    [Fact]
    public void Sequence_FirstStepWithFailingConditionIsCurrent_AndBlocks()
    {
        LogicBlockDto block = Sequence(
            Step("Start permit", conditions: Condition("Line 01 start permit must be given")),
            Step("Open valve", conditions: Condition("Valve 01 must be open", itemId: "Valve01")));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Permit", false, true), ("sim", "Valve01", true, true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Blocked, state.State);
        Assert.Equal("Line 01 start permit must be given", state.Reason);
        Assert.Equal(LogicStepStates.Current, state.Steps[0].State);
        Assert.Equal(LogicStepStates.Pending, state.Steps[1].State);
        Assert.Equal(2, Contacts(state).Count);
    }

    [Fact]
    public void Sequence_DoneStepsAdvanceAndReadyWhenAllDone()
    {
        LogicBlockDto stepOne = Sequence(
            Step("Start permit", conditions: Condition("Line 01 start permit must be given")),
            Step("Open valve", conditions: Condition("Valve 01 must be open", itemId: "Valve01")));

        LogicBlockStateDto partial = LogicStateEvaluator.EvaluateBlock(
            stepOne,
            Values(("sim", "Permit", true, true), ("sim", "Valve01", false, true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Blocked, partial.State);
        Assert.Equal(LogicStepStates.Done, partial.Steps[0].State);
        Assert.Equal(LogicStepStates.Current, partial.Steps[1].State);
        Assert.Equal("Valve 01 must be open", partial.Reason);

        LogicBlockStateDto complete = LogicStateEvaluator.EvaluateBlock(
            stepOne,
            Values(("sim", "Permit", true, true), ("sim", "Valve01", true, true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Ready, complete.State);
        Assert.Null(complete.Reason);
        Assert.All(complete.Steps, step => Assert.Equal(LogicStepStates.Done, step.State));
    }

    [Fact]
    public void Sequence_CompletionHandshakeHoldsTheStepOpen_AndNamesTheTag()
    {
        LogicBlockDto block = Sequence(
            Step("Start pump", completionItemId: "PumpRun", conditions: Condition("Pump must be enabled", itemId: "PumpEnable")),
            Step("Open valve", conditions: Condition("Valve 01 must be open", itemId: "Valve01")));

        LogicBlockStateDto waiting = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "PumpEnable", true, true), ("sim", "PumpRun", false, true), ("sim", "Valve01", true, true)),
            Mappings(new TagMapping { SourceId = "sim", ItemId = "PumpRun", DisplayName = "Pump 01 Running" }));

        Assert.Equal(LogicBlockStates.Blocked, waiting.State);
        Assert.Equal(LogicStepStates.Current, waiting.Steps[0].State);
        Assert.Equal("waiting for Pump 01 Running", waiting.Reason);

        LogicBlockStateDto completionMissing = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "PumpEnable", true, true)),
            Mappings(new TagMapping { SourceId = "sim", ItemId = "PumpRun", DisplayName = "Pump 01 Running" }));

        Assert.Equal(LogicBlockStates.Unknown, completionMissing.State);
        Assert.Equal("no data for Pump 01 Running", completionMissing.Reason);

        LogicBlockStateDto done = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "PumpEnable", true, true), ("sim", "PumpRun", true, true), ("sim", "Valve01", true, true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Ready, done.State);
        Assert.Equal(LogicStepStates.Done, done.Steps[0].State);
    }

    [Fact]
    public void Sequence_UnknownConditionInCurrentStepMakesBlockUnknown()
    {
        LogicBlockDto block = Sequence(
            Step("Start permit", conditions: Condition("Line 01 start permit must be given")));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(),
            Mappings(new TagMapping { SourceId = "sim", ItemId = "Permit", DisplayName = "Line 01 Start Permit" }));

        Assert.Equal(LogicBlockStates.Unknown, state.State);
        Assert.Equal(LogicStepStates.Unknown, state.Steps[0].State);
        Assert.Equal("no data for Line 01 Start Permit", state.Reason);
    }

    /// <summary>A flat block evaluated against a few tag values.</summary>
    private static LogicBlockStateDto Evaluate(LogicBlockDto block, params (string SourceId, string ItemId, object? Value, bool Good)[] entries) =>
        LogicStateEvaluator.EvaluateBlock(block, Values(entries), Mappings());

    [Fact]
    public void Should_And_Matches_ReportTheRequiredReadingAndTheLiveOne()
    {
        LogicBlockDto block = Flat(
            Condition("Pressure switch PS1 must read 1", itemId: "PS1"),
            Condition("Limit switch LS2 must be clear", op: LogicConditionOps.Off, itemId: "LS2"),
            Condition("Level must be above 50", op: LogicConditionOps.GreaterThan, value: 50, itemId: "Level"));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "PS1", false, true), ("sim", "LS2", false, true), ("sim", "Level", 40.0, true)),
            Mappings(new TagMapping { SourceId = "sim", ItemId = "Level", Decimals = 0 }));

        // The reading each contact must show, and whether the tag currently shows it.
        Assert.Equal("1", Contacts(state)[0].Should);
        Assert.False(Contacts(state)[0].Matches);
        Assert.Equal("0", Contacts(state)[1].Should);
        Assert.True(Contacts(state)[1].Matches);
        Assert.Equal("> 50", Contacts(state)[2].Should);
        Assert.False(Contacts(state)[2].Matches);
        Assert.Equal(LogicBlockStates.Blocked, state.State);
        Assert.Equal("Pressure switch PS1 must read 1", state.Reason);
    }

    [Fact]
    public void Should_IsUnknownsCompanion_AndMatchesIsNullWithoutAValue()
    {
        LogicBlockDto block = Flat(Condition("Pressure switch PS1 must read 1", itemId: "PS1"));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(block, Values(), Mappings());

        Assert.Equal("1", Contacts(state)[0].Should);
        Assert.Null(Contacts(state)[0].Matches);
        Assert.Equal(LogicConditionStates.Unknown, Contacts(state)[0].State);
    }

    [Fact]
    public void OrGate_IsAnyOf_AndTheBlockStillNeedsEveryRoot()
    {
        LogicBlockDto block = Flat(
            Condition("Local start PB must be pressed", itemId: "PB1", group: "Start permissive"),
            Condition("Remote start PB must be pressed", itemId: "PB2", group: "Start permissive"),
            Condition("Pressure switch PS1 must read 1", itemId: "PS1"));

        // The block's own AND sits on top, the group hangs under it, the contacts under that.
        LogicBlockStateDto oneTrue = Evaluate(block,
            ("sim", "PB1", true, true), ("sim", "PB2", false, true), ("sim", "PS1", false, true));

        Assert.Equal(LogicBlockStates.Blocked, oneTrue.State);
        Assert.Equal("Pressure switch PS1 must read 1", oneTrue.Reason);
        Assert.Equal(LogicElementKinds.And, oneTrue.Elements[0].Kind);
        LogicElementStateDto or = Assert.Single(oneTrue.Elements.Where(element => element.Kind == LogicElementKinds.Or));
        Assert.Equal(1, or.Depth);
        Assert.Equal(LogicConditionStates.True, or.State);
        Assert.Equal(3, oneTrue.Elements.Count(element => element.Kind == LogicElementKinds.Contact));

        // Every member false blocks, and the labelled gate names itself as the reason.
        LogicBlockStateDto allFalse = Evaluate(block,
            ("sim", "PB1", false, true), ("sim", "PB2", false, true), ("sim", "PS1", true, true));

        Assert.Equal(LogicBlockStates.Blocked, allFalse.State);
        Assert.Equal("Start permissive", allFalse.Reason);
        Assert.Equal(LogicConditionStates.False, Assert.Single(allFalse.Elements.Where(element => element.Kind == LogicElementKinds.Or)).State);

        // A member with no value leaves the gate unknown, and the reason names the missing tag.
        LogicBlockStateDto unknown = Evaluate(block,
            ("sim", "PB1", false, true), ("sim", "PS1", true, true));

        Assert.Equal(LogicBlockStates.Unknown, unknown.State);
        Assert.Equal("no data for PB2", unknown.Reason);
        Assert.Equal(LogicConditionStates.Unknown, Assert.Single(unknown.Elements.Where(element => element.Kind == LogicElementKinds.Or)).State);
    }

    [Fact]
    public void OrGate_WithoutALabelIsStillAReadableGate()
    {
        LogicBlockDto block = Flat(
            Condition("Local start PB must be pressed", itemId: "PB1", group: "Start permissive"),
            Condition("Remote start PB must be pressed", itemId: "PB2", group: "Start permissive"));

        LogicBlockStateDto state = Evaluate(block,
            ("sim", "PB1", false, true), ("sim", "PB2", false, true));

        // A single group is the whole network, so it is the root (no AND wrapper).
        Assert.Equal(LogicBlockStates.Blocked, state.State);
        Assert.Equal("Start permissive", state.Reason);
        LogicElementStateDto or = Assert.Single(state.Elements.Where(element => element.Kind == LogicElementKinds.Or));
        Assert.Equal(0, or.Depth);
        Assert.Equal(LogicConditionStates.False, or.State);
    }

    [Fact]
    public void WarnElementsAreReportedButInert()
    {
        LogicBlockDto block = Flat(
            Condition("Local start PB must be pressed", itemId: "PB1", group: "Start permissive"),
            Condition("Remote link is healthy", itemId: "Link", severity: LogicConditionSeverities.Warn, group: "Start permissive"));

        LogicBlockStateDto state = Evaluate(block,
            ("sim", "PB1", false, true), ("sim", "Link", true, true));

        // The warn member is true, but it neither satisfies nor blocks its gate.
        Assert.Equal(LogicBlockStates.Blocked, state.State);
        Assert.Equal(LogicConditionStates.False, Assert.Single(state.Elements.Where(element => element.Kind == LogicElementKinds.Or)).State);
        LogicElementStateDto warn = Assert.Single(state.Elements.Where(element => element.Id != Guid.Empty && element.Kind == LogicElementKinds.Contact && element.State == LogicConditionStates.True));
        Assert.Equal(LogicConditionStates.True, warn.State);
    }

    [Fact]
    public void Ton_HoldsUntilTheInputHasHeldLongEnough_AndResetsOnADrop()
    {
        LogicStateStore states = new();
        LogicBlockDto block = Flat(Condition("Hydraulic pressure must hold", itemId: "PS1", holdMs: 3000));

        LogicBlockStateDto atStart = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states, Now);

        // The expansion wrapped the contact in a TON, which is the root.
        LogicElementStateDto ton = atStart.Elements[0];
        Assert.Equal(LogicElementKinds.Ton, ton.Kind);
        Assert.Equal(3000, ton.PtMs);
        Assert.Equal(0, ton.ElapsedMs);
        Assert.Equal(LogicConditionStates.False, ton.State);
        Assert.True(Contacts(atStart)[0].Matches);
        Assert.Equal("Hydraulic pressure must hold", atStart.Reason);
        Assert.Equal(LogicBlockStates.Blocked, atStart.State);

        LogicBlockStateDto halfway = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states, Now.AddSeconds(1.5));

        Assert.Equal(1500, halfway.Elements[0].ElapsedMs);
        Assert.Equal(LogicConditionStates.False, halfway.Elements[0].State);
        Assert.Equal(LogicBlockStates.Blocked, halfway.State);

        LogicBlockStateDto elapsed = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states, Now.AddSeconds(3));

        Assert.Equal(LogicBlockStates.Ready, elapsed.State);
        Assert.Null(elapsed.Reason);
        Assert.Equal(LogicElementKinds.Ton, elapsed.Elements[0].Kind);
        Assert.Equal(LogicConditionStates.True, elapsed.Elements[0].State);

        // The input drops: the timer starts over.
        LogicBlockStateDto dropped = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", false, true)), Mappings(), states, Now.AddSeconds(4));

        Assert.Equal(LogicBlockStates.Blocked, dropped.State);
        Assert.Equal(0, dropped.Elements[0].ElapsedMs);
        Assert.Equal(LogicConditionStates.False, dropped.Elements[0].State);
        Assert.Equal("Hydraulic pressure must hold", dropped.Reason);

        LogicBlockStateDto restarted = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states, Now.AddSeconds(5));

        Assert.Equal(LogicBlockStates.Blocked, restarted.State);
        Assert.Equal(0, restarted.Elements[0].ElapsedMs);
    }

    [Fact]
    public void Ton_WithoutAStateStorePassesTheInputThrough()
    {
        LogicBlockDto block = Flat(Condition("Hydraulic pressure must hold", itemId: "PS1", holdMs: 3000));

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings());

        Assert.Equal(LogicBlockStates.Ready, state.State);
        Assert.Equal(LogicElementKinds.Ton, state.Elements[0].Kind);
        Assert.Equal(LogicConditionStates.True, state.Elements[0].State);
        Assert.Equal(3000, state.Elements[0].PtMs);
    }

    [Fact]
    public void Ton_ForgetsATimerWhoseElementLeftThePass()
    {
        LogicStateStore states = new();
        LogicConditionDto condition = Condition("Hydraulic pressure must hold", itemId: "PS1", holdMs: 3000);
        LogicBlockDto block = Flat(condition);

        LogicStateEvaluator.Evaluate(new[] { block }, 1, Values(("sim", "PS1", true, true)), Mappings(), Now, states);
        // The element is gone (edited away): the next full pass prunes its memory.
        LogicStateEvaluator.Evaluate(Array.Empty<LogicBlockDto>(), 2, Values(), Mappings(), Now.AddSeconds(1), states);
        // Re-added: the timer starts from zero rather than remembering the old run.
        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states, Now.AddSeconds(1));

        Assert.Equal(LogicBlockStates.Blocked, state.State);
        Assert.Equal(0, state.Elements[0].ElapsedMs);
    }

    [Fact]
    public void Signature_FollowsShouldMatchesAndTheTimersWholeSeconds()
    {
        LogicStateStore states = new();
        LogicBlockDto block = Flat(
            Condition("Pressure switch PS1 must read 1", itemId: "PS1"),
            Condition("Hydraulic pressure must hold", itemId: "PS2", holdMs: 3000));

        LogicStateSnapshot first = LogicStateEvaluator.Evaluate(
            new[] { block }, 1, Values(("sim", "PS1", true, true), ("sim", "PS2", true, true)), Mappings(), Now, states);
        LogicStateSnapshot sameSecond = LogicStateEvaluator.Evaluate(
            new[] { block }, 1, Values(("sim", "PS1", true, true), ("sim", "PS2", true, true)), Mappings(), Now.AddMilliseconds(400), states);
        LogicStateSnapshot nextSecond = LogicStateEvaluator.Evaluate(
            new[] { block }, 1, Values(("sim", "PS1", true, true), ("sim", "PS2", true, true)), Mappings(), Now.AddSeconds(1), states);
        LogicStateSnapshot elapses = LogicStateEvaluator.Evaluate(
            new[] { block }, 1, Values(("sim", "PS1", true, true), ("sim", "PS2", true, true)), Mappings(), Now.AddSeconds(3), states);

        // Same whole second of a running timer: no push. A new second, and the final state, do push.
        Assert.Equal(LogicStateEvaluator.Signature(first), LogicStateEvaluator.Signature(sameSecond));
        Assert.NotEqual(LogicStateEvaluator.Signature(first), LogicStateEvaluator.Signature(nextSecond));
        Assert.NotEqual(LogicStateEvaluator.Signature(nextSecond), LogicStateEvaluator.Signature(elapses));
    }

    [Fact]
    public void Disabled_BlockReportsDisabledEvenWhenConditionFalse()
    {
        LogicBlockDto block = Flat(Condition());
        block.Enabled = false;

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Permit", false, true)),
            Mappings());

        Assert.Equal(LogicBlockStates.Disabled, state.State);
        Assert.Equal("block is disabled", state.Reason);
        Assert.Equal(LogicConditionStates.False, Contacts(state)[0].State);
    }

    [Fact]
    public void Evaluate_SortsBlocksByOrderAndCarriesVersion()
    {
        LogicBlockDto late = Flat(Condition());
        late.Name = "Second";
        late.Order = 5;
        LogicBlockDto early = Flat(Condition());
        early.Name = "First";
        early.Order = 1;

        LogicStateSnapshot snapshot = LogicStateEvaluator.Evaluate(
            new[] { late, early },
            9,
            Values(("sim", "Permit", true, true)),
            Mappings(),
            Now);

        Assert.Equal(9, snapshot.Version);
        Assert.Equal(Now, snapshot.EvaluatedUtc);
        Assert.Equal(early.Id, snapshot.Blocks[0].Id);
        Assert.Equal(late.Id, snapshot.Blocks[1].Id);
    }

    [Fact]
    public void Signature_IgnoresValueTextButFollowsStates()
    {
        LogicBlockDto block = Flat(Condition());
        LogicStateSnapshot first = LogicStateEvaluator.Evaluate(
            new[] { block }, 1, Values(("sim", "Permit", true, true)), Mappings(), Now);
        LogicStateSnapshot sameStateDifferentValue = LogicStateEvaluator.Evaluate(
            new[] { block }, 1, Values(("sim", "Permit", true, true)), Mappings(), Now.AddSeconds(3));
        LogicStateSnapshot changed = LogicStateEvaluator.Evaluate(
            new[] { block }, 1, Values(("sim", "Permit", false, true)), Mappings(), Now);

        Assert.Equal(LogicStateEvaluator.Signature(first), LogicStateEvaluator.Signature(sameStateDifferentValue));
        Assert.NotEqual(LogicStateEvaluator.Signature(first), LogicStateEvaluator.Signature(changed));
    }

    // ---- Internal relays (coils and relay contacts) ----

    private static LogicElementDto RelayContact(string relay, string op = LogicConditionOps.On) => new()
    {
        Id = Guid.NewGuid(),
        Kind = LogicElementKinds.Contact,
        Relay = relay,
        Op = op
    };

    private static LogicElementDto TagContact(
        string itemId,
        string text = "Tag must read 1",
        string op = LogicConditionOps.On) => new()
    {
        Id = Guid.NewGuid(),
        Kind = LogicElementKinds.Contact,
        Text = text,
        SourceId = "sim",
        ItemId = itemId,
        Op = op
    };

    private static LogicElementDto Coil(string relay, LogicElementDto input, string text = "") => new()
    {
        Id = Guid.NewGuid(),
        Kind = LogicElementKinds.Coil,
        Text = text,
        Relay = relay,
        Inputs = { input }
    };

    private static LogicElementDto Gate(string kind, params LogicElementDto[] inputs)
    {
        var element = new LogicElementDto { Id = Guid.NewGuid(), Kind = kind };
        element.Inputs.AddRange(inputs);
        return element;
    }

    /// <summary>A block carrying its logic in the network form.</summary>
    private static LogicBlockDto Network(params LogicElementDto[] roots)
    {
        var block = new LogicBlockDto
        {
            Id = Guid.NewGuid(),
            Name = "Relay network",
            Kind = LogicBlockKinds.Interlock
        };
        block.Elements.AddRange(roots);
        return block;
    }

    private static LogicElementStateDto Row(LogicBlockStateDto state, LogicElementDto element) =>
        Assert.Single(state.Elements.Where(row => row.Id == element.Id));

    [Fact]
    public void Coil_DrivesItsRelay_AndALaterRelayContactReadsItInTheSamePass()
    {
        LogicStateStore states = new();
        LogicElementDto drive = Coil("M01", TagContact("PS1"));
        LogicElementDto read = RelayContact("M01");
        LogicBlockDto block = Network(drive, read);

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states, Now);

        Assert.Equal(LogicBlockStates.Ready, state.State);
        Assert.Equal(LogicConditionStates.True, Row(state, drive).State);
        Assert.Equal("1", Row(state, drive).ValueText);
        Assert.Equal(LogicConditionStates.True, Row(state, read).State);
        Assert.Equal("1", Row(state, read).Should);
        Assert.True(Row(state, read).Matches);
        Assert.Equal("1", Row(state, read).ValueText);
    }

    [Fact]
    public void RelayContact_BeforeItsCoilSeesThePreviousPass()
    {
        LogicStateStore states = new();
        LogicElementDto read = RelayContact("M01");
        LogicElementDto drive = Coil("M01", TagContact("PS1"));
        LogicBlockDto block = Network(read, drive);

        // The read comes first in the pass and nothing has driven M01 yet: unknown, named.
        LogicBlockStateDto first = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states, Now);

        Assert.Equal(LogicBlockStates.Unknown, first.State);
        Assert.Equal("no data for M01", first.Reason);
        Assert.Equal(LogicConditionStates.Unknown, Row(first, read).State);
        Assert.Equal("—", Row(first, read).ValueText);

        // The coil drove it in the first pass, so the second pass's read sees the value.
        LogicBlockStateDto second = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states, Now.AddMilliseconds(100));

        Assert.Equal(LogicBlockStates.Ready, second.State);
        Assert.Equal(LogicConditionStates.True, Row(second, read).State);
    }

    [Fact]
    public void RelayContact_Off_ReadsTheRelaysBitInverted()
    {
        LogicStateStore states = new();
        LogicBlockDto drive = Network(Coil("M01", TagContact("PS1")));
        drive.Order = 1;
        LogicElementDto readOff = RelayContact("M01", LogicConditionOps.Off);
        LogicBlockDto consumer = Network(readOff);
        consumer.Order = 2;

        LogicStateSnapshot snapshot = LogicStateEvaluator.Evaluate(
            new[] { drive, consumer }, 1, Values(("sim", "PS1", false, true)), Mappings(), Now, states);

        Assert.Equal(LogicBlockStates.Ready, snapshot.Blocks[1].State);
        Assert.Equal("0", Row(snapshot.Blocks[1], readOff).Should);
        Assert.True(Row(snapshot.Blocks[1], readOff).Matches);
        Assert.Equal("0", Row(snapshot.Blocks[1], readOff).ValueText);
    }

    [Fact]
    public void RelayNobodyDrives_ReadsUnknown_AndNamesTheRelay()
    {
        LogicElementDto read = RelayContact("M77");
        LogicBlockDto block = Network(read);

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block, Values(), Mappings(), new LogicStateStore(), Now);

        Assert.Equal(LogicBlockStates.Unknown, state.State);
        Assert.Equal("no data for M77", state.Reason);
        Assert.Equal("1", Row(state, read).Should);
        Assert.Null(Row(state, read).Matches);
        Assert.Equal("—", Row(state, read).ValueText);
    }

    [Fact]
    public void Coil_WithAnUnknownInputWritesNothing_AndTheRelayKeepsItsValue()
    {
        LogicStateStore states = new();
        LogicElementDto driveCoil = Coil("M01", TagContact("PS1"));
        LogicBlockDto drive = Network(driveCoil);
        drive.Order = 1;
        LogicElementDto read = RelayContact("M01");
        LogicBlockDto consumer = Network(read);
        consumer.Order = 2;

        LogicStateSnapshot ready = LogicStateEvaluator.Evaluate(
            new[] { drive, consumer }, 1, Values(("sim", "PS1", true, true)), Mappings(), Now, states);
        Assert.Equal(LogicBlockStates.Ready, ready.Blocks[1].State);

        // PS1 loses its value: the coil writes nothing and M01 keeps the last driven bit.
        LogicStateSnapshot held = LogicStateEvaluator.Evaluate(
            new[] { drive, consumer }, 2, Values(("sim", "PS1", true, false)), Mappings(), Now.AddMilliseconds(100), states);

        Assert.Equal(LogicConditionStates.Unknown, Row(held.Blocks[0], driveCoil).State);
        Assert.Equal("—", Row(held.Blocks[0], driveCoil).ValueText);
        Assert.Equal(LogicConditionStates.True, Row(held.Blocks[1], read).State);
        Assert.Equal(LogicBlockStates.Ready, held.Blocks[1].State);
    }

    [Fact]
    public void Coil_InADisabledBlockDoesNotWrite_ButStillReportsWhatItWould()
    {
        LogicStateStore states = new();
        LogicElementDto driveCoil = Coil("M01", TagContact("PS1"));
        LogicBlockDto drive = Network(driveCoil);
        drive.Enabled = false;
        LogicElementDto read = RelayContact("M01");
        LogicBlockDto consumer = Network(read);
        consumer.Order = 1;

        LogicStateSnapshot snapshot = LogicStateEvaluator.Evaluate(
            new[] { drive, consumer }, 1, Values(("sim", "PS1", true, true)), Mappings(), Now, states);

        Assert.Equal(LogicBlockStates.Disabled, snapshot.Blocks[0].State);
        Assert.Equal(LogicConditionStates.True, Row(snapshot.Blocks[0], driveCoil).State);
        Assert.Null(states.TryGetRelay("M01"));
        Assert.Equal(LogicBlockStates.Unknown, snapshot.Blocks[1].State);

        // Enabled again, the coil drives the relay within the same pass.
        drive.Enabled = true;
        LogicStateSnapshot enabled = LogicStateEvaluator.Evaluate(
            new[] { drive, consumer }, 2, Values(("sim", "PS1", true, true)), Mappings(), Now.AddMilliseconds(100), states);

        Assert.Equal(LogicConditionStates.True, Row(enabled.Blocks[1], read).State);
        Assert.Equal(LogicBlockStates.Ready, enabled.Blocks[1].State);
    }

    [Fact]
    public void Relay_IsForgottenWhenNoBlockReferencesIt()
    {
        LogicStateStore states = new();
        LogicBlockDto block = Network(Coil("M01", TagContact("PS1")));

        LogicStateEvaluator.Evaluate(new[] { block }, 1, Values(("sim", "PS1", true, true)), Mappings(), Now, states);
        Assert.True(states.TryGetRelay("M01"));

        // The block is gone (deleted): the next full pass prunes the relay.
        LogicStateEvaluator.Evaluate(Array.Empty<LogicBlockDto>(), 2, Values(), Mappings(), Now.AddSeconds(1), states);
        Assert.Null(states.TryGetRelay("M01"));
    }

    [Fact]
    public void Relay_FeedsEveryGateAndFunctionBlock_InAnyBlock()
    {
        LogicStateStore states = new();
        LogicBlockDto drive = Network(Coil("M01", TagContact("PS1")));
        drive.Order = 1;

        LogicElementDto notRoot = Gate(LogicElementKinds.Not, RelayContact("M01"));
        LogicBlockDto notBlock = Network(notRoot);
        notBlock.Order = 2;

        LogicElementDto relaySide = RelayContact("M01");
        LogicElementDto xorRoot = Gate(LogicElementKinds.Xor, relaySide, TagContact("PB2", "PB2 must be pressed"));
        LogicBlockDto xorBlock = Network(xorRoot);
        xorBlock.Order = 3;

        LogicElementDto andRoot = Gate(
            LogicElementKinds.And,
            RelayContact("M01"),
            TagContact("PB3", "PB3 must be pressed"));
        LogicBlockDto andBlock = Network(andRoot);
        andBlock.Order = 4;

        LogicElementDto tonRoot = new()
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.Ton,
            PtMs = 1000,
            Inputs = { RelayContact("M01") }
        };
        LogicBlockDto tonBlock = Network(tonRoot);
        tonBlock.Order = 5;

        LogicElementDto ctuRoot = new()
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.Ctu,
            Pv = 1,
            Inputs = { RelayContact("M01") }
        };
        LogicBlockDto ctuBlock = Network(ctuRoot);
        ctuBlock.Order = 6;

        LogicStateSnapshot snapshot = LogicStateEvaluator.Evaluate(
            new[] { drive, notBlock, xorBlock, andBlock, tonBlock, ctuBlock },
            1,
            Values(("sim", "PS1", true, true), ("sim", "PB2", false, true), ("sim", "PB3", false, true)),
            Mappings(),
            Now,
            states);

        // The relay one block drives is read by every other block's own kind of logic.
        Assert.Equal(LogicBlockStates.Ready, snapshot.Blocks[0].State);
        Assert.Equal(LogicBlockStates.Blocked, snapshot.Blocks[1].State);
        Assert.Equal(LogicConditionStates.False, Row(snapshot.Blocks[1], notRoot).State);
        Assert.Equal(LogicBlockStates.Ready, snapshot.Blocks[2].State);
        Assert.Equal(LogicConditionStates.True, Row(snapshot.Blocks[2], relaySide).State);
        Assert.Equal(LogicBlockStates.Blocked, snapshot.Blocks[3].State);
        Assert.Equal("PB3 must be pressed", snapshot.Blocks[3].Reason);
        Assert.Equal(LogicBlockStates.Blocked, snapshot.Blocks[4].State);
        Assert.Equal(1000, Row(snapshot.Blocks[4], tonRoot).PtMs);
        Assert.Equal(LogicConditionStates.False, Row(snapshot.Blocks[4], tonRoot).State);
        Assert.Equal(LogicBlockStates.Ready, snapshot.Blocks[5].State);
        Assert.Equal(1, Row(snapshot.Blocks[5], ctuRoot).Count);
    }

    [Fact]
    public void RelayChain_AcrossThreeBlocksSettlesInOneOrderedPass()
    {
        LogicStateStore states = new();
        LogicBlockDto first = Network(Coil("M01", TagContact("PS1")));
        first.Order = 1;
        LogicBlockDto second = Network(Coil("M02", RelayContact("M01")));
        second.Order = 2;
        LogicElementDto read = RelayContact("M02");
        LogicBlockDto third = Network(read);
        third.Order = 3;

        LogicStateSnapshot snapshot = LogicStateEvaluator.Evaluate(
            new[] { first, second, third }, 1, Values(("sim", "PS1", true, true)), Mappings(), Now, states);

        Assert.Equal(LogicBlockStates.Ready, snapshot.Blocks[2].State);
        Assert.Equal(LogicConditionStates.True, Row(snapshot.Blocks[2], read).State);
    }

    [Fact]
    public void DeepNetwork_AtTheBoundsNestsAndEvaluates()
    {
        LogicStateStore states = new();
        LogicElementDto root = TagContact("PS1");
        for (int i = 0; i < LogicLimits.MaxElementDepth; i++)
        {
            root = Gate(LogicElementKinds.Not, root);
        }

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            Network(root), Values(("sim", "PS1", true, true)), Mappings(), states, Now);

        Assert.Equal(LogicLimits.MaxElementDepth + 1, state.Elements.Count);
        Assert.Equal(LogicLimits.MaxElementDepth, state.Elements.Max(row => row.Depth));

        // Sixteen inversions: the tag's true passes through unchanged.
        Assert.Equal(LogicBlockStates.Ready, state.State);
    }

    /// <summary>One machine's tags, with the entries a test needs bound (or left empty).</summary>
    private static LogicTagSourceDto TagSource(string name, params (string Name, string ItemId)[] entries)
    {
        LogicTagSourceDto source = new() { Name = name };
        foreach ((string tagName, string itemId) in entries)
        {
            source.Tags.Add(new LogicTagDto
            {
                Name = tagName,
                SourceId = itemId.Length == 0 ? string.Empty : "sim",
                ItemId = itemId
            });
        }

        return source;
    }

    private static LogicBlockDto NamedFlat(string tagSource, params string[] tags) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Dry end start",
        TagSource = tagSource,
        Kind = LogicBlockKinds.Interlock,
        Conditions = tags
            .Select(tag => new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                Text = tag + " must be given",
                Tag = tag,
                Op = LogicConditionOps.On
            })
            .ToList()
    };

    [Fact]
    public void NamedTag_ResolvesThroughTheMachinesTagsSource()
    {
        LogicTagSourceDto source = TagSource("DRYEND", ("START_PB", "Start"));
        LogicBlockDto block = NamedFlat("DRYEND", "START_PB");

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Start", true, true)),
            Mappings(),
            states: null,
            nowUtc: Now,
            tagSources: new[] { source });

        Assert.Equal(LogicBlockStates.Ready, state.State);
        Assert.Equal(LogicConditionStates.True, Contacts(state)[0].State);
    }

    [Fact]
    public void NamedTag_Unassigned_ReadsUnknownAndNamesTheEntry()
    {
        LogicTagSourceDto source = TagSource("DRYEND", ("START_PB", ""));
        LogicBlockDto block = NamedFlat("DRYEND", "START_PB");

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(),
            Mappings(),
            states: null,
            nowUtc: Now,
            tagSources: new[] { source });

        // Never a silent false: the entry is named so the author knows what to assign.
        Assert.Equal(LogicBlockStates.Unknown, state.State);
        Assert.Equal("no data for START_PB (not assigned)", state.Reason);
        Assert.Equal("—", Contacts(state)[0].ValueText);
    }

    [Fact]
    public void NamedTag_NotInTheSource_NamesTheMachine()
    {
        LogicTagSourceDto source = TagSource("DRYEND", ("START_PB", "Start"));
        LogicBlockDto block = NamedFlat("DRYEND", "GHOST_PB");

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(),
            Mappings(),
            states: null,
            nowUtc: Now,
            tagSources: new[] { source });

        Assert.Equal(LogicBlockStates.Unknown, state.State);
        Assert.Equal("no data for GHOST_PB (not in DRYEND)", state.Reason);
    }

    [Fact]
    public void NamedTag_WithoutATagsSource_SaysSo()
    {
        LogicBlockDto block = NamedFlat(string.Empty, "GHOST_PB");

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(),
            Mappings(),
            states: null,
            nowUtc: Now,
            tagSources: Array.Empty<LogicTagSourceDto>());

        Assert.Equal("no data for GHOST_PB (no tags source)", state.Reason);
    }

    [Fact]
    public void NamedTag_BoundButUnread_NamesTheRealTagFromTheMapping()
    {
        LogicTagSourceDto source = TagSource("DRYEND", ("START_PB", "ns=2;s=Status/Line01.Permit"));
        LogicBlockDto block = NamedFlat("DRYEND", "START_PB");

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(),
            Mappings(new TagMapping
            {
                SourceId = "sim",
                ItemId = "ns=2;s=Status/Line01.Permit",
                DisplayName = "Line 01 Start Permit"
            }),
            states: null,
            nowUtc: Now,
            tagSources: new[] { source });

        Assert.Equal("no data for Line 01 Start Permit", state.Reason);
    }

    [Fact]
    public void NamedTag_SharedByTwoBlocks_MovesBothWhenTheEntryIsBound()
    {
        LogicBlockDto start = NamedFlat("DRYEND", "START_PB");
        LogicBlockDto stop = NamedFlat("DRYEND", "START_PB");
        stop.Name = "Dry end stop";

        LogicBlockStateDto before = LogicStateEvaluator.EvaluateBlock(
            start, Values(), Mappings(), states: null, nowUtc: Now,
            tagSources: new[] { TagSource("DRYEND", ("START_PB", "")) });

        LogicTagSourceDto bound = TagSource("DRYEND", ("START_PB", "Start"));
        LogicBlockStateDto afterStart = LogicStateEvaluator.EvaluateBlock(
            start, Values(("sim", "Start", true, true)), Mappings(), states: null, nowUtc: Now, tagSources: new[] { bound });
        LogicBlockStateDto afterStop = LogicStateEvaluator.EvaluateBlock(
            stop, Values(("sim", "Start", true, true)), Mappings(), states: null, nowUtc: Now, tagSources: new[] { bound });

        // One assignment moves every block of the machine.
        Assert.Equal(LogicBlockStates.Unknown, before.State);
        Assert.Equal(LogicBlockStates.Ready, afterStart.State);
        Assert.Equal(LogicBlockStates.Ready, afterStop.State);
    }

    [Fact]
    public void NamedTag_OnANetworkContactReadsTheBoundTag()
    {
        LogicTagSourceDto source = TagSource("DRYEND", ("START_PB", "PS1"));
        LogicElementDto contact = TagContact("START_PB");
        contact.Tag = "START_PB";
        contact.ItemId = string.Empty;
        LogicBlockDto block = Network(contact);
        block.TagSource = "DRYEND";

        LogicBlockStateDto state = LogicStateEvaluator.EvaluateBlock(
            block, Values(("sim", "PS1", true, true)), Mappings(), states: null, nowUtc: Now, tagSources: new[] { source });

        Assert.Equal(LogicBlockStates.Ready, state.State);
        Assert.Equal(LogicConditionStates.True, Row(state, contact).State);
    }

    [Fact]
    public void NamedTag_CompletionHandshakeResolvesAndReportsWhileUnassigned()
    {
        LogicConditionDto condition = Condition(tag: "START_PB");
        LogicStepDto step = new()
        {
            Id = Guid.NewGuid(),
            Name = "Start",
            Conditions = { condition },
            CompletionTag = "DONE_FB"
        };
        LogicBlockDto block = Sequence(step);
        block.TagSource = "DRYEND";

        LogicBlockStateDto unassigned = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Permit", true, true)),
            Mappings(),
            states: null,
            nowUtc: Now,
            tagSources: new[] { TagSource("DRYEND", ("START_PB", "Permit"), ("DONE_FB", "")) });

        Assert.Equal(LogicStepStates.Unknown, Assert.Single(unassigned.Steps).State);
        Assert.Equal("no data for DONE_FB (not assigned)", Assert.Single(unassigned.Steps).Reason);

        LogicBlockStateDto done = LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "Permit", true, true), ("sim", "Done", true, true)),
            Mappings(),
            states: null,
            nowUtc: Now,
            tagSources: new[] { TagSource("DRYEND", ("START_PB", "Permit"), ("DONE_FB", "Done")) });

        Assert.Equal(LogicBlockStates.Ready, done.State);
        Assert.Equal(LogicStepStates.Done, Assert.Single(done.Steps).State);
    }
}
