using System.Net;
using System.Net.Sockets;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The phone keeps the last list a bridge served, so the floor screen still reads when the
/// link is down: the cache is shown on start, a failed refresh leaves it on screen and names
/// how old it is, and a later success clears the offline mark.
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
    public void LoadCached_ShowsTheLastSnapshotBeforeAnyCall()
    {
        LogicBlockDto block = Block();
        MultiBridgeTagCache tags = new();
        BridgeConnection connection = Connection(new MemoryBridgeCacheStore(), tags, block);

        // Definitions, state and the live values come straight from disk.
        Assert.Single(connection.Overview.Definitions);
        LogicBlockCardViewModel card = Assert.Single(connection.Overview.Visible);
        Assert.Equal("Primary Arm Up", card.Name);
        Assert.Equal("Primary Arm", card.Group);
        Assert.Equal("Pressure switch PS1 must read 1", card.Reason);
        Assert.True(tags.TryGet(TagBindingKey.Create(connection.CacheKey, "sim", "PS1"), out MultiBridgeTagEntry? entry));
        Assert.Equal("Off", LogicTagText.Format(entry));

        // Cached is not offline yet — the first call decides that.
        Assert.False(connection.IsOffline);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheListAndMarksItOffline()
    {
        LogicBlockDto block = Block();
        BridgeConnection connection = Connection(new MemoryBridgeCacheStore(), new MultiBridgeTagCache(), block);

        await connection.ConnectAsync(CancellationToken.None);

        Assert.False(connection.IsConnected);
        Assert.True(connection.IsOffline);
        Assert.StartsWith("offline · showing Plant 1 as of ", connection.OfflineText);
        Assert.Contains("no bridge answered", connection.StatusText);

        // The list is still the last data the bridge served.
        Assert.Equal("Primary Arm Up", Assert.Single(connection.Overview.Visible).Name);
        Assert.Equal("Primary Arm", Assert.Single(connection.Overview.VisibleGroups).Name);
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
