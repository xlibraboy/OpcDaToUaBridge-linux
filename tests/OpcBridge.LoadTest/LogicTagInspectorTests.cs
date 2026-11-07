using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The phone's tag inspector mirrors the web Result tab's tag door: a row's tag resolves to the
/// same identity across blocks (a named area entry or a direct pair), the index answers which
/// blocks read, write and drive it, and the sheet names the source and the blocks behind it.
/// </summary>
public sealed class LogicTagInspectorTests
{
    private const string BridgeKey = "mobile:test";

    private static LogicBlockDto Block(string name, string? area = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Area = area ?? string.Empty,
        Kind = LogicBlockKinds.Interlock
    };

    private static LogicConditionDto Condition(string text, string tag) => new()
    {
        Id = Guid.NewGuid(),
        Text = text,
        Tag = tag,
        Op = LogicConditionOps.On
    };

    private static LogicElementDto Contact(string text, string? tag = null, string? sourceId = null, string? itemId = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = LogicElementKinds.Contact,
        Text = text,
        Tag = tag ?? string.Empty,
        SourceId = sourceId ?? string.Empty,
        ItemId = itemId ?? string.Empty,
        Op = LogicConditionOps.On
    };

    private static LogicAreaTagsDto DryEnd(params LogicTagDto[] tags) => new()
    {
        Name = "DRY END",
        Tags = tags.ToList()
    };

    [Fact]
    public void TagReferences_ResolveNamedDirectRelayAndCoilOutput()
    {
        LogicAreaTagsDto tags = DryEnd(
            new LogicTagDto { Name = "START_PB", SourceId = "sim", ItemId = "Start" },
            new LogicTagDto { Name = "WAITING" },
            new LogicTagDto { Name = "PUMP_RUN", SourceId = "sim", ItemId = "Pump" });
        LogicBlockDto block = Block("Read", "DRY END");

        LogicTagReference named = LogicTagReferences.ForElement(block, Contact("Permit", tag: "START_PB"), tags)!;
        Assert.True(named.Named);
        Assert.Equal("START_PB", named.Name);
        Assert.Equal("DRY END", named.Area);
        Assert.True(named.IsBound);
        Assert.StartsWith("pair|", named.Key, StringComparison.Ordinal);

        LogicTagReference direct = LogicTagReferences.ForElement(
            block,
            Contact("Run", sourceId: "sim", itemId: "Start"),
            tags)!;
        Assert.False(direct.Named);
        Assert.Equal(named.Key, direct.Key);

        LogicTagReference waiting = LogicTagReferences.ForElement(block, Contact("Waiting", tag: "WAITING"), tags)!;
        Assert.False(waiting.IsBound);
        Assert.StartsWith("name|", waiting.Key, StringComparison.Ordinal);

        Assert.Null(LogicTagReferences.ForElement(
            block,
            new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Contact, Relay = "M01" },
            tags));

        block.OutputTag = "PUMP_RUN";
        LogicTagReference coil = LogicTagReferences.ForElement(
            block,
            new LogicElementDto { Id = Guid.NewGuid(), Kind = LogicElementKinds.Coil },
            tags)!;
        Assert.True(coil.Named);
        Assert.Equal("PUMP_RUN", coil.Name);
        Assert.True(coil.IsBound);
    }

    [Fact]
    public void UsageIndex_JoinsReadersWritersAndDriversAcrossBlocks()
    {
        LogicAreaTagsDto tags = DryEnd(
            new LogicTagDto { Name = "PUMP_RUN", SourceId = "sim", ItemId = "Pump" },
            new LogicTagDto { Name = "START", SourceId = "sim", ItemId = "Start" });

        LogicBlockDto driver = Block("Drive", "DRY END");
        driver.OutputTag = "PUMP_RUN";

        LogicBlockDto reader = Block("Read", "DRY END");
        reader.Conditions.Add(Condition("Pump must be running", "PUMP_RUN"));

        LogicBlockDto writer = Block("Write", "DRY END");
        writer.Actions.Add(new LogicActionDto { Label = "Start pump", Tag = "START" });

        LogicBlockDto direct = Block("Direct");
        direct.Elements.Add(Contact("Direct run", sourceId: "sim", itemId: "Pump"));

        LogicBlockDto handshake = Block("Sequence", "DRY END");
        handshake.Kind = LogicBlockKinds.Sequence;
        handshake.Steps.Add(new LogicStepDto { Id = Guid.NewGuid(), Name = "Open valve", CompletionTag = "START" });

        LogicBlockDto[] blocks = { driver, reader, writer, direct, handshake };
        LogicTagUsageIndex index = LogicTagUsageIndex.Build(blocks, new[] { tags });

        LogicTagReference pump = LogicTagReferences.ForNamed(reader, "PUMP_RUN", tags)!;
        Assert.True(index.Drivers(pump).Single().Block.Id == driver.Id);
        IReadOnlyList<LogicTagUsage> readers = index.Readers(pump);
        Assert.Equal(new[] { "Direct", "Read" }, readers.Select(use => use.Block.Name));
        Assert.Contains("Pump must be running", readers.Single(use => use.Block.Name == "Read").Lines);
        Assert.Contains("Direct run", readers.Single(use => use.Block.Name == "Direct").Lines);

        // The driver's output is not a read of its own tag.
        Assert.DoesNotContain(readers, use => use.Block.Id == driver.Id);

        LogicTagReference start = LogicTagReferences.ForNamed(writer, "START", tags)!;
        IReadOnlyList<LogicTagUsage> writes = index.Uses(start);
        Assert.Equal(2, writes.Count);
        LogicTagUsage write = writes.Single(use => use.Block.Id == writer.Id);
        Assert.Contains("write", write.Roles);
        Assert.True(write.Reads);
        Assert.Contains("Start pump", write.Lines);
        Assert.Contains("Open valve completion", writes.Single(use => use.Block.Id == handshake.Id).Lines);
    }

    [Fact]
    public void Inspector_ShowsTheSourceAndTheBlocksBehindTheTag()
    {
        LogicAreaTagsDto tags = DryEnd(new LogicTagDto
        {
            Name = "START_PB",
            SourceId = "sim",
            ItemId = "Start",
            Contact = LogicContactForms.NormallyClosed
        });

        LogicBlockDto driver = Block("Drive", "DRY END");
        driver.OutputTag = "START_PB";

        LogicBlockDto reader = Block("Read", "DRY END");
        reader.Conditions.Add(Condition("Start permit must be given", "START_PB"));

        MultiBridgeTagCache cache = new();
        cache.ReplaceBridge(BridgeKey, new[]
        {
            new HmiTagDto
            {
                SourceId = "sim",
                SourceName = "Simulation",
                ItemId = "Start",
                DisplayName = "Dry End Start Permit",
                Digital = true,
                OnText = "On",
                OffText = "Off",
                Value = false,
                IsGood = true,
                UpdateRateMs = 1000
            }
        });

        LogicStateSnapshot snapshot = new()
        {
            Blocks =
            {
                new LogicBlockStateDto { Id = driver.Id, State = LogicBlockStates.Ready },
                new LogicBlockStateDto { Id = reader.Id, State = LogicBlockStates.Blocked, Reason = "start permit" }
            }
        };

        var vm = new LogicBlockDetailViewModel(
            reader,
            BridgeKey,
            snapshot.Blocks[1],
            cache,
            null,
            tags,
            new[] { driver, reader },
            new[] { tags });
        vm.ApplyUsageStates(snapshot);

        LogicElementRowViewModel row = Assert.Single(vm.Elements);
        Assert.True(row.HasTagReference);
        vm.OpenTagPanel(row);

        Assert.True(vm.IsTagPanelOpen);
        LogicTagInspectorViewModel inspector = vm.SelectedTag!;
        Assert.Equal("START_PB", inspector.Title);
        Assert.Equal("Area DRY END", inspector.Subtitle);
        Assert.Contains(inspector.IdentityRows, row => row.Label == "Area" && row.Value == "DRY END");
        Assert.Contains(inspector.IdentityRows, row => row.Label == "Device contact" && row.Value == "NC");
        Assert.Contains(inspector.IdentityRows, row => row.Label == "Field" && row.Value == "actuated");
        Assert.Contains(inspector.MappedRows, row => row.Label == "Source" && row.Value == "Simulation");
        Assert.Contains(inspector.MappedRows, row => row.Label == "Live" && row.Value == "Off"
            && row.RawValue == "0" && row.StateWord == "Off" && row.ShowStateWord);
        Assert.Contains(inspector.MappedRows, row => row.Label == "Quality" && row.Value == "good");
        Assert.Contains(inspector.MappedRows, row => row.Label == "Rate" && row.Value == "1000 ms");

        // The two views are one sheet: Source first, Used by on its own tab.
        Assert.True(inspector.IsSourceView);
        Assert.False(inspector.IsUsageView);
        Assert.Equal("Used by (1)", inspector.UsageTabLabel);
        inspector.ShowUsage();
        Assert.True(inspector.IsUsageView);
        inspector.ShowSource();
        Assert.True(inspector.IsSourceView);

        Assert.Single(inspector.Drivers);
        Assert.Equal("Drive", inspector.Drivers[0].Name);
        Assert.Single(inspector.Readers);
        Assert.Equal("Read", inspector.Readers[0].Name);
        Assert.Equal("BLOCKED", inspector.Readers[0].StateLabel);

        // A push repaints the cards' state without rebuilding the sheet.
        vm.ApplyUsageStates(new LogicStateSnapshot
        {
            Blocks = { new LogicBlockStateDto { Id = reader.Id, State = LogicBlockStates.Ready } }
        });
        Assert.Equal("READY", inspector.Readers[0].StateLabel);

        vm.CloseTagPanel();
        Assert.False(vm.IsTagPanelOpen);
        Assert.Null(vm.SelectedTag);
    }

    [Fact]
    public void Inspector_ExplainsAnUnassignedTag()
    {
        LogicAreaTagsDto tags = DryEnd(new LogicTagDto { Name = "START_PB" });
        LogicBlockDto block = Block("Read", "DRY END");
        block.Conditions.Add(Condition("Start permit must be given", "START_PB"));

        var vm = new LogicBlockDetailViewModel(
            block,
            BridgeKey,
            null,
            null,
            null,
            tags,
            new[] { block },
            new[] { tags });

        LogicElementRowViewModel row = Assert.Single(vm.Elements);
        vm.OpenTagPanel(row);
        LogicTagInspectorViewModel inspector = vm.SelectedTag!;

        Assert.Equal("START_PB", inspector.Title);
        Assert.Contains(inspector.MappedRows, item => item.Label == "Source" && item.Value == "not assigned");
        Assert.Contains(inspector.IdentityRows, item => item.IsWarning && item.Value.Contains("not assigned", StringComparison.Ordinal));
        Assert.Empty(inspector.Drivers);
        // The block itself reads the name, so it is the one user the inspector lists.
        Assert.Single(inspector.Readers);
        Assert.Equal("Read", inspector.Readers[0].Name);
    }

    [Fact]
    public void Coil_RowCarriesTheBlockOutputTagAndItsLink()
    {
        LogicAreaTagsDto tags = DryEnd(new LogicTagDto
        {
            Name = "PUMP_RUN",
            SourceId = "sim",
            ItemId = "Pump"
        });
        LogicElementDto coil = new()
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.Coil,
            Inputs = { Contact("Permit", tag: "PUMP_RUN") }
        };
        LogicBlockDto block = Block("Drive", "DRY END");
        block.OutputTag = "PUMP_RUN";
        block.Elements.Add(coil);

        var vm = new LogicBlockDetailViewModel(
            block,
            BridgeKey,
            null,
            null,
            null,
            tags,
            new[] { block },
            new[] { tags });

        LogicElementRowViewModel row = vm.Elements.Single(candidate => candidate.Id == coil.Id);
        Assert.True(row.HasTagReference);
        Assert.Contains("PUMP_RUN", row.TagLabel, StringComparison.Ordinal);
        Assert.Equal("PUMP_RUN", row.TagReference!.Name);
    }
}
