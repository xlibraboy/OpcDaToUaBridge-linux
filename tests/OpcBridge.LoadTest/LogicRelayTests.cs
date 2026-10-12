using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Client;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The relay/tag binding rules: which M a tag belongs to, whether the tag may be written (a
/// driven M belongs to the logic, a free one to the operator), and how a coil's value reaches
/// the tag — the value cache, and the device write-out for a device-backed tag, once per change.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class LogicRelayTests
{
    private static LogicRelayDto Binding(
        string name = "M01",
        string sourceId = LogicRelays.InternalSourceId,
        string itemId = "M01",
        string write = LogicRelayWrites.Auto) => new()
    {
        Name = name,
        SourceId = sourceId,
        ItemId = itemId,
        Write = write
    };

    private static LogicBlockDto BlockWithCoil(string relay, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Line 01 Start " + relay,
        Kind = LogicBlockKinds.Interlock,
        Enabled = enabled,
        Elements =
        {
            new LogicElementDto
            {
                Id = Guid.NewGuid(),
                Kind = LogicElementKinds.Coil,
                Relay = relay,
                Inputs =
                {
                    new LogicElementDto
                    {
                        Id = Guid.NewGuid(),
                        Kind = LogicElementKinds.Contact,
                        Text = "permit",
                        SourceId = "sim",
                        ItemId = "Permit",
                        Op = LogicConditionOps.On
                    }
                }
            }
        }
    };

    private static MappingStore CreateMappingStore()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "mappings.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return new MappingStore(Options.Create(new BridgeOptions()));
    }

    private static void AddTag(MappingStore store, string sourceId, string itemId, string mode = TagMode.Internal, bool writeable = true)
    {
        store.Add(new[]
        {
            new TagMapping
            {
                SourceId = sourceId,
                ItemId = itemId,
                DisplayName = itemId,
                DataType = "Boolean",
                Enabled = true,
                Mode = mode,
                AccessRights = writeable ? TagAccessRights.ReadWrite : TagAccessRights.Read
            }
        });
    }

    private sealed class RecordingSink : ILogicRelayWriteSink
    {
        private readonly List<(string SourceId, string ItemId, object? Value)> writes_ = new();

        public IReadOnlyList<(string SourceId, string ItemId, object? Value)> Writes
        {
            get
            {
                lock (writes_)
                {
                    return writes_.ToArray();
                }
            }
        }

        /// <summary>Waits for the nth write-out; the publisher sends it off the pass thread.</summary>
        public async Task WaitForAsync(int count)
        {
            for (int i = 0; i < 250 && Writes.Count < count; i++)
            {
                await Task.Delay(20);
            }
        }

        public Task<bool> WriteRelayValueAsync(string sourceId, string itemId, object? value, CancellationToken ct)
        {
            lock (writes_)
            {
                writes_.Add((sourceId, itemId, value));
            }

            return Task.FromResult(true);
        }
    }

    [Fact]
    public void ADrivenRelay_IsReadOnly_AndAFreeRelayIsWritable()
    {
        LogicRelayDto driven = Binding("M01", itemId: "Line01.Start");
        LogicRelayDto free = Binding("M02", itemId: "Line01.Test");

        LogicRelayRules rules = LogicRelayRules.Build(
            new[] { driven, free },
            new[] { BlockWithCoil("M01") });

        Assert.True(rules.HasBindings);
        Assert.True(rules.TryGetByTag(LogicRelays.InternalSourceId, "Line01.Start", out LogicRelayDto found));
        Assert.Equal("M01", found.Name);
        Assert.False(rules.IsWritable(driven));
        Assert.True(rules.IsWritable(free));
        Assert.False(rules.TryGetByTag(LogicRelays.InternalSourceId, "Line01.Permit", out _));
    }

    [Fact]
    public void ADisabledBlock_LeavesItsRelayFree()
    {
        LogicRelayDto binding = Binding();

        LogicRelayRules rules = LogicRelayRules.Build(
            new[] { binding },
            new[] { BlockWithCoil("M01", enabled: false) });

        Assert.True(rules.IsWritable(binding));
    }

    [Fact]
    public void ASequenceStepsCoil_DrivesItsRelay()
    {
        LogicRelayDto binding = Binding();
        LogicBlockDto block = new()
        {
            Id = Guid.NewGuid(),
            Name = "Line 01 sequence",
            Kind = LogicBlockKinds.Sequence,
            Steps =
            {
                new LogicStepDto
                {
                    Id = Guid.NewGuid(),
                    Name = "Step 1",
                    Elements = { BlockWithCoil("M01").Elements[0] }
                }
            }
        };

        LogicRelayRules rules = LogicRelayRules.Build(new[] { binding }, new[] { block });

        Assert.False(rules.IsWritable(binding));
    }

    [Fact]
    public void TheWritePolicy_OverridesTheDrivenRule()
    {
        LogicRelayDto always = Binding("M01", itemId: "Always", write: LogicRelayWrites.Allow);
        LogicRelayDto never = Binding("M02", itemId: "Never", write: LogicRelayWrites.Never);

        LogicRelayRules rules = LogicRelayRules.Build(
            new[] { always, never },
            new[] { BlockWithCoil("M01"), BlockWithCoil("M02") });

        Assert.True(rules.IsWritable(always));
        Assert.False(rules.IsWritable(never));

        LogicRelayRules free = LogicRelayRules.Build(new[] { never }, Array.Empty<LogicBlockDto>());
        Assert.False(free.IsWritable(never));
    }

    [Fact]
    public void Publishing_PutsTheCoilsValueOnTheBoundTag()
    {
        MappingStore mappings = CreateMappingStore();
        AddTag(mappings, LogicRelays.InternalSourceId, "Line01.Start");
        BridgeState state = new(Options.Create(new BridgeOptions()));
        LogicRelayPublisher publisher = new(mappings, state);

        publisher.Publish(Binding("M01", itemId: "Line01.Start"), true);

        Assert.True(state.TryGetSnapshot(LogicRelays.InternalSourceId, "Line01.Start", out BridgeValueSnapshot snapshot));
        Assert.True(snapshot.IsGood);
        Assert.Equal(true, snapshot.Value);
    }

    [Fact]
    public async Task Publishing_RepeatsNothing_UntilTheValueChanges()
    {
        MappingStore mappings = CreateMappingStore();
        AddTag(mappings, "sim", "M01", mode: TagMode.Source);
        BridgeState state = new(Options.Create(new BridgeOptions()));
        RecordingSink sink = new();
        LogicRelayPublisher publisher = new(mappings, state, uaServer: null, writeSink: sink);

        publisher.Publish(Binding("M01", sourceId: "sim", itemId: "M01"), true);
        await sink.WaitForAsync(1);
        publisher.Publish(Binding("M01", sourceId: "sim", itemId: "M01"), true);

        Assert.Single(sink.Writes);

        publisher.Publish(Binding("M01", sourceId: "sim", itemId: "M01"), false);
        await sink.WaitForAsync(2);
        Assert.Equal(2, sink.Writes.Count);
    }

    [Fact]
    public async Task ADeviceTag_GetsTheValueWrittenOut_OnChangeOnly()
    {
        MappingStore mappings = CreateMappingStore();
        AddTag(mappings, "sim", "ns=2;s=Status/Line01.M01", mode: TagMode.Source);
        BridgeState state = new(Options.Create(new BridgeOptions()));
        RecordingSink sink = new();
        LogicRelayPublisher publisher = new(mappings, state, uaServer: null, writeSink: sink);

        publisher.Publish(Binding("M01", sourceId: "sim", itemId: "ns=2;s=Status/Line01.M01"), true);
        await sink.WaitForAsync(1);

        Assert.Single(sink.Writes);
        Assert.Equal(("sim", "ns=2;s=Status/Line01.M01"), (sink.Writes[0].SourceId, sink.Writes[0].ItemId));
        Assert.Equal(true, sink.Writes[0].Value);
    }

    [Fact]
    public async Task AnInternalTag_IsOnlyShown_NeverWrittenOut()
    {
        MappingStore mappings = CreateMappingStore();
        AddTag(mappings, LogicRelays.InternalSourceId, "Line01.Start");
        BridgeState state = new(Options.Create(new BridgeOptions()));
        RecordingSink sink = new();
        LogicRelayPublisher publisher = new(mappings, state, uaServer: null, writeSink: sink);

        publisher.Publish(Binding("M01", itemId: "Line01.Start"), true);
        await Task.Delay(50);

        Assert.Empty(sink.Writes);
        Assert.True(state.TryGetSnapshot(LogicRelays.InternalSourceId, "Line01.Start", out BridgeValueSnapshot snapshot));
        Assert.Equal(true, snapshot.Value);
    }

    [Fact]
    public void PublishingToATagThatDoesNotExist_ChangesNothing()
    {
        MappingStore mappings = CreateMappingStore();
        BridgeState state = new(Options.Create(new BridgeOptions()));
        LogicRelayPublisher publisher = new(mappings, state);

        publisher.Publish(Binding("M01", itemId: "NotMapped"), true);

        Assert.False(state.TryGetSnapshot(LogicRelays.InternalSourceId, "NotMapped", out _));
    }

    [Fact]
    public void AReadOnlyDeviceTag_IsShownButNotWrittenOut()
    {
        MappingStore mappings = CreateMappingStore();
        AddTag(mappings, "sim", "ReadOnly", mode: TagMode.Source, writeable: false);
        BridgeState state = new(Options.Create(new BridgeOptions()));
        RecordingSink sink = new();
        LogicRelayPublisher publisher = new(mappings, state, uaServer: null, writeSink: sink);

        publisher.Publish(Binding("M01", sourceId: "sim", itemId: "ReadOnly"), true);

        Assert.Empty(sink.Writes);
        Assert.True(state.TryGetSnapshot("sim", "ReadOnly", out BridgeValueSnapshot snapshot));
        Assert.Equal(true, snapshot.Value);
    }
}
