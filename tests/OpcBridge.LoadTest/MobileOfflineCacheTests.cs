using System.Net;
using System.Net.Sockets;
using OpcBridge.Client;
using OpcBridge.Core;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The phone keeps the structure a bridge served — definitions, tag metadata and notes — so
/// the floor screen still reads when the link is down, while the last readings and evaluated
/// states are never presented as current: a failed refresh clears them and the banner says
/// why, and a later success fills them back in.
/// </summary>
public sealed class MobileOfflineCacheTests
{
    private static readonly DateTime SavedUtc = new(2026, 10, 9, 5, 30, 0, DateTimeKind.Utc);

    private static LogicBlockDto Block() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Primary Arm Up",
        Group = "Primary Arm",
        Kind = LogicBlockKinds.Interlock,
        Conditions =
        {
            new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                Text = "Pressure switch PS1 must read 1",
                SourceId = "sim",
                ItemId = "PS1",
                Op = LogicConditionOps.On
            }
        }
    };

    private static BridgeCacheSnapshot Snapshot(LogicBlockDto block) => new()
    {
        SavedUtc = SavedUtc,
        Blocks = new List<LogicBlockDto> { block },
        State = new LogicStateSnapshot
        {
            Version = 3,
            Blocks =
            {
                new LogicBlockStateDto
                {
                    Id = block.Id,
                    State = LogicBlockStates.Blocked,
                    Reason = "Pressure switch PS1 must read 1",
                    Elements =
                    {
                        new LogicElementStateDto
                        {
                            Id = block.Conditions[0].Id,
                            Kind = LogicElementKinds.Contact,
                            State = LogicConditionStates.False,
                            ValueText = "Off",
                            Should = "1",
                            Matches = false
                        }
                    }
                }
            }
        },
        Tags = new List<HmiTagDto>
        {
            new() { SourceId = "sim", ItemId = "PS1", DisplayName = "Pressure switch PS1", Value = false, IsGood = true, DataType = "Boolean", Digital = true }
        }
    };

    /// <summary>A port nothing listens on: the connection is refused at once, no timeout.</summary>
    private static int ClosedPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static BridgeConnection Connection(MemoryBridgeCacheStore store, MultiBridgeTagCache tags, LogicBlockDto block)
    {
        store.Write(BridgeCacheCodec.Key("b1"), BridgeCacheCodec.Serialize(Snapshot(block)));
        return new BridgeConnection(
            new SavedBridge("b1", "Plant 1", "http://127.0.0.1:" + ClosedPort()),
            tags,
            post: null,
            cache: store);
    }

    [Fact]
    public void LoadCached_ShowsTheStructureButNeverTheLastReadings()
    {
        LogicBlockDto block = Block();
        MultiBridgeTagCache tags = new();
        BridgeConnection connection = Connection(new MemoryBridgeCacheStore(), tags, block);

        // The definitions come straight from disk, so the floor list can still be read.
        Assert.Single(connection.Overview.Definitions);
        LogicBlockCardViewModel card = Assert.Single(connection.Overview.Visible);
        Assert.Equal("Primary Arm Up", card.Name);
        Assert.Equal("Primary Arm", card.Group);

        // The cached evaluation is not shown: nothing reads as current before the bridge answers.
        Assert.Equal("no data", card.StateLabel);
        Assert.Null(card.Reason);
        Assert.Null(connection.Overview.State);

        // The tag keeps its name and settings, but its cached reading is gone.
        Assert.True(tags.TryGet(TagBindingKey.Create(connection.CacheKey, "sim", "PS1"), out MultiBridgeTagEntry? entry));
        Assert.Equal("Pressure switch PS1", entry!.DisplayName);
        Assert.Null(entry.Value);
        Assert.Equal("—", LogicTagText.Format(entry));

        // The persistent Elements screen still has the last data, explicitly separated from
        // the live view that was just cleared.
        Assert.NotNull(connection.LastKnown);
        Assert.Equal(LogicBlockStates.Blocked, connection.LastKnown!.State!.Blocks[0].State);
        Assert.False(TagDigital.CoerceBool(connection.LastKnown.Tags[0].Value));

        // Cached is not offline yet — the first call decides that.
        Assert.False(connection.IsOffline);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheStructureAndClearsTheReadings()
    {
        LogicBlockDto block = Block();
        MultiBridgeTagCache tags = new();
        BridgeConnection connection = Connection(new MemoryBridgeCacheStore(), tags, block);

        await connection.ConnectAsync(CancellationToken.None);

        Assert.False(connection.IsConnected);
        Assert.True(connection.IsOffline);
        Assert.StartsWith("offline · live values hidden · last synced ", connection.OfflineText);
        Assert.Contains("no bridge answered", connection.StatusText);

        // The block list is still readable by name and group…
        LogicBlockCardViewModel card = Assert.Single(connection.Overview.Visible);
        Assert.Equal("Primary Arm Up", card.Name);
        Assert.Equal("Primary Arm", Assert.Single(connection.Overview.VisibleGroups).Name);

        // …but no state or value survives the lost link.
        Assert.Equal("no data", card.StateLabel);
        Assert.Null(card.Reason);
        Assert.Null(connection.Overview.State);
        Assert.True(tags.TryGet(TagBindingKey.Create(connection.CacheKey, "sim", "PS1"), out MultiBridgeTagEntry? entry));
        Assert.Null(entry!.Value);
        Assert.Null(entry.IsGood);

        // The failed call captured the last-known snapshot before clearing the live view.
        Assert.NotNull(connection.LastKnown);
        Assert.Equal(LogicBlockStates.Blocked, connection.LastKnown!.State!.Blocks[0].State);
        Assert.False(TagDigital.CoerceBool(connection.LastKnown.Tags[0].Value));
        Assert.StartsWith("offline", connection.SyncText);
    }

    [Fact]
    public void CacheCodec_RoundTripsAndIgnoresJunk()
    {
        LogicBlockDto block = Block();
        BridgeCacheSnapshot snapshot = Snapshot(block);

        BridgeCacheSnapshot? copy = BridgeCacheCodec.Deserialize(BridgeCacheCodec.Serialize(snapshot));

        Assert.NotNull(copy);
        Assert.Equal(SavedUtc, copy!.SavedUtc);
        Assert.Equal("Primary Arm Up", copy.Blocks[0].Name);
        Assert.Equal(LogicBlockStates.Blocked, copy.State!.Blocks[0].State);
        Assert.Equal("1", copy.State.Blocks[0].Elements[0].Should);
        Assert.Single(copy.Tags);

        // A first run, a truncated write or an empty definition list reads as "no cache".
        Assert.Null(BridgeCacheCodec.Deserialize(null));
        Assert.Null(BridgeCacheCodec.Deserialize("  "));
        Assert.Null(BridgeCacheCodec.Deserialize("not json"));
        Assert.Null(BridgeCacheCodec.Deserialize("{\"savedUtc\":\"2026-10-09T05:30:00Z\",\"blocks\":[]}"));
    }

    [Fact]
    public void MemoryStore_BehavesAsAStore()
    {
        MemoryBridgeCacheStore store = new();

        Assert.Null(store.Read("missing"));
        store.Write("k", "v");
        Assert.Equal("v", store.Read("k"));
    }
}
