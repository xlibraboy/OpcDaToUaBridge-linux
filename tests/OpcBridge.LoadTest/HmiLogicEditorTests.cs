using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.ViewModels.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic page's editor model, without a bridge: the collect rules that mirror the Logic web
/// app (sequence → steps only; element rows → the network only; otherwise the flat conditions),
/// the indentation tree, the dirty comparison and the console text the rows read back.
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
            stored.Description,
            string.Join(", ", stored.Tags),
            LogicEditorCodec.LoadConditions(stored.Conditions, catalog, null),
            LogicEditorCodec.LoadElements(stored.Elements, catalog, null),
            LogicEditorCodec.LoadSteps(stored.Steps, catalog, null, null, null),
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
            stored, stored.Order, stored.Name, stored.Kind, stored.Enabled, stored.Group,
            stored.Description, string.Join(", ", stored.Tags), conditions, new List<LogicElementRowViewModel>(),
            new List<LogicStepRowViewModel>(), LogicEditorCodec.LoadActions(stored.Actions, catalog, null));

        Assert.True(LogicEditorCodec.Matches(built, stored));
        conditions[0].Text = "Line 01 start permit must be given (edited)";
        Assert.False(LogicEditorCodec.Matches(
            LogicEditorCodec.Build(
                stored, stored.Order, stored.Name, stored.Kind, stored.Enabled, stored.Group,
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
                catalog,
                null,
                null,
                null),
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
        var step = new LogicStepRowViewModel(
            new LogicStepDto { Id = Guid.NewGuid(), Name = "Step 1" },
            catalog);
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
}
