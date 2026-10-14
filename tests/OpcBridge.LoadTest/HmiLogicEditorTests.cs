using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic page's editor model, without a bridge: the collect rules that mirror the Logic web
/// app (sequence → steps only; element rows → the network only; otherwise the flat conditions),
/// the indentation tree, the dirty comparison, the row guards and the wording the surface shows
/// while a bridge is quiet, unreachable, or never answered at all.
/// </summary>
public sealed class HmiLogicEditorTests
{
    private static LogicTagCatalog Catalog() => LogicTagCatalog.Build(new[]
    {
        new MultiBridgeTagEntry
        {
            Key = TagBindingKey.Create("default", "sim", "ns=2;s=Status/Line01.Permit"),
            DisplayName = "Line 01 Start Permit",
            SourceName = "Simulation",
            Writeable = true
        }
    });

    private static LogicBlockDto StoredInterlock() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Line 01 Start",
        Group = "Primary Arm",
        Description = "Start permit chain",
        Kind = LogicBlockKinds.Interlock,
        Enabled = true,
        Order = 3,
        Tags = { "Line 1", "Safety" },
        Conditions =
        {
            new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                Text = "Line 01 start permit must be given",
                SourceId = "sim",
                ItemId = "ns=2;s=Status/Line01.Permit",
                Op = LogicConditionOps.On
            },
            new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                Text = "Line 01 level above 50 %",
                SourceId = "sim",
                ItemId = "Level",
                Op = LogicConditionOps.GreaterThan,
                Value = 50,
                Group = "Level ok",
                HoldMs = 1500,
                NextStepText = "Wait for the level"
            }
        },
        Actions =
        {
            new LogicActionDto
            {
                Label = "Acknowledge",
                SourceId = "sim",
                ItemId = "Ack",
                Value = "1",
                Confirm = true
            }
        }
    };

    private static LogicBlockDto BuildFromStored(LogicBlockDto stored)
    {
        LogicTagCatalog catalog = Catalog();
        return LogicEditorCodec.Build(
            stored,
            stored.Order,
            stored.Name,
            stored.Kind,
            stored.Enabled,
            stored.Group,
            stored.Area,
            stored.TagSource,
            stored.Description,
            string.Join(", ", stored.Tags),
            LogicEditorCodec.LoadConditions(stored.Conditions, catalog, null),
            LogicEditorCodec.LoadElements(stored.Elements, catalog, null),
            LogicEditorCodec.LoadSteps(stored.Steps, catalog),
            LogicEditorCodec.LoadActions(stored.Actions, catalog, null));
    }

    [Fact]
    public void SimpleConditions_RoundTripTheStoredBlock()
    {
        LogicBlockDto stored = StoredInterlock();
        LogicBlockDto built = BuildFromStored(stored);

        Assert.True(LogicEditorCodec.Matches(built, stored));
        Assert.Equal(stored.Id, built.Id);
        Assert.Equal(stored.Order, built.Order);
        Assert.Equal(new[] { "Line 1", "Safety" }, built.Tags);
        Assert.Equal(50, built.Conditions[1].Value);
        Assert.Equal(1500, built.Conditions[1].HoldMs);
        Assert.Equal("Wait for the level", built.Conditions[1].NextStepText);
        Assert.Empty(built.Elements);
        Assert.Empty(built.Steps);
        Assert.Equal("Acknowledge", built.Actions[0].Label);
    }

    [Fact]
    public void AChangedRow_BreaksTheMatch()
    {
        LogicBlockDto stored = StoredInterlock();
        LogicTagCatalog catalog = Catalog();
        List<LogicConditionRowViewModel> conditions = LogicEditorCodec.LoadConditions(stored.Conditions, catalog, null);
        LogicBlockDto built = LogicEditorCodec.Build(
            stored, stored.Order, stored.Name, stored.Kind, stored.Enabled, stored.Group, stored.Area, stored.TagSource,
            stored.Description, string.Join(", ", stored.Tags), conditions, new List<LogicElementRowViewModel>(),
            new List<LogicStepRowViewModel>(), LogicEditorCodec.LoadActions(stored.Actions, catalog, null));

        Assert.True(LogicEditorCodec.Matches(built, stored));
        conditions[0].Text = "Line 01 start permit must be given (edited)";
        Assert.False(LogicEditorCodec.Matches(
            LogicEditorCodec.Build(
                stored, stored.Order, stored.Name, stored.Kind, stored.Enabled, stored.Group, stored.Area, stored.TagSource,
                stored.Description, string.Join(", ", stored.Tags), conditions, new List<LogicElementRowViewModel>(),
                new List<LogicStepRowViewModel>(), LogicEditorCodec.LoadActions(stored.Actions, catalog, null)),
            stored));
    }

    [Fact]
    public void NetworkForm_RoundTrips_AndCarriesTheNetworkOnly()
    {
        var stored = new LogicBlockDto
        {
            Id = Guid.NewGuid(),
            Name = "Line 02 Start",
            Kind = LogicBlockKinds.Interlock,
            Elements =
            {
                new LogicElementDto
                {
                    Id = Guid.NewGuid(),
                    Kind = LogicElementKinds.And,
                    Inputs =
                    {
                        new LogicElementDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = LogicElementKinds.Contact,
                            Text = "Permit",
                            SourceId = "sim",
                            ItemId = "Permit",
                            Op = LogicConditionOps.On
                        },
                        new LogicElementDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = LogicElementKinds.Ton,
                            Text = "Hold",
                            PtMs = 3000,
                            Inputs =
                            {
                                new LogicElementDto
                                {
                                    Id = Guid.NewGuid(),
                                    Kind = LogicElementKinds.Contact,
                                    Text = "Running",
                                    SourceId = "sim",
                                    ItemId = "Run",
                                    Op = LogicConditionOps.On
                                }
                            }
                        }
                    }
                }
            }
        };

        LogicBlockDto built = BuildFromStored(stored);

        Assert.True(LogicEditorCodec.Matches(built, stored));
        Assert.Empty(built.Conditions);
        Assert.Empty(built.Steps);
        Assert.Equal(LogicElementKinds.And, built.Elements[0].Kind);
        Assert.Equal(2, built.Elements[0].Inputs.Count);
        Assert.Equal(3000, built.Elements[0].Inputs[1].PtMs);
    }

    [Fact]
    public void SequenceForm_CarriesStepsOnly_AndKeepsStepActions()
    {
        Guid stepId = Guid.NewGuid();
        var stored = new LogicBlockDto
        {
            Id = Guid.NewGuid(),
            Name = "Start sequence",
            Kind = LogicBlockKinds.Sequence,
            Steps =
            {
                new LogicStepDto
                {
                    Id = stepId,
                    Name = "Open valve",
                    NextStepText = "Wait for flow",
                    CompletionSourceId = "sim",
                    CompletionItemId = "Handshake",
                    Conditions =
                    {
                        new LogicConditionDto
                        {
                            Id = Guid.NewGuid(),
                            Text = "Valve is closed",
                            SourceId = "sim",
                            ItemId = "ValveClosed",
                            Op = LogicConditionOps.On
                        }
                    },
                    Actions =
                    {
                        new LogicActionDto { Label = "Open", SourceId = "sim", ItemId = "Valve", Value = "1", Confirm = false }
                    }
                }
            }
        };

        LogicBlockDto built = BuildFromStored(stored);

        Assert.True(LogicEditorCodec.Matches(built, stored));
        Assert.Empty(built.Conditions);
        Assert.Empty(built.Elements);
        LogicStepDto step = Assert.Single(built.Steps);
        Assert.Equal(stepId, step.Id);
        Assert.Equal("Open valve", step.Name);
        Assert.Equal("Wait for flow", step.NextStepText);
        Assert.Equal("sim", step.CompletionSourceId);
        Assert.Equal("Handshake", step.CompletionItemId);
        Assert.Equal("Open", Assert.Single(step.Actions).Label);
        Assert.Equal("Valve is closed", Assert.Single(step.Conditions).Text);
    }

    [Fact]
    public void ASequenceNeverCarriesBlockLevelConditions()
    {
        // Kind switched to sequence while condition rows were still around: the collect rule
        // keeps the steps and clears the block-level lists (the bridge refuses both forms).
        LogicTagCatalog catalog = Catalog();
        var block = new LogicBlockDto { Id = Guid.NewGuid(), Name = "Seq", Kind = LogicBlockKinds.Sequence };
        LogicBlockDto built = LogicEditorCodec.Build(
            block,
            0,
            "Seq",
            LogicBlockKinds.Sequence,
            true,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            LogicEditorCodec.LoadConditions(
                new[]
                {
                    new LogicConditionDto { Id = Guid.NewGuid(), Text = "Left over", SourceId = "sim", ItemId = "X", Op = LogicConditionOps.On }
                },
                catalog,
                null),
            new List<LogicElementRowViewModel>(),
            LogicEditorCodec.LoadSteps(
                new[] { new LogicStepDto { Id = Guid.NewGuid(), Name = "Step 1" } },
                catalog),
            new List<LogicActionRowViewModel>());

        Assert.Empty(built.Conditions);
        Assert.Empty(built.Elements);
        Assert.Single(built.Steps);
    }

    [Fact]
    public void ElementTreeFromRows_NestsByIndent()
    {
        LogicTagCatalog catalog = Catalog();
        var rows = new List<LogicElementRowViewModel>
        {
            new(new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.And }, 0, catalog),
            new(new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact }, 1, catalog),
            new(new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact }, 1, catalog),
            new(new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact }, 0, catalog)
        };

        List<LogicElementDto> tree = LogicEditorCodec.ElementTreeFromRows(rows);

        Assert.Equal(2, tree.Count);
        Assert.Equal(2, tree[0].Inputs.Count);
        Assert.Empty(tree[1].Inputs);
        Assert.Equal(rows[0].ElementId, tree[0].Id);
        Assert.Equal(rows[2].ElementId, tree[0].Inputs[1].Id);
        Assert.Equal(rows[3].ElementId, tree[1].Id);
    }

    [Fact]
    public void ConvertToNetwork_MatchesTheBridgesOwnExpansion()
    {
        // The same conditions the bridge expands server-side: the editor's convert must produce
        // the identical network (ids included — the derived wrappers keep timer memory stable).
        LogicConditionDto[] conditions =
        {
            new() { Id = Guid.NewGuid(), Text = "Permit", SourceId = "sim", ItemId = "Permit", Op = LogicConditionOps.On },
            new() { Id = Guid.NewGuid(), Text = "Level", SourceId = "sim", ItemId = "Level", Op = LogicConditionOps.GreaterThan, Value = 50, Group = "Level ok" },
            new() { Id = Guid.NewGuid(), Text = "Held", SourceId = "sim", ItemId = "Held", Op = LogicConditionOps.On, HoldMs = 2000 }
        };

        List<LogicElementDto> expanded = LogicNetwork.Expand(conditions);
        List<LogicElementRowViewModel> rows = LogicEditorCodec.LoadElements(expanded, Catalog(), null);
        List<LogicElementDto> rebuilt = LogicEditorCodec.ElementTreeFromRows(rows);

        Assert.Equal(JsonSerializer.Serialize(expanded), JsonSerializer.Serialize(rebuilt));
    }

    [Fact]
    public void ParseTags_TrimsAndDropsBlanks()
    {
        Assert.Equal(new[] { "Line 1", "Safety" }, LogicEditorCodec.ParseTags(" Line 1 , , Safety ,, "));
        Assert.Empty(LogicEditorCodec.ParseTags(" , "));
    }

    [Fact]
    public void ConditionRow_NumericValueOnlyForNumericOps_AndHoldInMilliseconds()
    {
        LogicTagCatalog catalog = Catalog();
        var on = new LogicConditionRowViewModel(
            new LogicConditionDto { Id = Guid.NewGuid(), Op = LogicConditionOps.On, Value = 5 },
            catalog);
        Assert.Null(LogicEditorCodec.ConditionFromRow(on).Value);

        var greater = new LogicConditionRowViewModel(
            new LogicConditionDto { Id = Guid.NewGuid(), Op = LogicConditionOps.GreaterThan, Value = 50, HoldMs = 1500 },
            catalog);
        greater.HoldSeconds = "1.5";
        LogicConditionDto built = LogicEditorCodec.ConditionFromRow(greater);
        Assert.Equal(50, built.Value);
        Assert.Equal(1500, built.HoldMs);
        Assert.True(greater.NeedsValue);
    }

    [Fact]
    public void ElementRow_OnlyTheFieldsOfItsKindAreSet()
    {
        LogicTagCatalog catalog = Catalog();
        var timer = new LogicElementRowViewModel(
            new LogicElementDto
            {
                Id = Guid.NewGuid(),
                Kind = LogicElementKinds.Ton,
                Text = "Hold",
                PtMs = 3000,
                SourceId = "stale",
                ItemId = "stale",
                Op = LogicConditionOps.Off
            },
            2,
            catalog);
        timer.PtSeconds = "2.5";

        LogicElementDto built = LogicEditorCodec.ElementFromRow(timer);

        Assert.Equal(2500, built.PtMs);
        Assert.Equal(LogicElementKinds.Ton, built.Kind);
        Assert.Equal(string.Empty, built.SourceId);
        Assert.Equal(string.Empty, built.ItemId);
        Assert.Equal(LogicConditionOps.On, built.Op);
        Assert.Null(built.Value);
        Assert.Equal(2, timer.Indent);
    }

    [Fact]
    public void StepRow_HandshakeIsBothOrNeither_AndMoveCommandsReport()
    {
        LogicTagCatalog catalog = Catalog();
        var steps = new System.Collections.ObjectModel.ObservableCollection<LogicStepRowViewModel>();
        var list = new LogicRowList<LogicStepRowViewModel>(steps);
        var step = new LogicStepRowViewModel(
            new LogicStepDto { Id = Guid.NewGuid(), Name = "Step 1" },
            catalog,
            list);
        steps.Add(step);
        Assert.True(step.MoveUpCommand.CanExecute(null) == false);
        Assert.True(step.MoveDownCommand.CanExecute(null) == false);

        // A second step re-arms the first one's "down" and the second one's "up".
        var second = new LogicStepRowViewModel(
            new LogicStepDto { Id = Guid.NewGuid(), Name = "Step 2" },
            catalog,
            list);
        steps.Add(second);
        list.NotifyChanged();
        Assert.True(step.MoveDownCommand.CanExecute(null));
        Assert.True(second.MoveUpCommand.CanExecute(null));

        Assert.Null(step.CompletionSourceId);
        Assert.Null(step.CompletionItemId);

        step.CompletionSource = new LogicSourceOption("sim", "Simulation");
        step.CompletionTag = new LogicTagOption("sim", "Handshake", "Handshake", "Simulation", true);
        Assert.Equal("sim", step.CompletionSourceId);
        Assert.Equal("Handshake", step.CompletionItemId);

        // Half a handshake never becomes a stored id.
        step.CompletionSource = null;
        Assert.Null(step.CompletionSourceId);
        Assert.Null(step.CompletionItemId);
    }

    [Fact]
    public void TagCatalog_KeepsAStoredTagThatIsNoLongerInTheCache()
    {
        LogicTagCatalog catalog = Catalog();
        var row = new LogicConditionRowViewModel(
            new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                SourceId = "gone",
                ItemId = "Old.Tag",
                Op = LogicConditionOps.On
            },
            catalog);

        Assert.Equal("gone", row.SourceId);
        Assert.Equal("Old.Tag", row.ItemId);
        Assert.Equal("Old.Tag", row.SelectedTag?.Label);
    }

    [Fact]
    public void Freshness_NamesADeadFeed_AndAgesAQuietOne()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        // The link's own state wins over the age: a dead feed never claims a recent update.
        Assert.Equal("stale — bridge unreachable", LogicText.Freshness(BridgeLinkState.Failed, null, now));
        Assert.Equal("stale — bridge unreachable", LogicText.Freshness(BridgeLinkState.Failed, now.AddSeconds(-30), now));
        Assert.Equal("stale — reconnecting", LogicText.Freshness(BridgeLinkState.Reconnecting, now.AddSeconds(-30), now));

        // A live link with no snapshot yet, and then the age counting up.
        Assert.Equal("no state yet", LogicText.Freshness(BridgeLinkState.Connected, null, now));
        Assert.Equal("updated just now", LogicText.Freshness(BridgeLinkState.Connected, now.AddSeconds(-2), now));
        Assert.Equal("updated 12s ago", LogicText.Freshness(BridgeLinkState.Connected, now.AddSeconds(-12), now));
        Assert.Equal("updated 2m ago", LogicText.Freshness(BridgeLinkState.Connected, now.AddSeconds(-125), now));
    }

    [Fact]
    public void BlockListEmptyMessage_NamesReadingUnreachableRefusedAndEmptySeparately()
    {
        Assert.Equal(
            "Connect a bridge to load its plant logic.",
            LogicText.BlockListEmptyMessage(hasBridge: false, connected: false, loading: false, error: null));
        Assert.Equal(
            "Reading the bridge's blocks…",
            LogicText.BlockListEmptyMessage(hasBridge: true, connected: true, loading: true, error: null));
        Assert.Equal(
            "The bridge is unreachable — its blocks will appear when it answers.",
            LogicText.BlockListEmptyMessage(hasBridge: true, connected: false, loading: false, error: "boom"));
        Assert.Equal(
            "The bridge could not list its blocks — HTTP 500",
            LogicText.BlockListEmptyMessage(hasBridge: true, connected: true, loading: false, error: "HTTP 500"));
        Assert.Equal(
            "No logic blocks yet — use New block to author the first one.",
            LogicText.BlockListEmptyMessage(hasBridge: true, connected: true, loading: false, error: null));
    }

    [Fact]
    public void ResultEmptyState_DoesNotReadARefusedFetchAsNoBlocks()
    {
        Assert.Equal(
            ("COULD NOT READ", "The bridge could not list its blocks — HTTP 500"),
            LogicText.ResultEmptyState(false, false, hasBridge: true, connected: true, loading: false, error: "HTTP 500", hasBlocks: false));
        Assert.Equal(
            ("NO BLOCKS", "The bridge is unreachable — its blocks will appear when it answers."),
            LogicText.ResultEmptyState(false, false, hasBridge: true, connected: false, loading: false, error: "HTTP 500", hasBlocks: false));
        Assert.Equal(
            ("READING…", "Reading the bridge's blocks…"),
            LogicText.ResultEmptyState(false, false, hasBridge: true, connected: true, loading: true, error: null, hasBlocks: false));
        Assert.Equal(
            ("NOT SAVED YET", "Save the block to see its evaluated network."),
            LogicText.ResultEmptyState(false, true, hasBridge: true, connected: true, loading: false, error: null, hasBlocks: false));
        Assert.Equal(
            ("NO BLOCK SELECTED", "Pick a block in the list to see its evaluated network."),
            LogicText.ResultEmptyState(false, false, hasBridge: true, connected: true, loading: false, error: null, hasBlocks: true));
        Assert.Equal(
            ("NO BLOCKS YET", "Use New block to author the first one."),
            LogicText.ResultEmptyState(false, false, hasBridge: true, connected: true, loading: false, error: null, hasBlocks: false));
    }

    [Fact]
    public void RowGuards_FollowCanEdit()
    {
        var condition = new LogicConditionRowViewModel(
            new LogicConditionDto { Id = Guid.NewGuid(), Op = LogicConditionOps.On },
            Catalog());
        Assert.True(condition.RemoveCommand.CanExecute(null));
        condition.CanEdit = false;
        Assert.False(condition.RemoveCommand.CanExecute(null));

        var element = new LogicElementRowViewModel(
            new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact },
            1,
            Catalog());
        Assert.True(element.IndentRowCommand.CanExecute(null));
        Assert.True(element.OutdentRowCommand.CanExecute(null));
        element.CanEdit = false;
        Assert.False(element.IndentRowCommand.CanExecute(null));
        Assert.False(element.OutdentRowCommand.CanExecute(null));
        Assert.False(element.RemoveElementCommand.CanExecute(null));
    }

    [Fact]
    public void StepRow_AddConditionAndMove_GoWithCanEdit()
    {
        var steps = new System.Collections.ObjectModel.ObservableCollection<LogicStepRowViewModel>();
        var list = new LogicRowList<LogicStepRowViewModel>(steps);
        var step = new LogicStepRowViewModel(
            new LogicStepDto { Id = Guid.NewGuid(), Name = "Step 1" },
            Catalog(),
            list);
        steps.Add(step);
        steps.Add(new LogicStepRowViewModel(
            new LogicStepDto { Id = Guid.NewGuid(), Name = "Step 2" },
            Catalog(),
            list));
        list.NotifyChanged();

        Assert.True(step.AddConditionCommand.CanExecute(null));
        Assert.True(step.MoveDownCommand.CanExecute(null));
        step.CanEdit = false;
        Assert.False(step.AddConditionCommand.CanExecute(null));
        Assert.False(step.MoveDownCommand.CanExecute(null));
        Assert.False(step.RemoveStepCommand.CanExecute(null));
    }

    [Fact]
    public void ElementRows_MoveCarriesTheNestedRows_AndInsertStaysAtTheLevel()
    {
        LogicTagCatalog catalog = Catalog();
        var elements = new System.Collections.ObjectModel.ObservableCollection<LogicElementRowViewModel>();
        LogicRowList<LogicElementRowViewModel> list = null!;
        list = new LogicRowList<LogicElementRowViewModel>(
            elements,
            beside => new LogicElementRowViewModel(
                new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact },
                beside?.Indent ?? 0,
                catalog,
                rowList: list),
            indentOf: row => row.Indent);

        LogicElementRowViewModel Add(string kind, int indent)
        {
            var row = new LogicElementRowViewModel(
                new LogicElementDto { Id = Guid.NewGuid(), Kind = kind },
                indent,
                catalog,
                rowList: list);
            elements.Add(row);
            return row;
        }

        LogicElementRowViewModel and = Add(LogicElementKinds.And, 0);
        LogicElementRowViewModel inputA = Add(LogicElementKinds.Contact, 1);
        LogicElementRowViewModel inputB = Add(LogicElementKinds.Contact, 1);
        LogicElementRowViewModel or = Add(LogicElementKinds.Or, 0);
        LogicElementRowViewModel inputC = Add(LogicElementKinds.Contact, 1);
        list.NotifyChanged();

        // A row moves among its siblings only: the first input cannot go up, the last cannot go down.
        Assert.False(inputA.MoveUpCommand.CanExecute(null));
        Assert.True(inputA.MoveDownCommand.CanExecute(null));
        Assert.True(inputC.MoveUpCommand.CanExecute(null) == false);
        Assert.True(and.MoveUpCommand.CanExecute(null) == false);
        Assert.True(and.MoveDownCommand.CanExecute(null));

        // Moving the gate carries the rows nested under it: the tree is intact afterwards.
        or.MoveUpCommand.Execute(null);
        Assert.Equal(new[] { or, inputC, and, inputA, inputB }, elements);
        Assert.Equal(new[] { 0, 1, 0, 1, 1 }, elements.Select(row => row.Indent));

        // Insert above a child lands directly above it, at its level.
        inputA.InsertAboveCommand.Execute(null);
        Assert.Equal(6, elements.Count);
        LogicElementRowViewModel insertedAbove = elements[3];
        Assert.Equal(inputA, elements[4]);
        Assert.Equal(1, insertedAbove.Indent);
        Assert.DoesNotContain(insertedAbove, new[] { and, or, inputA, inputB, inputC });

        // Insert below a gate's last child lands after its whole group, at the same level.
        inputB.InsertBelowCommand.Execute(null);
        Assert.Equal(7, elements.Count);
        Assert.Equal(inputB, elements[5]);
        Assert.Equal(and, elements[2]);
        Assert.Equal(1, elements[6].Indent);
    }

    [Fact]
    public async Task InsertAndMoveButtons_BuildEachListWhereTheOperatorIsWorking()
    {
        await using var connections = new BridgeConnectionManager(new BridgeManagerOptions(
            PollInterval: TimeSpan.FromSeconds(5),
            SnapshotRetryDelays: new[] { TimeSpan.Zero },
            BridgeRetryDelays: new[] { TimeSpan.FromSeconds(5) },
            HubTiming: new HmiHubTiming(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), new[] { TimeSpan.Zero })));
        var panel = new LogicPanelViewModel(connections, work => work(), _ => { });
        panel.SelectedBridge = new LogicBridgeOption("default", BridgeLinkState.Connected);
        await panel.LoadSelectedBridgeAsync();
        panel.NewBlockCommand.Execute(null);

        // Conditions: the draft starts with one row, and a row's own button builds the list beside it.
        LogicConditionRowViewModel first = Assert.Single(panel.Conditions);
        Assert.True(first.InsertBelowCommand.CanExecute(null));
        Assert.False(first.MoveUpCommand.CanExecute(null));
        first.InsertBelowCommand.Execute(null);
        Assert.Equal(new[] { first, panel.Conditions[1] }, panel.Conditions);

        panel.Conditions[1].InsertAboveCommand.Execute(null);
        Assert.Equal(3, panel.Conditions.Count);
        Assert.Equal(first, panel.Conditions[0]);

        // Moving a row re-numbers the list: the row moved up is the first, the one it passed is last.
        LogicConditionRowViewModel middle = panel.Conditions[1];
        LogicConditionRowViewModel last = panel.Conditions[2];
        last.MoveUpCommand.Execute(null);
        Assert.Equal(new[] { first, last, middle }, panel.Conditions);
        Assert.False(middle.MoveDownCommand.CanExecute(null));
        Assert.True(middle.MoveUpCommand.CanExecute(null));

        // Elements: the first one flips the editor to the network, and insert stays at the level.
        panel.AddElementCommand.Execute(null);
        Assert.True(panel.ShowsNetwork);
        Assert.False(panel.ShowsConditions);
        LogicElementRowViewModel top = panel.Elements[0];
        top.InsertBelowCommand.Execute(null);
        Assert.Equal(2, panel.Elements.Count);
        Assert.Equal(0, panel.Elements[1].Indent);

        panel.Elements[1].IndentRowCommand.Execute(null);
        panel.Elements[1].InsertBelowCommand.Execute(null);
        Assert.Equal(new[] { 0, 1, 1 }, panel.Elements.Select(row => row.Indent));

        // Removing a row re-arms the guards of the rows it left behind.
        panel.Elements[1].RemoveElementCommand.Execute(null);
        Assert.Equal(new[] { 0, 1 }, panel.Elements.Select(row => row.Indent));
        Assert.False(top.MoveDownCommand.CanExecute(null));

        // Steps: inserting one renumbers the sequence, and a move renumbers it again.
        panel.SelectedKind = LogicSelectOption.Find(panel.KindOptions, LogicBlockKinds.Sequence);
        panel.AddStepCommand.Execute(null);
        LogicStepRowViewModel step = Assert.Single(panel.Steps);
        step.InsertBelowCommand.Execute(null);
        Assert.Equal(new[] { "STEP 1", "STEP 2" }, panel.Steps.Select(row => row.StepLabel));
        panel.Steps[1].MoveUpCommand.Execute(null);
        Assert.Equal(new[] { "STEP 1", "STEP 2" }, panel.Steps.Select(row => row.StepLabel));
        Assert.False(panel.Steps[0].MoveUpCommand.CanExecute(null));

        // Actions: the same buttons author the phone's button row.
        panel.AddActionCommand.Execute(null);
        LogicActionRowViewModel action = Assert.Single(panel.Actions);
        action.InsertAboveCommand.Execute(null);
        Assert.Equal(new[] { panel.Actions[0], action }, panel.Actions);
        Assert.True(panel.Actions[0].Confirm);
        Assert.False(panel.Actions[0].Label.Length > 0);
    }

    [Fact]
    public async Task LosingTheLink_DisablesTheRowControlsAndReQueriesTheAddButtons()
    {
        await using var connections = new BridgeConnectionManager(new BridgeManagerOptions(
            PollInterval: TimeSpan.FromSeconds(5),
            SnapshotRetryDelays: new[] { TimeSpan.Zero },
            BridgeRetryDelays: new[] { TimeSpan.FromSeconds(5) },
            HubTiming: new HmiHubTiming(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), new[] { TimeSpan.Zero })));
        var panel = new LogicPanelViewModel(connections, work => work(), _ => { });

        panel.SelectedBridge = new LogicBridgeOption("default", BridgeLinkState.Connected);
        await panel.LoadSelectedBridgeAsync();
        panel.NewBlockCommand.Execute(null);
        Assert.NotEmpty(panel.Conditions);
        Assert.True(panel.AddConditionCommand.CanExecute(null));
        Assert.True(panel.Conditions[0].CanEdit);

        // The command's CanExecuteChanged is a weak event: root the handler through the asserts.
        int reQueries = 0;
        EventHandler watcher = (_, _) => reQueries++;
        panel.AddConditionCommand.CanExecuteChanged += watcher;

        // A dropped link must grey the rows and the Add buttons in the same tick as the hint line.
        panel.SelectedBridge = new LogicBridgeOption("default", BridgeLinkState.Failed);

        Assert.True(reQueries > 0);
        Assert.False(panel.AddConditionCommand.CanExecute(null));
        Assert.False(panel.AddElementCommand.CanExecute(null));
        Assert.False(panel.AddStepCommand.CanExecute(null));
        Assert.False(panel.AddActionCommand.CanExecute(null));
        Assert.All(panel.Conditions, row => Assert.False(row.CanEdit));

        await panel.LoadSelectedBridgeAsync();
        Assert.All(panel.Conditions, row => Assert.False(row.CanEdit));
        GC.KeepAlive(watcher);
    }

    [Fact]
    public void RelayRows_RoundTripTheCoilAndTheRelayContact()
    {
        LogicTagCatalog catalog = Catalog();
        var stored = new LogicBlockDto
        {
            Id = Guid.NewGuid(),
            Name = "Relay network",
            Kind = LogicBlockKinds.Interlock,
            Elements =
            {
                new LogicElementDto
                {
                    Id = Guid.NewGuid(),
                    Kind = LogicElementKinds.Coil,
                    Text = "Start permit",
                    Relay = "M01",
                    Inputs =
                    {
                        new LogicElementDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = LogicElementKinds.Contact,
                            Text = "Permit must be given",
                            SourceId = "sim",
                            ItemId = "ns=2;s=Status/Line01.Permit",
                            Op = LogicConditionOps.On
                        }
                    }
                },
                new LogicElementDto
                {
                    Id = Guid.NewGuid(),
                    Kind = LogicElementKinds.Contact,
                    Text = "M01 must be on",
                    Relay = "M01",
                    Op = LogicConditionOps.On
                }
            }
        };

        List<LogicElementRowViewModel> rows = LogicEditorCodec.LoadElements(stored.Elements, catalog, null, new[] { "M01" });
        List<LogicElementDto> rebuilt = LogicEditorCodec.ElementTreeFromRows(rows);

        Assert.Equal(JsonSerializer.Serialize(stored.Elements), JsonSerializer.Serialize(rebuilt));
        Assert.True(rows[0].IsCoil);
        Assert.False(rows[0].ShowsDirectTag);
        Assert.False(rows[0].ShowsSeverity);
        Assert.True(rows[0].ShowsRelay);
        Assert.Contains("M01", rows[0].RelayNames);

        // The coil's input is an ordinary tag contact, and the second root reads the relay.
        Assert.True(rows[1].ShowsDirectTag);
        Assert.False(rows[1].ShowsRelay);
        Assert.True(rows[2].IsRelayContact);
        Assert.True(rows[2].ShowsRelay);
        Assert.False(rows[2].ShowsDirectTag);
    }

    [Fact]
    public void RelayContact_ACoercionBackToOnClearsTheTagAndTheValue()
    {
        LogicTagCatalog catalog = Catalog();
        var row = new LogicElementRowViewModel(
            new LogicElementDto
            {
                Id = Guid.NewGuid(),
                Kind = LogicElementKinds.Contact,
                Text = "Level above 5",
                SourceId = "sim",
                ItemId = "Level",
                Op = LogicConditionOps.GreaterThan,
                Value = 5
            },
            0,
            catalog);

        Assert.True(row.NeedsValue);

        // Typing a relay makes it a relay contact: a bit has nothing to compare against.
        row.Relay = "m01";

        Assert.Equal(LogicConditionOps.On, row.Op.Value);
        Assert.False(row.NeedsValue);
        Assert.True(row.IsRelayContact);
        Assert.False(row.ShowsDirectTag);

        LogicElementDto built = LogicEditorCodec.ElementFromRow(row);
        Assert.Equal("M01", built.Relay);
        Assert.Equal(string.Empty, built.SourceId);
        Assert.Equal(string.Empty, built.ItemId);
        Assert.Null(built.Value);
    }

    [Fact]
    public void Indent_ClampsAtTheSharedDepthLimit()
    {
        LogicTagCatalog catalog = Catalog();
        var deep = new LogicElementRowViewModel(
            new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Not },
            LogicLimits.MaxElementDepth + 5,
            catalog);
        var justBelow = new LogicElementRowViewModel(
            new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Not },
            LogicLimits.MaxElementDepth - 1,
            catalog);

        Assert.Equal(LogicLimits.MaxElementDepth, deep.Indent);
        Assert.False(deep.IndentRowCommand.CanExecute(null));
        Assert.True(deep.OutdentRowCommand.CanExecute(null));

        Assert.True(justBelow.IndentRowCommand.CanExecute(null));
        justBelow.IndentRowCommand.Execute(null);
        Assert.Equal(LogicLimits.MaxElementDepth, justBelow.Indent);
        Assert.False(justBelow.IndentRowCommand.CanExecute(null));
    }
}
