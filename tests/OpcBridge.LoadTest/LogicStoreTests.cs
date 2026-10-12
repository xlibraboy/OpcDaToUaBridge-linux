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

        (IReadOnlyList<LogicBlockDto> blocks, _, long snapshotVersion) = store.GetSnapshot();
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
        (IReadOnlyList<LogicBlockDto> blocks, _, _) = store.GetSnapshot();
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
        (IReadOnlyList<LogicBlockDto> blocks, _, _) = store.GetSnapshot();
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
        Assert.Equal("A block needs at least one condition or an IEC network.", error);
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
        Assert.Equal("A sequence carries its logic in its steps.", error);
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
        (IReadOnlyList<LogicBlockDto> blocks, _, _) = store.GetSnapshot();
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
    public void TrySave_NormalizesGroupsAndKeepsHoldTimers()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock(conditions: new LogicConditionDto
        {
            Id = Guid.NewGuid(),
            Text = "Hydraulic pressure must hold",
            SourceId = "sim",
            ItemId = "PS1",
            Op = LogicConditionOps.On,
            Severity = LogicConditionSeverities.Block,
            Group = "  Start permissive  ",
            HoldMs = 3000
        });
        block.Group = "  Primary Arm  ";

        bool ok = store.TrySave(block, out LogicBlockDto saved, out _, out string? error);

        Assert.True(ok, error);
        Assert.Equal("Primary Arm", saved.Group);
        Assert.Equal("Start permissive", saved.Conditions[0].Group);
        Assert.Equal(3000, saved.Conditions[0].HoldMs);
    }

    [Fact]
    public void TrySave_RejectsAnInterlockGroupLongerThan40Characters()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Group = new string('x', 41);

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("An interlock group can be at most 40 characters.", error);
    }

    [Fact]
    public void TrySave_RejectsAnOrGroupLongerThan24Characters()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions[0].Group = new string('x', 25);

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("An OR group can be at most 24 characters.", error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3_600_001)]
    public void TrySave_RejectsAHoldTimerOutOfRange(int holdMs)
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions[0].HoldMs = holdMs;

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("A hold timer must be between 0 and 3,600,000 ms.", error);
    }

    [Fact]
    public void TrySave_KeepsAnIecNetworkAndRejectsMixingItWithConditions()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions.Clear();
        block.Elements = new List<LogicElementDto>
        {
            new()
            {
                Kind = LogicElementKinds.And,
                Inputs =
                {
                    new LogicElementDto
                    {
                        Kind = LogicElementKinds.Contact,
                        Text = "Pressure switch PS1 must read 1",
                        SourceId = "sim",
                        ItemId = "PS1",
                        Op = LogicConditionOps.On
                    },
                    new LogicElementDto
                    {
                        Kind = LogicElementKinds.Ton,
                        Text = "Flow must hold",
                        PtMs = 3000,
                        Inputs =
                        {
                            new LogicElementDto
                            {
                                Kind = LogicElementKinds.Contact,
                                Text = "Flow switch FS1 must read 1",
                                SourceId = "sim",
                                ItemId = "FS1",
                                Op = LogicConditionOps.On
                            }
                        }
                    }
                }
            }
        };

        bool ok = store.TrySave(block, out LogicBlockDto saved, out _, out string? error);

        Assert.True(ok, error);
        LogicElementDto savedRoot = Assert.Single(saved.Elements);
        Assert.Equal(LogicElementKinds.And, savedRoot.Kind);
        Assert.NotEqual(Guid.Empty, savedRoot.Id);
        Assert.Equal(3000, savedRoot.Inputs[1].PtMs);
        Assert.Equal(LogicElementKinds.Contact, savedRoot.Inputs[1].Inputs[0].Kind);

        // Both forms at once is ambiguous: one source of truth.
        LogicBlockDto mixed = CreateInterlock();
        mixed.Elements = block.Elements;
        bool mixedOk = store.TrySave(mixed, out _, out _, out string? mixedError);

        Assert.False(mixedOk);
        Assert.Equal("Use either conditions or an IEC network, not both.", mixedError);
    }

    [Fact]
    public void TrySave_RejectsAnElementWithTheWrongArity()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions.Clear();
        block.Elements = new List<LogicElementDto>
        {
            new() { Kind = LogicElementKinds.Not, Inputs = { Contact(), Contact() } }
        };

        bool ok = store.TrySave(block, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("NOT takes exactly 1 input(s).", error);
    }

    [Fact]
    public void TrySave_BoundsTheNetworkAtSixteenLevelsAndTwoHundredFiftySixElements()
    {
        LogicStore store = CreateStore();

        // Sixteen levels are accepted: the deepest element sits at depth 16.
        LogicBlockDto deep = CreateInterlock();
        deep.Conditions.Clear();
        LogicElementDto node = Contact();
        for (int i = 0; i < LogicLimits.MaxElementDepth; i++)
        {
            node = new LogicElementDto { Kind = LogicElementKinds.Not, Inputs = { node } };
        }

        deep.Elements = new List<LogicElementDto> { node };
        bool deepOk = store.TrySave(deep, out LogicBlockDto savedDeep, out _, out string? deepError);

        Assert.True(deepOk, deepError);
        Assert.Equal(LogicLimits.MaxElementDepth, DepthOf(Assert.Single(savedDeep.Elements)));

        // One level more is refused.
        LogicBlockDto deeper = CreateInterlock();
        deeper.Conditions.Clear();
        deeper.Elements = new List<LogicElementDto>
        {
            new() { Kind = LogicElementKinds.Not, Inputs = { node } }
        };
        bool deeperOk = store.TrySave(deeper, out _, out _, out string? deeperError);

        Assert.False(deeperOk);
        Assert.Equal("A network can nest at most 16 levels.", deeperError);

        // The element budget follows the shared limit too.
        LogicBlockDto wide = CreateInterlock();
        wide.Conditions.Clear();
        wide.Elements = Enumerable.Range(0, LogicLimits.MaxElementsPerBlock + 1).Select(_ => (LogicElementDto)Contact()).ToList();
        bool wideOk = store.TrySave(wide, out _, out _, out string? wideError);

        Assert.False(wideOk);
        Assert.Equal("A block can hold at most 256 elements.", wideError);
    }

    private static int DepthOf(LogicElementDto element) =>
        element.Inputs.Count == 0 ? 0 : 1 + element.Inputs.Max(DepthOf);

    [Fact]
    public void TrySave_CanonicalizesRelayNamesAndClearsTheTagOnARelayContact()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions.Clear();
        block.Elements = new List<LogicElementDto>
        {
            // A contact with a relay reads that relay: the name is canonical uppercase, and the
            // tag fields and the comparison value that belong to a tag contact drop off.
            new()
            {
                Kind = LogicElementKinds.Contact,
                Text = "M01 must be on",
                Relay = " m01 ",
                SourceId = "sim",
                ItemId = "Permit",
                Op = LogicConditionOps.On,
                Value = 5
            }
        };

        bool ok = store.TrySave(block, out LogicBlockDto saved, out _, out string? error);

        Assert.True(ok, error);
        LogicElementDto contact = Assert.Single(saved.Elements);
        Assert.Equal("M01", contact.Relay);
        Assert.Equal(string.Empty, contact.SourceId);
        Assert.Equal(string.Empty, contact.ItemId);
        Assert.Null(contact.Value);
    }

    [Fact]
    public void TrySave_RejectsARelayContactWithANumericOpOrABadRelayName()
    {
        LogicStore store = CreateStore();

        LogicBlockDto numeric = CreateInterlock();
        numeric.Conditions.Clear();
        numeric.Elements = new List<LogicElementDto>
        {
            new()
            {
                Kind = LogicElementKinds.Contact,
                Text = "M01 above 5",
                Relay = "M01",
                Op = LogicConditionOps.GreaterThan,
                Value = 5
            }
        };
        Assert.False(store.TrySave(numeric, out _, out _, out string? numericError));
        Assert.Equal("A relay contact must be on or off.", numericError);

        LogicBlockDto badName = CreateInterlock();
        badName.Conditions.Clear();
        badName.Elements = new List<LogicElementDto>
        {
            new() { Kind = LogicElementKinds.Contact, Text = "Relay must be on", Relay = "0M1", Op = LogicConditionOps.On }
        };
        Assert.False(store.TrySave(badName, out _, out _, out string? nameError));
        Assert.Equal("A relay name must start with a letter (max 16 characters).", nameError);
    }

    [Fact]
    public void TrySave_CoilNeedsARelayTakesOneInputAndAlwaysBlocks()
    {
        LogicStore store = CreateStore();

        LogicBlockDto missing = CreateInterlock();
        missing.Conditions.Clear();
        missing.Elements = new List<LogicElementDto>
        {
            new() { Kind = LogicElementKinds.Coil, Inputs = { Contact() } }
        };
        Assert.False(store.TrySave(missing, out _, out _, out string? missingError));
        Assert.Equal("A coil needs a relay (e.g. M01).", missingError);

        LogicBlockDto noInput = CreateInterlock();
        noInput.Conditions.Clear();
        noInput.Elements = new List<LogicElementDto> { new() { Kind = LogicElementKinds.Coil, Relay = "M01" } };
        Assert.False(store.TrySave(noInput, out _, out _, out string? arityError));
        Assert.Equal("COIL takes exactly 1 input(s).", arityError);

        LogicBlockDto warn = CreateInterlock();
        warn.Conditions.Clear();
        warn.Elements = new List<LogicElementDto>
        {
            new()
            {
                Kind = LogicElementKinds.Coil,
                Relay = "M01",
                Severity = LogicConditionSeverities.Warn,
                Inputs = { Contact() }
            }
        };
        bool warnOk = store.TrySave(warn, out LogicBlockDto saved, out _, out string? warnError);

        Assert.True(warnOk, warnError);
        LogicElementDto coil = Assert.Single(saved.Elements);
        Assert.Equal(LogicConditionSeverities.Block, coil.Severity);
        Assert.Equal(string.Empty, coil.ItemId);
    }

    [Fact]
    public void TrySave_KeepsARelayContactBesideTagContactsInOneGate()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions.Clear();
        block.Elements = new List<LogicElementDto>
        {
            new()
            {
                Kind = LogicElementKinds.And,
                Inputs =
                {
                    new LogicElementDto
                    {
                        Kind = LogicElementKinds.Contact,
                        Text = "M01 must be on",
                        Relay = "M01",
                        Op = LogicConditionOps.On
                    },
                    new LogicElementDto
                    {
                        Kind = LogicElementKinds.Contact,
                        Text = "Permit must be given",
                        SourceId = "sim",
                        ItemId = "Permit",
                        Op = LogicConditionOps.On
                    }
                }
            }
        };

        bool ok = store.TrySave(block, out LogicBlockDto saved, out _, out string? error);

        Assert.True(ok, error);
        LogicElementDto gate = Assert.Single(saved.Elements);
        Assert.Equal("M01", gate.Inputs[0].Relay);
        Assert.Equal("Permit", gate.Inputs[1].ItemId);
    }

    [Fact]
    public void TrySave_ClearsFieldsThatDoNotBelongToTheKind()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        block.Conditions.Clear();
        block.Elements = new List<LogicElementDto>
        {
            new()
            {
                Kind = LogicElementKinds.And,
                // A gate combines its inputs: a stray tag, relay and preset are dropped, not saved.
                ItemId = "not-a-contact",
                Relay = "M01",
                Op = LogicConditionOps.On,
                PtMs = 5000,
                Pv = 7,
                Inputs = { Contact(), Contact() }
            }
        };

        bool ok = store.TrySave(block, out LogicBlockDto saved, out _, out string? error);

        Assert.True(ok, error);
        LogicElementDto gate = Assert.Single(saved.Elements);
        Assert.Equal(string.Empty, gate.ItemId);
        Assert.Equal(string.Empty, gate.Relay);
        Assert.Equal(0, gate.PtMs);
        Assert.Equal(0, gate.Pv);
    }

    private static LogicElementDto Contact() => new()
    {
        Kind = LogicElementKinds.Contact,
        Text = "Permit must be given",
        SourceId = "sim",
        ItemId = "Permit",
        Op = LogicConditionOps.On
    };

    [Fact]
    public void TryRemove_ReturnsFalseForUnknownAndTrueOtherwise()
    {
        LogicStore store = CreateStore();
        LogicBlockDto block = CreateInterlock();
        store.TrySave(block, out _, out _, out _);

        Assert.False(store.TryRemove(Guid.NewGuid(), out _));
        Assert.True(store.TryRemove(block.Id, out long version));
        Assert.Equal(2, version);
        (IReadOnlyList<LogicBlockDto> blocks, _, _) = store.GetSnapshot();
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
        (IReadOnlyList<LogicBlockDto> blocks, _, long version) = reopened.GetSnapshot();

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
        (IReadOnlyList<LogicBlockDto> blocks, _, _) = store.GetSnapshot();

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

    // ---- Relay/tag bindings ----

    [Fact]
    public void RelayBindings_AreCanonicalizedAndReloaded()
    {
        LogicStore store = CreateStore();
        bool ok = store.TrySetRelays(
            new[]
            {
                new LogicRelayDto { Name = " m01 ", SourceId = "internal", ItemId = " Line01.Start ", Write = "ALLOW" }
            },
            out IReadOnlyList<LogicRelayDto> saved,
            out long version,
            out string? error);

        Assert.True(ok, error);
        Assert.Equal(1, version);
        LogicRelayDto relay = Assert.Single(saved);
        Assert.Equal("M01", relay.Name);
        Assert.Equal("internal", relay.SourceId);
        Assert.Equal("Line01.Start", relay.ItemId);
        Assert.Equal(LogicRelayWrites.Allow, relay.Write);

        LogicStore reopened = new(Options.Create(new BridgeOptions()));
        (_, IReadOnlyList<LogicRelayDto> reloaded, _) = reopened.GetSnapshot();
        LogicRelayDto stored = Assert.Single(reloaded);
        Assert.Equal("M01", stored.Name);
        Assert.Equal("Line01.Start", stored.ItemId);
        Assert.Equal(LogicRelayWrites.Allow, stored.Write);
    }

    [Fact]
    public void RelayBindings_DropUnboundEntries_AndDefaultTheSource()
    {
        LogicStore store = CreateStore();

        Assert.True(store.TrySetRelays(
            new[]
            {
                new LogicRelayDto { Name = "M01" },
                new LogicRelayDto { Name = "M02", ItemId = "Line01.Test" }
            },
            out IReadOnlyList<LogicRelayDto> saved,
            out _,
            out string? error), error);

        LogicRelayDto relay = Assert.Single(saved);
        Assert.Equal("M02", relay.Name);
        Assert.Equal("default", relay.SourceId);
        Assert.Equal(LogicRelayWrites.Auto, relay.Write);
    }

    [Fact]
    public void RelayBindings_RefuseDuplicatesAndBadNames()
    {
        LogicStore store = CreateStore();

        Assert.False(store.TrySetRelays(
            new[]
            {
                new LogicRelayDto { Name = "M01", ItemId = "A" },
                new LogicRelayDto { Name = "m01", ItemId = "B" }
            },
            out _,
            out _,
            out string? duplicateError));
        Assert.Contains("twice", duplicateError);

        Assert.False(store.TrySetRelays(
            new[] { new LogicRelayDto { Name = "1M", ItemId = "A" } },
            out _,
            out _,
            out string? nameError));
        Assert.Contains("relay name", nameError);

        Assert.Empty(store.GetSnapshot().Relays);
    }

    [Fact]
    public void ALegacyBlockArray_LoadsAsBlocksWithoutRelays()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logic.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { CreateInterlock("Legacy block") }));

        LogicStore store = new(Options.Create(new BridgeOptions()));
        (IReadOnlyList<LogicBlockDto> blocks, IReadOnlyList<LogicRelayDto> relays, _) = store.GetSnapshot();

        Assert.Single(blocks);
        Assert.Equal("Legacy block", blocks[0].Name);
        Assert.Empty(relays);
    }

    [Fact]
    public void RelayBindings_FromDiskThatNoLongerValidate_AreDropped()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logic.json");
        File.WriteAllText(
            path,
            """
            {
              "Relays": [
                { "Name": "M01", "SourceId": "internal", "ItemId": "Line01.Start", "Write": "never" },
                { "Name": "1BAD", "SourceId": "internal", "ItemId": "Line01.Bad" }
              ],
              "Blocks": []
            }
            """);

        LogicStore store = new(Options.Create(new BridgeOptions()));
        (_, IReadOnlyList<LogicRelayDto> relays, _) = store.GetSnapshot();

        LogicRelayDto relay = Assert.Single(relays);
        Assert.Equal("M01", relay.Name);
        Assert.Equal(LogicRelayWrites.Never, relay.Write);
    }
}
