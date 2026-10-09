using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class LogicOverviewViewModelTests
{
    private static LogicBlockDto Block(string name, string kind = LogicBlockKinds.Interlock, params LogicConditionDto[] conditions) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = kind,
        Conditions = conditions.ToList()
    };

    private static LogicConditionDto Condition(string text = "Permit must be given") => new()
    {
        Id = Guid.NewGuid(),
        Text = text,
        SourceId = "sim",
        ItemId = "Permit",
        Op = LogicConditionOps.On
    };

    private static LogicBlockStateDto State(Guid id, string state, string? reason = null, params LogicStepStateDto[] steps) => new()
    {
        Id = id,
        State = state,
        Reason = reason,
        Steps = steps.ToList()
    };

    [Fact]
    public void ApplyDefinitions_UpdatesInPlaceUntilTheSetChanges()
    {
        LogicOverviewViewModel vm = new();
        LogicBlockDto first = Block("Line 01 Start", conditions: Condition());

        vm.ApplyDefinitions(new[] { first });
        LogicBlockCardViewModel card = vm.Blocks[0];

        // A rename must not replace the card (the list must not rebuild under a finger).
        first.Name = "Line 01 Start (rev B)";
        vm.ApplyDefinitions(new[] { first });
        Assert.Same(card, vm.Blocks[0]);
        Assert.Equal("Line 01 Start (rev B)", card.Name);

        // A new block rebuilds the set.
        LogicBlockDto second = Block("Line 02 Start", conditions: Condition());
        vm.ApplyDefinitions(new[] { first, second });
        Assert.Equal(2, vm.Blocks.Count);
    }

    [Fact]
    public void ApplyState_SetsStateWordReasonAndSummary()
    {
        LogicOverviewViewModel vm = new();
        LogicBlockDto blocked = Block("Line 01 Start", conditions: Condition());
        LogicBlockDto ready = Block("Line 02 Start", conditions: Condition());
        vm.ApplyDefinitions(new[] { blocked, ready });

        vm.ApplyState(new LogicStateSnapshot
        {
            Blocks =
            {
                State(blocked.Id, LogicBlockStates.Blocked, "Line 01 start permit must be given"),
                State(ready.Id, LogicBlockStates.Ready)
            }
        });

        Assert.Equal("BLOCKED", vm.Blocks.Single(b => b.Id == blocked.Id).StateLabel);
        Assert.Equal("Line 01 start permit must be given", vm.Blocks.Single(b => b.Id == blocked.Id).Reason);
        Assert.Equal("READY", vm.Blocks.Single(b => b.Id == ready.Id).StateLabel);
        Assert.Equal("all conditions met", vm.Blocks.Single(b => b.Id == ready.Id).Reason);
        Assert.Equal("1 ready · 1 blocked", vm.Summary);
    }

    [Fact]
    public void ApplyState_DerivesSequenceProgressFromTheCurrentStep()
    {
        LogicOverviewViewModel vm = new();
        LogicBlockDto sequence = Block("Start-up", LogicBlockKinds.Sequence);
        sequence.Steps.Add(new LogicStepDto { Id = Guid.NewGuid(), Name = "Start permit" });
        sequence.Steps.Add(new LogicStepDto { Id = Guid.NewGuid(), Name = "Open valve" });
        vm.ApplyDefinitions(new[] { sequence });

        LogicStepStateDto current = new() { Id = sequence.Steps[1].Id, State = LogicStepStates.Current, Reason = "Valve 01 must be open" };
        vm.ApplyState(new LogicStateSnapshot
        {
            Blocks =
            {
                State(sequence.Id, LogicBlockStates.Blocked, "Valve 01 must be open",
                    new LogicStepStateDto { Id = sequence.Steps[0].Id, State = LogicStepStates.Done },
                    current)
            }
        });

        LogicBlockCardViewModel card = vm.Blocks[0];
        Assert.Equal("Sequence", card.KindLabel);
        Assert.Equal("1/2 steps · Open valve", card.ProgressText);
    }

    [Fact]
    public void ApplyState_WithoutASnapshotReadsAsNoDataNeverAsReady()
    {
        LogicOverviewViewModel vm = new();
        vm.ApplyDefinitions(new[] { Block("Line 01 Start", conditions: Condition()) });

        vm.ApplyState(null);

        // Before any snapshot the card reads as loading, never as ready.
        Assert.Equal("no data", vm.Blocks[0].StateLabel);
        Assert.Equal("reading…", vm.Blocks[0].Reason);
        Assert.Equal("0 ready · 0 blocked · 1 no data", vm.Summary);
    }
}

public sealed class LogicBlockDetailViewModelTests
{
    private const string BridgeKey = "mobile:test";

    private static LogicConditionDto Condition(
        string text,
        string itemId = "Permit",
        string severity = LogicConditionSeverities.Block,
        string? nextStep = null,
        string op = LogicConditionOps.On) => new()
    {
        Id = Guid.NewGuid(),
        Text = text,
        SourceId = "sim",
        ItemId = itemId,
        Op = op,
        Severity = severity,
        NextStepText = nextStep
    };

    private static LogicBlockDto Interlock(params LogicConditionDto[] conditions) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Line 01 Start",
        Kind = LogicBlockKinds.Interlock,
        Conditions = conditions.ToList()
    };

    private static MultiBridgeTagCache TagCache(params HmiTagDto[] tags)
    {
        MultiBridgeTagCache cache = new();
        cache.ReplaceBridge(BridgeKey, tags);
        return cache;
    }

    [Fact]
    public void ConditionRows_ShowMarksValueTextAndTheNextStepOnlyWhileNotTrue()
    {
        LogicConditionDto permit = Condition("Line 01 start permit must be given", itemId: "Permit", nextStep: "Turn the permit key");
        LogicBlockDetailViewModel vm = new(
            Interlock(permit),
            BridgeKey,
            new LogicBlockStateDto
            {
                State = LogicBlockStates.Blocked,
                Reason = permit.Text,
                Conditions = { new LogicConditionStateDto { Id = permit.Id, State = LogicConditionStates.False, ValueText = "Blocked" } }
            },
            TagCache(new HmiTagDto
            {
                SourceId = "sim",
                SourceName = "Simulation",
                ItemId = "Permit",
                DisplayName = "Line 01 Start Permit",
                Digital = true,
                OnText = "Permit",
                OffText = "Blocked",
                Value = false,
                IsGood = true
            }));

        LogicConditionRowViewModel row = vm.Conditions[0];
        Assert.Equal("✗", row.Mark);
        // The plant reads the condition as 0 (false); the mapped texts stay as the live value.
        Assert.Equal("0", row.StateWord);
        Assert.True(row.Blocks);
        Assert.True(row.ShowNextStep);
        Assert.Equal("Turn the permit key", row.NextStepText);
        Assert.Equal("Line 01 Start Permit · Simulation", row.TagLabel);
        // The live value comes from the tag cache, so a delta keeps it current.
        Assert.Equal("Blocked", row.ValueText);
        Assert.Equal("Blocked by: Line 01 start permit must be given", vm.BlockedByText);
    }

    [Fact]
    public void ConditionRows_ReadOneZeroOrNoDataForBooleansOnly()
    {
        LogicConditionDto permit = Condition("Permit must be given");
        LogicConditionDto level = Condition("Tank 01 level above 50 %", itemId: "Tank01.Level", op: LogicConditionOps.GreaterThan);
        LogicConditionDto valve = Condition("Valve 01 must be open", itemId: "Valve01");
        LogicBlockDetailViewModel vm = new(
            Interlock(permit, level, valve),
            BridgeKey,
            new LogicBlockStateDto
            {
                State = LogicBlockStates.Blocked,
                Conditions =
                {
                    new LogicConditionStateDto { Id = permit.Id, State = LogicConditionStates.True, ValueText = "Permit" },
                    new LogicConditionStateDto { Id = level.Id, State = LogicConditionStates.True, ValueText = "62.5 %" }
                }
            });

        Assert.True(vm.Conditions[0].ShowStateWord);
        Assert.Equal("1", vm.Conditions[0].StateWord);
        Assert.Equal("Permit", vm.Conditions[0].ValueText);

        // A numeric comparison keeps its value: a level above 50 % is not a bit.
        Assert.False(vm.Conditions[1].ShowStateWord);
        Assert.Equal(string.Empty, vm.Conditions[1].StateWord);
        Assert.Equal("62.5 %", vm.Conditions[1].ValueText);

        // No snapshot yet: a boolean reads as no data, never as 0.
        Assert.True(vm.Conditions[2].ShowStateWord);
        Assert.Equal("—", vm.Conditions[2].StateWord);
    }

    [Fact]
    public void ADigitalTagMakesItsConditionReadAsABitEvenWithACompareOp()
    {
        LogicConditionDto run = Condition("Run feedback", itemId: "Run", op: LogicConditionOps.Equal);
        LogicBlockDetailViewModel vm = new(
            Interlock(run),
            BridgeKey,
            new LogicBlockStateDto
            {
                State = LogicBlockStates.Ready,
                Conditions = { new LogicConditionStateDto { Id = run.Id, State = LogicConditionStates.True, ValueText = "Running" } }
            },
            TagCache(new HmiTagDto
            {
                SourceId = "sim",
                SourceName = "Simulation",
                ItemId = "Run",
                DisplayName = "Run feedback",
                Digital = true,
                OnText = "Running",
                OffText = "Stopped",
                Value = true,
                IsGood = true
            }));

        Assert.True(vm.Conditions[0].ShowStateWord);
        Assert.Equal("1", vm.Conditions[0].StateWord);
        Assert.Equal("Running", vm.Conditions[0].ValueText);
    }

    [Fact]
    public void Sequence_ShowsTheStepFlowAndMarksTheCurrentStep()
    {
        Guid stepOne = Guid.NewGuid();
        Guid stepTwo = Guid.NewGuid();
        LogicBlockDto sequence = new()
        {
            Id = Guid.NewGuid(),
            Name = "Start-up",
            Kind = LogicBlockKinds.Sequence,
            Steps =
            {
                new LogicStepDto { Id = stepOne, Name = "Start permit", Conditions = { Condition("Permit must be given") } },
                new LogicStepDto
                {
                    Id = stepTwo,
                    Name = "Open valve",
                    Conditions = { Condition("Valve 01 must be open", itemId: "Valve01") }
                }
            }
        };

        LogicBlockDetailViewModel vm = new(
            sequence,
            BridgeKey,
            new LogicBlockStateDto
            {
                State = LogicBlockStates.Blocked,
                Reason = "Valve 01 must be open",
                Steps =
                {
                    new LogicStepStateDto { Id = stepOne, State = LogicStepStates.Done },
                    new LogicStepStateDto { Id = stepTwo, State = LogicStepStates.Current, Reason = "Valve 01 must be open" }
                }
            });

        Assert.True(vm.IsSequence);
        Assert.Equal(2, vm.Steps.Count);
        Assert.Equal("Done", vm.Steps[0].StateLabel);
        Assert.False(vm.Steps[0].IsCurrent);
        Assert.Equal("Current", vm.Steps[1].StateLabel);
        Assert.True(vm.Steps[1].IsCurrent);
        Assert.Equal("Valve 01 must be open", vm.Steps[1].Reason);
        Assert.Single(vm.Steps[0].Conditions);
    }

    [Fact]
    public void ActionCommand_RaisesTheRequestForThePageToConfirmAndWrite()
    {
        LogicBlockDto block = Interlock(Condition("Permit must be given"));
        block.Actions.Add(new LogicActionDto
        {
            Label = "Start pump",
            SourceId = "sim",
            ItemId = "Cmd/Pump01.Start",
            Value = "true",
            Confirm = true
        });
        LogicBlockDetailViewModel vm = new(block, BridgeKey);
        LogicActionButtonViewModel? requested = null;
        vm.ActionRequested += action => requested = action;

        vm.RunActionCommand.Execute(vm.Actions[0]);

        Assert.NotNull(requested);
        Assert.Equal("Start pump", requested!.Label);
        Assert.True(requested.Confirm);
    }

    [Fact]
    public void Notes_AreListedNewestFirstWithAuthorAndLocalTime()
    {
        LogicBlockDetailViewModel vm = new(Interlock(Condition("Permit must be given")), BridgeKey);

        vm.SetNotes(new[]
        {
            new LogicNoteDto { Text = "newer", Author = "operator1", CreatedUtc = new DateTime(2026, 10, 8, 10, 30, 5, DateTimeKind.Utc) },
            new LogicNoteDto { Text = "older", Author = "app", CreatedUtc = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc) }
        });

        Assert.Equal(2, vm.Notes.Count);
        Assert.Equal("newer", vm.Notes[0].Text);
        Assert.Equal("operator1", vm.Notes[0].Author);
        Assert.StartsWith("2026-10-08", vm.Notes[0].WhenText, StringComparison.Ordinal);
    }
}
