using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Client;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(InterlinkApiAppCollection))]
public sealed class LogicStoreTests : IDisposable
{
    // LogicStore persists to a fixed file under AppContext.BaseDirectory — which is also the
    // directory the app-backed tests copy their instance from. Clear it before each test, and
    // again after (xUnit disposes each test instance), so no leftover block can seed a test app.
    private static LogicStore CreateStore()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logic.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return new LogicStore(Options.Create(new BridgeOptions()));
    }

    public void Dispose()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logic.json");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static LogicBlockDto CreateInterlock(
        string name = "Line 01 Start",
        Guid? id = null,
        params LogicConditionDto[] conditions)
    {
        return new LogicBlockDto
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Kind = LogicBlockKinds.Interlock,
            Conditions = conditions.Length == 0
                ? new List<LogicConditionDto> { CreateCondition() }
                : conditions.ToList()
        };
    }

    private static LogicConditionDto CreateCondition(
        string text = "Line 01 start permit must be given",
        string itemId = "ns=2;s=Status/Line01.Permit",
        string op = LogicConditionOps.On,
        double? value = null,
        string severity = LogicConditionSeverities.Block)
    {
        return new LogicConditionDto
        {
            Id = Guid.NewGuid(),
            Text = text,
            SourceId = "sim",
            ItemId = itemId,
            Op = op,
            Value = value,
            Severity = severity
        };
    }

    [Fact]
    public void TrySave_AddsBlockAndRaisesChangedWithNewVersion()
    {
        LogicStore store = CreateStore();
        List<long> versions = new();
        store.Changed += versions.Add;

        bool ok = store.TrySave(CreateInterlock(), out _, out long version, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(1, version);
        Assert.Equal(new[] { 1L }, versions);

        (IReadOnlyList<LogicBlockDto> blocks, long snapshotVersion) = store.GetSnapshot();
        Assert.Single(blocks);
        Assert.Equal("Line 01 Start", blocks[0].Name);
        Assert.Equal(LogicBlockKinds.Interlock, blocks[0].Kind);
        Assert.Equal(1, snapshotVersion);
    }

    [Fact]
    public void TrySave_ReplacesBlockByIdInsteadOfDuplicating()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        store.TrySave(block, out _, out _, out _);

        block.Name = "Line 01 Start (rev B)";
        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        (IReadOnlyList<LogicBlockDto> blocks, _) = store.GetSnapshot();
        Assert.Single(blocks);
        Assert.Equal("Line 01 Start (rev B)", blocks[0].Name);
    }

    [Fact]
    public void TrySave_RejectsDuplicateNamesCaseInsensitively()
    {
        LogicStore store = CreateStore();
        store.TrySave(CreateInterlock("Line 01 Start"), out _, out _, out _);

        bool ok = store.TrySave(CreateInterlock("line 01 start"), out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Block name already exists.", error);
        (IReadOnlyList<LogicBlockDto> blocks, _) = store.GetSnapshot();
        Assert.Single(blocks);
    }

    [Theory]
    [InlineData("", "Block name is required (max 64 characters).")]
    [InlineData("   ", "Block name is required (max 64 characters).")]
    public void TrySave_RejectsBlankName(string name, string expectedError)
    {
        LogicStore store = CreateStore();

        bool ok = store.TrySave(CreateInterlock(name), out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TrySave_RejectsInvalidKind()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Kind = "ladder";

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Block kind must be interlock, permissive or sequence.", error);
    }

    [Fact]
    public void TrySave_RejectsEmptyConditionList()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions.Clear();

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("A block needs at least one condition.", error);
    }

    [Fact]
    public void TrySave_RejectsNumericConditionWithoutValue()
    {
        LogicStore store = CreateStore();

        bool ok = store.TrySave(
            CreateInterlock(conditions: CreateCondition(op: LogicConditionOps.GreaterThan, value: null)),
            out _,
            out _, out string? error);

        Assert.False(ok);
        Assert.Equal("A numeric comparison needs a value.", error);
    }

    [Fact]
    public void TrySave_RejectsSequenceWithoutSteps()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Kind = LogicBlockKinds.Sequence;

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("A sequence needs at least one step.", error);
    }

    [Fact]
    public void TrySave_RejectsDuplicateStepNames()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Kind = LogicBlockKinds.Sequence;
        block.Conditions.Clear();
        block.Steps.Add(CreateStep("Step 1"));
        block.Steps.Add(CreateStep("step 1"));

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Step names must be unique within a sequence.", error);
    }

    [Fact]
    public void TrySave_RejectsHalfConfiguredCompletionHandshake()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Kind = LogicBlockKinds.Sequence;
        block.Conditions.Clear();
        LogicStepDto step = CreateStep("Step 1");
        step.CompletionSourceId = "sim";
        step.CompletionItemId = null;
        block.Steps.Add(step);

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("A completion handshake needs both a source and a tag.", error);
    }

    [Fact]
    public void TrySave_NormalizesSourceIdAndGeneratesIds()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Id = Guid.Empty;
        block.Conditions[0].Id = Guid.Empty;
        block.Conditions[0].SourceId = "  ";

        bool ok = store.TrySave(block, out _, out _, out _);

        Assert.True(ok);
        (IReadOnlyList<LogicBlockDto> blocks, _) = store.GetSnapshot();
        Assert.NotEqual(Guid.Empty, blocks[0].Id);
        Assert.NotEqual(Guid.Empty, blocks[0].Conditions[0].Id);
        Assert.Equal(DaRuntimeSettings.DefaultSourceId, blocks[0].Conditions[0].SourceId);
    }

    [Fact]
    public void TrySave_NormalizesTagsTrimsDropsBlanksAndDeduplicates()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Tags = new List<string> { " Line 1 ", "", "safety", "line 1", "  ", "Safety" };

        bool ok = store.TrySave(block, out LogicBlockDto saved, out _, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        // First spelling wins the case-insensitive duplicate; authored order is kept.
        Assert.Equal(new[] { "Line 1", "safety" }, saved.Tags);
    }

    [Fact]
    public void TrySave_RejectsMoreThanEightTags()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Tags = Enumerable.Range(1, 9).Select(i => "Tag " + i).ToList();

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("A block can carry at most 8 tags.", error);
        Assert.Empty(store.GetSnapshot().Blocks);
    }

    [Fact]
    public void TrySave_RejectsATagLongerThan24Characters()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Tags = new List<string> { new string('x', 25) };

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("A tag can be at most 24 characters.", error);
    }

    [Fact]
    public void TryRemove_ReturnsFalseForUnknownAndTrueOtherwise()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        store.TrySave(block, out _, out _, out _);

        Assert.False(store.TryRemove(Guid.NewGuid(), out _));
        Assert.True(store.TryRemove(block.Id, out long version));
        Assert.Equal(2, version);
        (IReadOnlyList<LogicBlockDto> blocks, _) = store.GetSnapshot();
        Assert.Empty(blocks);
    }

    [Fact]
    public void Persist_ReloadsBlocksAfterRestart()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions[0].NextStepText = "Turn the permit key to Permit";
        block.Tags = new List<string> { "Line 1", "Safety" };
        store.TrySave(block, out _, out _, out _);

        LogicStore reopened = new(Options.Create(new BridgeOptions()));
        (IReadOnlyList<LogicBlockDto> blocks, long version) = reopened.GetSnapshot();

        // Version is in-memory only (mirrors InterlinkStore), but the block text must survive.
        Assert.Equal(0, version);
        Assert.Single(blocks);
        Assert.Equal(block.Id, blocks[0].Id);
        Assert.Equal("Turn the permit key to Permit", blocks[0].Conditions[0].NextStepText);
        Assert.Equal(new[] { "Line 1", "Safety" }, blocks[0].Tags);
    }

    [Fact]
    public void LoadFromDisk_DropsBlocksThatNoLongerValidate()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logic.json");
        LogicBlockDto valid = CreateInterlock("Valid block");
        LogicBlockDto invalid = CreateInterlock("Broken block");
        invalid.Conditions.Clear();
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { valid, invalid }));

        LogicStore store = new(Options.Create(new BridgeOptions()));
        (IReadOnlyList<LogicBlockDto> blocks, _) = store.GetSnapshot();

        Assert.Single(blocks);
        Assert.Equal("Valid block", blocks[0].Name);
    }

    [Fact]
    public void SetAll_ThrowsBeforePersistingWhenAnyBlockIsInvalid()
    {
        LogicStore store = CreateStore();
        LogicBlockDto invalid = CreateInterlock();
        invalid.Conditions.Clear();

        Assert.Throws<InvalidOperationException>(() => store.SetAll(new[] { CreateInterlock("Good"), invalid }));
        Assert.Empty(store.GetSnapshot().Blocks);
    }

    private static LogicStepDto CreateStep(string name)
    {
        return new LogicStepDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Conditions = { CreateCondition() }
        };
    }
}
