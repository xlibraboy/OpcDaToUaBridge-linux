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

    private static LogicConditionDto Condition(
        string text = "Line 01 start permit must be given",
        string op = LogicConditionOps.On,
        double? value = null,
        string sourceId = "sim",
        string itemId = "Permit",
        string severity = LogicConditionSeverities.Block,
        string? nextStep = null)
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
            NextStepText = nextStep
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
        Assert.All(state.Conditions, condition => Assert.Equal(LogicConditionStates.True, condition.State));
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
        Assert.Equal(LogicConditionStates.False, state.Conditions[0].State);
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
        Assert.Equal(LogicConditionStates.Unknown, state.Conditions[0].State);
        Assert.Equal("—", state.Conditions[0].ValueText);
        Assert.Null(state.Conditions[0].TimestampUtc);
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
        Assert.Equal(LogicConditionStates.False, state.Conditions[1].State);
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

        Assert.Equal("Running", state.Conditions[0].ValueText);
        Assert.Equal("12.3 m³/h", state.Conditions[1].ValueText);
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
        Assert.Equal(2, state.Conditions.Count);
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
        Assert.Equal(LogicConditionStates.False, state.Conditions[0].State);
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
