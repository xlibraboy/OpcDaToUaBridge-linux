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
        int holdMs = 0)
    {
        return new LogicConditionDto
        {
            Id = Guid.NewGuid(),
            Text = text,
            SourceId = sourceId,
            ItemId = itemId,
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
}
