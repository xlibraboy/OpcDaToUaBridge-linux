using OpcBridge.Client;
using OpcBridge.Hmi.ViewModels.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic page's Tags tab and the area-tag references rows carry: a block reads its area's
/// named tags, assigned once (on the tab) and shared by every block of the area — including the
/// "created empty, assigned later" flow, where an unassigned entry must keep a block savable and
/// readable as unknown rather than as a silent false.
/// </summary>
public sealed class HmiLogicTagsTests
{
    private const string StartItemId = "ns=2;s=Status/Start";
    private const string DoneItemId = "ns=2;s=Status/Done";

    private static LogicTagCatalog Catalog() => new(
        new[] { new LogicSourceOption("sim", "Simulation") },
        new[]
        {
            new LogicTagOption("sim", StartItemId, "Dry End Start Permit", "Simulation", Writeable: true),
            new LogicTagOption("sim", DoneItemId, "Dry End Done", "Simulation", Writeable: true)
        });

    private static LogicAreaTagsDto Source(string name = "DRYEND", params (string Name, string ItemId)[] tags)
    {
        LogicAreaTagsDto source = new() { Name = name };
        foreach ((string tagName, string itemId) in tags)
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

    private static LogicBlockDto NamedBlock(params string[] tags) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Dry end start",
        Group = "DRYEND",
        TagSource = "DRYEND",
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
    public void Load_ShowsTheMachinesEntriesAndTheNameTheBlockCarries()
    {
        LogicAreaTagsViewModel tab = new();
        LogicBlockDto block = NamedBlock("START_PB");

        tab.Load(
            block,
            new[] { Source("DRYEND", ("START_PB", StartItemId), ("FAULT", "")) },
            new[] { block, NamedBlock("START_PB") },
            Catalog());

        Assert.Equal("DRYEND", tab.Name);
        Assert.Equal(new[] { "START_PB", "FAULT" }, tab.Entries.Select(entry => entry.Name));
        Assert.Equal(StartItemId, tab.Entries[0].ItemId);
        Assert.False(tab.Entries[1].IsAssigned);
        Assert.Equal(LogicText.TagNotAssigned, tab.Entries[1].TagText);
        Assert.Equal(2, tab.UsedByCount);
        Assert.True(tab.HasUsedBy);
        Assert.False(tab.IsDirty);

        // The referenced entry is marked; a stored entry nobody reads is not.
        Assert.True(tab.Entries[0].UsedByThisBlock);
        Assert.False(tab.Entries[1].UsedByThisBlock);
    }

    [Fact]
    public void Load_ReadsTheAreasTagsEvenWhenTheBlockStoresAnotherName()
    {
        LogicAreaTagsViewModel tab = new();
        LogicBlockDto block = NamedBlock("START_PB");
        block.Area = "DRY END";

        tab.Load(
            block,
            new[] { Source("DRYEND"), Source("DRY END", ("START_PB", StartItemId)) },
            new[] { block },
            Catalog());

        // The area is the tag-sharing key: it wins over the name the block stores, and the area
        // a loaded block carries is offered in the field's suggestions even before it is saved.
        Assert.Equal("DRY END", tab.Name);
        Assert.Equal(StartItemId, Assert.Single(tab.Entries).ItemId);
        Assert.Contains("DRY END", tab.AreaNames);
        Assert.True(tab.IsKnownArea);
    }

    [Fact]
    public void Load_DefaultsTheNameFromTheAreaThenTheStoredNameThenTheGroup()
    {
        LogicAreaTagsViewModel tab = new();
        LogicBlockDto block = NamedBlock();
        block.Area = "DRY END";
        block.TagSource = "WETEND";

        tab.Load(block, Array.Empty<LogicAreaTagsDto>(), new[] { block }, Catalog());

        Assert.Equal("DRY END", tab.Name);
        Assert.Empty(tab.Entries);

        LogicBlockDto legacy = NamedBlock();
        legacy.TagSource = string.Empty;

        tab.Load(legacy, Array.Empty<LogicAreaTagsDto>(), new[] { legacy }, Catalog());

        // No area and no stored name: the group fallback keeps the block resolving as before.
        Assert.Equal("DRYEND", tab.Name);
        Assert.Empty(tab.Entries);
    }

    [Fact]
    public void Load_CreatesAnEntryForAReferenceTheSourceDoesNotHold()
    {
        LogicAreaTagsViewModel tab = new();
        LogicBlockDto block = NamedBlock("GHOST_PB");

        tab.Load(block, new[] { Source("DRYEND") }, new[] { block }, Catalog());

        // The reference is shown (unassigned) instead of hidden, so it can be bound here — and a
        // load that changed nothing is not an unsaved edit.
        LogicTagEntryViewModel entry = Assert.Single(tab.Entries);
        Assert.Equal("GHOST_PB", entry.Name);
        Assert.False(entry.IsAssigned);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Load_ASecondBlockOfAnotherMachineAdoptsItWithoutDuplicatingEntries()
    {
        LogicAreaTagsViewModel tab = new();
        LogicBlockDto dry = NamedBlock("START_PB");
        LogicAreaTagsDto dryEnd = Source("DRYEND", ("START_PB", StartItemId));
        LogicAreaTagsDto wetEnd = Source("WETEND", ("PUMP_RUN", StartItemId));

        tab.Load(dry, new[] { dryEnd, wetEnd }, new[] { dry }, Catalog());
        Assert.Equal("START_PB", Assert.Single(tab.Entries).Name);

        LogicBlockDto wet = NamedBlock("PUMP_RUN");
        wet.Name = "Wet end start";
        wet.Group = "WETEND";
        wet.TagSource = "WETEND";
        tab.Load(wet, new[] { dryEnd, wetEnd }, new[] { dry, wet }, Catalog());

        // The second block's machine replaces the first's entries — once, not twice.
        LogicTagEntryViewModel entry = Assert.Single(tab.Entries);
        Assert.Equal("PUMP_RUN", entry.Name);
        Assert.Equal("WETEND", tab.Name);
        Assert.Equal(1, tab.UsedByCount);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void NewTag_AddsUniqueUnassignedEntriesAndDirtiesTheTab()
    {
        LogicAreaTagsViewModel tab = new();
        LogicBlockDto block = NamedBlock();
        tab.Load(block, Array.Empty<LogicAreaTagsDto>(), new[] { block }, Catalog());

        LogicTagEntryViewModel first = tab.NewTag();
        LogicTagEntryViewModel second = tab.NewTag();

        Assert.Equal("TAG1", first.Name);
        Assert.Equal("TAG2", second.Name);
        Assert.False(first.IsAssigned);
        Assert.True(tab.IsDirty);
        Assert.True(tab.HasEntries);

        tab.MarkSaved(tab.ToDto());
        Assert.False(tab.IsDirty);

        first.Name = "START_PB";
        first.SelectedSource = first.Sources.First(source => source.SourceId == "sim");
        first.SelectedTag = first.Items.First(item => item.ItemId == StartItemId);
        Assert.True(first.IsAssigned);
        Assert.True(tab.IsDirty);

        // Removing an entry is an edit too.
        tab.RemoveEntry(second);
        Assert.Single(tab.Entries);
        Assert.DoesNotContain("TAG2", tab.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void EnsureEntry_ReusesTheEntryCarryingTheSameTagAndFindsNamesCaseInsensitively()
    {
        LogicAreaTagsViewModel tab = new();
        LogicBlockDto block = NamedBlock();
        tab.Load(block, new[] { Source("DRYEND", ("START_PB", StartItemId)) }, new[] { block }, Catalog());

        LogicTagEntryViewModel existing = tab.EnsureEntry("SOMETHING_ELSE", "sim", StartItemId);

        Assert.Equal("START_PB", existing.Name);
        Assert.Single(tab.Entries);
        Assert.Same(existing, tab.FindByName("start_pb"));
        Assert.Null(tab.FindByName("  "));
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void ConditionRow_CarriesTheMachineTagAloneAndHidesTheDirectPickers()
    {
        LogicBlockDto block = NamedBlock("START_PB");
        LogicAreaTagsViewModel tab = new();
        tab.Load(block, new[] { Source("DRYEND", ("START_PB", StartItemId)) }, new[] { block }, Catalog());

        LogicConditionRowViewModel row = LogicEditorCodec.LoadConditions(
            block.Conditions, Catalog(), null, tab.Entries)[0];

        Assert.Equal("START_PB", row.Tag);
        Assert.True(row.HasBlockTag);
        Assert.False(row.ShowsDirectTag);
        Assert.Same(tab.Entries[0], row.SelectedBlockTag);

        LogicConditionDto saved = LogicEditorCodec.ConditionFromRow(row);
        Assert.Equal("START_PB", saved.Tag);
        Assert.Equal(string.Empty, saved.SourceId);
        Assert.Equal(string.Empty, saved.ItemId);

        // Clearing the machine tag brings the direct pickers back, and the pair is written again.
        row.ClearBlockTagCommand.Execute(null);
        Assert.False(row.HasBlockTag);
        Assert.True(row.ShowsDirectTag);
        LogicConditionDto direct = LogicEditorCodec.ConditionFromRow(row);
        Assert.Equal(string.Empty, direct.Tag);
        Assert.Equal(LogicConditionOps.On, direct.Op);
    }

    [Fact]
    public void ConditionRow_WithoutAMachineTagKeepsTheStoredDirectPair()
    {
        LogicConditionDto condition = new()
        {
            Id = Guid.NewGuid(),
            Text = "Line 01 permit must be given",
            SourceId = "sim",
            ItemId = StartItemId,
            Op = LogicConditionOps.On
        };

        LogicConditionRowViewModel row = new(condition, Catalog());

        LogicConditionDto saved = LogicEditorCodec.ConditionFromRow(row);

        Assert.Equal(string.Empty, saved.Tag);
        Assert.Equal("sim", saved.SourceId);
        Assert.Equal(StartItemId, saved.ItemId);
    }

    [Fact]
    public void ElementRow_ANamedTagKeepsTheContactAPairAndARelayWins()
    {
        LogicBlockDto block = NamedBlock("START_PB");
        LogicAreaTagsViewModel tab = new();
        tab.Load(block, new[] { Source("DRYEND", ("START_PB", StartItemId)) }, new[] { block }, Catalog());

        LogicElementDto named = new()
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.Contact,
            Text = "Start permit",
            Tag = "START_PB",
            Op = LogicConditionOps.On
        };
        LogicElementRowViewModel row = new(named, 0, Catalog(), null, null, tab.Entries);

        Assert.True(row.ShowsBlockTag);
        Assert.False(row.ShowsDirectTag);
        Assert.Equal("START_PB", row.Tag);

        LogicElementDto saved = LogicEditorCodec.ElementFromRow(row);
        Assert.Equal("START_PB", saved.Tag);
        Assert.Equal(string.Empty, saved.SourceId);
        Assert.Equal(string.Empty, saved.ItemId);

        // A relay on a contact wins: the machine tag is dropped, exactly like a direct pair.
        row.Relay = "M01";
        Assert.True(row.IsRelayContact);
        Assert.False(row.ShowsDirectTag);
        saved = LogicEditorCodec.ElementFromRow(row);
        Assert.Equal("M01", saved.Relay);
        Assert.Equal(string.Empty, saved.Tag);

        // And a direct contact keeps its pair with no machine tag.
        row.Relay = string.Empty;
        row.ClearBlockTagCommand.Execute(null);
        Assert.True(row.ShowsDirectTag);
        saved = LogicEditorCodec.ElementFromRow(row);
        Assert.Equal(string.Empty, saved.Tag);
        Assert.Equal(string.Empty, saved.ItemId);
    }

    [Fact]
    public void StepRow_CarriesTheCompletionTagOrTheDirectPair()
    {
        LogicBlockDto block = NamedBlock();
        LogicAreaTagsViewModel tab = new();
        tab.Load(
            block,
            new[] { Source("DRYEND", ("START_PB", StartItemId), ("DONE_FB", DoneItemId)) },
            new[] { block },
            Catalog());

        LogicStepDto step = new()
        {
            Id = Guid.NewGuid(),
            Name = "Start",
            CompletionTag = "DONE_FB",
            Conditions = { new LogicConditionDto { Id = Guid.NewGuid(), Text = "start", Tag = "START_PB", Op = LogicConditionOps.On } }
        };

        LogicStepRowViewModel row = new(step, Catalog(), new System.Collections.ObjectModel.ObservableCollection<LogicStepRowViewModel>(), null, null, tab.Entries);

        Assert.Equal("DONE_FB", row.CompletionTagName);
        Assert.False(row.ShowsDirectCompletion);
        LogicStepDto saved = LogicEditorCodec.StepFromRow(row);
        Assert.Equal("DONE_FB", saved.CompletionTag);
        Assert.Null(saved.CompletionSourceId);
        Assert.Null(saved.CompletionItemId);

        // The step's own condition carries the machine tag too; clearing the handshake leaves
        // the direct pair unset (an optional handshake needs both fields or a machine tag).
        Assert.Equal("START_PB", Assert.Single(saved.Conditions).Tag);
        row.ClearCompletionTagCommand.Execute(null);
        saved = LogicEditorCodec.StepFromRow(row);
        Assert.Null(saved.CompletionTag);
        Assert.Null(saved.CompletionSourceId);
        Assert.Null(saved.CompletionItemId);
    }

    [Fact]
    public void ActionRow_CarriesTheMachineTagAlone()
    {
        LogicBlockDto block = NamedBlock();
        LogicAreaTagsViewModel tab = new();
        tab.Load(block, new[] { Source("DRYEND", ("START_PB", StartItemId)) }, new[] { block }, Catalog());

        LogicActionDto action = new()
        {
            Label = "Start",
            Tag = "START_PB",
            Value = "true",
            Confirm = true
        };

        LogicActionRowViewModel row = new(action, Catalog(), null, tab.Entries);

        Assert.Equal("START_PB", row.Tag);
        Assert.False(row.ShowsDirectTag);
        LogicActionDto saved = LogicEditorCodec.ActionFromRow(row);
        Assert.Equal("START_PB", saved.Tag);
        Assert.Equal(string.Empty, saved.ItemId);
    }

    [Fact]
    public void Build_CarriesTheAreaAndTheAreaTagsAndRoundTripsTheStoredBlock()
    {
        LogicBlockDto stored = NamedBlock("START_PB");
        stored.Area = "DRY END";
        stored.TagSource = "DRY END";
        LogicAreaTagsViewModel tab = new();
        tab.Load(stored, new[] { Source("DRY END", ("START_PB", StartItemId)) }, new[] { stored }, Catalog());
        LogicTagCatalog catalog = Catalog();

        var conditions = LogicEditorCodec.LoadConditions(stored.Conditions, catalog, null, tab.Entries);
        var elements = LogicEditorCodec.LoadElements(stored.Elements, catalog, null, null, tab.Entries);
        var steps = LogicEditorCodec.LoadSteps(stored.Steps, catalog, null, null, null, tab.Entries);
        var actions = LogicEditorCodec.LoadActions(stored.Actions, catalog, null, tab.Entries);

        LogicBlockDto built = LogicEditorCodec.Build(
            stored,
            0,
            stored.Name,
            stored.Kind,
            stored.Enabled,
            stored.Group,
            stored.Area,
            tab.Name,
            stored.Description,
            string.Empty,
            conditions,
            elements,
            steps,
            actions);

        // A load that changed nothing must compare equal, or Save would light up on every select.
        Assert.Equal("DRY END", built.Area);
        Assert.Equal("DRY END", built.TagSource);
        Assert.True(LogicEditorCodec.Matches(built, stored));
    }

    [Fact]
    public void RefreshUsed_MarksOnlyTheEntriesTheBlockReferences()
    {
        LogicAreaTagsViewModel tab = new();
        LogicBlockDto block = NamedBlock();
        tab.Load(block, new[] { Source("DRYEND", ("START_PB", StartItemId), ("FAULT", "")) }, new[] { block }, Catalog());

        tab.RefreshUsed(new[] { "START_PB" });

        Assert.True(tab.Entries[0].UsedByThisBlock);
        Assert.False(tab.Entries[1].UsedByThisBlock);
    }
}
