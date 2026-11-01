using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Elements screen flattens every block's network (and every sequence step's) into one
/// stable, grouped list. It renders the live snapshot while connected and the persisted
/// last-known snapshot otherwise, without ever mixing the two.
/// </summary>
public sealed class MobileElementsTests
{
    private const string BridgeKey = "mobile:test";

    private static readonly DateTime LiveUtc = new(2026, 11, 2, 1, 56, 0, DateTimeKind.Utc);
    private static readonly DateTime SavedUtc = new(2026, 11, 2, 1, 55, 22, DateTimeKind.Utc);

    private static LogicConditionDto Condition(string text, string itemId) => new()
    {
        Id = Guid.NewGuid(),
        Text = text,
        SourceId = "sim",
        ItemId = itemId,
        Op = LogicConditionOps.On
    };

    private static LogicBlockDto Block(string name, string kind, params LogicConditionDto[] conditions) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = kind,
        Conditions = conditions.ToList()
    };

    private static HmiTagDto Tag(bool value = false) => new()
    {
        SourceId = "sim",
        SourceName = "Simulation",
        ItemId = "PS1",
        DisplayName = "Pressure switch PS1",
        Digital = true,
        OnText = "On",
        OffText = "Off",
        Value = value,
        IsGood = true,
        TimestampUtc = SavedUtc
    };

    private static MultiBridgeTagCache Tags(HmiTagDto tag)
    {
        MultiBridgeTagCache cache = new();
        cache.ReplaceBridge(BridgeKey, new[] { tag });
        return cache;
    }

    private static LogicStateSnapshot State(
        LogicBlockDto block,
        string blockState,
        params LogicElementStateDto[] elements) => new()
    {
        EvaluatedUtc = LiveUtc,
        Blocks =
        {
            new LogicBlockStateDto
            {
                Id = block.Id,
                State = blockState,
                Elements = elements.ToList()
            }
        }
    };

    [Fact]
    public void LiveState_RendersMarksReadingsAndTheShouldLine()
    {
        LogicConditionDto permit = Condition("Pressure switch PS1 must read 1", "PS1");
        LogicBlockDto block = Block("Line 01 Start", LogicBlockKinds.Interlock, permit);
        MultiBridgeTagCache tags = Tags(Tag());
        LogicStateSnapshot state = State(
            block,
            LogicBlockStates.Blocked,
            new LogicElementStateDto
            {
                Id = permit.Id,
                Kind = LogicElementKinds.Contact,
                State = LogicConditionStates.False,
                Should = "1",
                Matches = false,
                ValueText = "Off",
                TimestampUtc = SavedUtc
            });

        ElementsViewModel vm = new();
        vm.ApplyDefinitions(new[] { block }, null, tags, BridgeKey);
        vm.ApplyState(state, tags, BridgeKey, lastKnown: false, LiveUtc);

        LogicElementRowViewModel row = Assert.Single(vm.AllRows);
        Assert.Equal("Pressure switch PS1 must read 1", row.Text);
        Assert.Equal("✗", row.Mark);
        Assert.Equal("0", row.ActualText);
        Assert.Equal("Off", row.ValueText);
        Assert.Equal("should 1 · actual 0", row.ShouldLine);
        Assert.False(row.IsLastKnown);
        Assert.Contains("Pressure switch PS1", row.TagLabel);

        ElementBlockGroupViewModel group = Assert.Single(vm.VisibleGroups);
        Assert.Equal("Line 01 Start", group.Name);
        Assert.Equal("BLOCKED", group.StateLabel);
        Assert.Contains("live", vm.SourceText);
        Assert.False(vm.ShowingLastKnown);
    }

    [Fact]
    public void LastKnownState_RendersThePersistedReadingAndItsTime()
    {
        LogicConditionDto permit = Condition("Pressure switch PS1 must read 1", "PS1");
        LogicBlockDto block = Block("Line 01 Start", LogicBlockKinds.Interlock, permit);
        HmiTagDto tag = Tag();
        LogicStateSnapshot state = State(
            block,
            LogicBlockStates.Blocked,
            new LogicElementStateDto
            {
                Id = permit.Id,
                Kind = LogicElementKinds.Contact,
                State = LogicConditionStates.False,
                Should = "1",
                Matches = false,
                ValueText = "Off",
                TimestampUtc = SavedUtc
            });
        BridgeLastKnown known = new(
            SavedUtc,
            new[] { block },
            Array.Empty<LogicAreaTagsDto>(),
            state,
            new[] { tag });
        MultiBridgeTagCache knownTags = new();
        knownTags.ReplaceBridge(BridgeKey, known.Tags);

        ElementsViewModel vm = new();
        vm.ApplyDefinitions(known.Blocks, known.TagSources, knownTags, BridgeKey);
        vm.ApplyState(known.State, knownTags, BridgeKey, lastKnown: true, known.SavedUtc);

        LogicElementRowViewModel row = Assert.Single(vm.AllRows);
        Assert.True(row.IsLastKnown);
        Assert.Equal("Off", row.ValueText);
        Assert.Equal(SavedUtc.ToLocalTime().ToString("HH:mm:ss"), row.LastReadText);
        Assert.True(vm.ShowingLastKnown);
        Assert.StartsWith("last known · ", vm.SourceText);
    }

    [Fact]
    public void SequenceSteps_AreFlattenedWithTheirStepLabel()
    {
        LogicStepDto step = new()
        {
            Id = Guid.NewGuid(),
            Name = "Open valve",
            Conditions =
            {
                Condition("Valve 01 must be open", "Valve01"),
                Condition("Pump must be running", "Pump")
            }
        };
        LogicBlockDto sequence = new()
        {
            Id = Guid.NewGuid(),
            Name = "Start-up",
            Kind = LogicBlockKinds.Sequence,
            Steps = { step }
        };

        ElementsViewModel vm = new();
        vm.ApplyDefinitions(new[] { sequence }, null, null, BridgeKey);

        // Two conditions expand into an AND and two contacts.
        Assert.Equal(3, vm.AllRows.Count);
        Assert.All(vm.AllRows, row => Assert.Equal("Step 1 · Open valve", row.StepLabel));
        Assert.All(vm.AllRows, row => Assert.Equal(sequence.Id, row.BlockId));
    }

    [Fact]
    public void SearchAndKindFilter_NarrowTheGroupsAndRows()
    {
        LogicBlockDto interlock = Block(
            "Line 01 Start",
            LogicBlockKinds.Interlock,
            Condition("Pump permit must be given", "Pump"));
        LogicStepDto step = new()
        {
            Id = Guid.NewGuid(),
            Name = "Start",
            Conditions = { Condition("Valve must be open", "Valve") }
        };
        LogicBlockDto sequence = new()
        {
            Id = Guid.NewGuid(),
            Name = "Start-up",
            Kind = LogicBlockKinds.Sequence,
            Steps = { step }
        };

        ElementsViewModel vm = new();
        vm.ApplyDefinitions(new[] { interlock, sequence }, null, null, BridgeKey);

        vm.SearchText = "pump";
        Assert.Single(vm.VisibleGroups);
        Assert.Equal("Line 01 Start", vm.VisibleGroups[0].Name);
        Assert.Single(vm.VisibleGroups[0]);
        Assert.Contains("of", vm.FilterText);

        vm.SearchText = string.Empty;
        vm.SelectKindFilter(vm.KindFilters.Single(chip => chip.Key == LogicBlockKinds.Sequence));
        Assert.Single(vm.VisibleGroups);
        Assert.Equal("Start-up", vm.VisibleGroups[0].Name);
        Assert.Equal("Valve must be open", Assert.Single(vm.VisibleGroups[0]).Text);
    }

    [Fact]
    public void DefinitionsAndRows_StayStableAcrossStatePushes()
    {
        LogicConditionDto permit = Condition("Pump permit must be given", "Pump");
        LogicBlockDto block = Block("Line 01 Start", LogicBlockKinds.Interlock, permit);
        List<LogicBlockDto> definitions = new() { block };
        MultiBridgeTagCache tags = Tags(Tag(value: true));
        ElementsViewModel vm = new();
        vm.ApplyDefinitions(definitions, null, tags, BridgeKey);
        ElementBlockGroupViewModel group = Assert.Single(vm.VisibleGroups);
        LogicElementRowViewModel row = Assert.Single(vm.AllRows);

        vm.ApplyState(
            State(block, LogicBlockStates.Ready,
                new LogicElementStateDto { Id = permit.Id, State = LogicConditionStates.True, Should = "1", Matches = true, ValueText = "On" }),
            tags,
            BridgeKey,
            lastKnown: false,
            LiveUtc);
        vm.ApplyDefinitions(definitions, null, tags, BridgeKey);

        Assert.Same(group, Assert.Single(vm.VisibleGroups));
        Assert.Same(row, Assert.Single(vm.AllRows));
        Assert.Equal("READY", group.StateLabel);
    }

    [Fact]
    public void GroupsAreOrderedByBlockOrderThenName()
    {
        LogicBlockDto second = Block("Beta", LogicBlockKinds.Interlock, Condition("b", "B"));
        second.Order = 2;
        LogicBlockDto first = Block("Alpha", LogicBlockKinds.Interlock, Condition("a", "A"));
        first.Order = 1;

        ElementsViewModel vm = new();
        vm.ApplyDefinitions(new[] { second, first }, null, null, BridgeKey);

        Assert.Equal(new[] { "Alpha", "Beta" }, vm.VisibleGroups.Select(group => group.Name));
    }
}
