using System.Net;
using System.Net.Sockets;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The phone keeps a durable copy of what each bridge served — the blocks, the areas' tags,
/// tag metadata and notes — and the reader uses it when the link is down. The copy is structure
/// only: no reading and no evaluated state is ever presented as current, and a later sync
/// replaces the stored dataset in one step.
/// </summary>
public sealed class MobileOfflineDatasetTests
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

    private static BridgeDataset Dataset(LogicBlockDto block) => new(
        SavedUtc,
        new List<LogicBlockDto> { block },
        Array.Empty<LogicAreaTagsDto>(),
        new List<HmiTagDto>
        {
            new() { SourceId = "sim", ItemId = "PS1", DisplayName = "Pressure switch PS1", Value = false, IsGood = true, DataType = "Boolean", Digital = true }
        });

    /// <summary>A port nothing listens on: the connection is refused at once, no timeout.</summary>
    private static int ClosedPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static BridgeConnection Connection(
        MemoryMobileStore store,
        string url,
        MultiBridgeTagCache? tags = null)
    {
        BridgeConnection connection = new(
            new SavedBridge("b1", "Plant 1", url),
            tags ?? new MultiBridgeTagCache(),
            post: null,
            store: store);
        connection.Api.SetBaseAddress(url);
        return connection;
    }

    [Fact]
    public void LoadStored_ShowsTheStructureButNeverTheReadings()
    {
        LogicBlockDto block = Block();
        MultiBridgeTagCache tags = new();
        MemoryMobileStore store = new();
        store.SaveDataset("b1", Dataset(block));
        BridgeConnection connection = Connection(store, "http://127.0.0.1:1", tags);

        // The definitions come straight from the phone's dataset, so the floor list reads.
        Assert.Single(connection.Overview.Definitions);
        LogicBlockCardViewModel card = Assert.Single(connection.Overview.Visible);
        Assert.Equal("Primary Arm Up", card.Name);
        Assert.Equal("Primary Arm", card.Group);

        // No evaluation is restored, so nothing reads as current before the bridge answers.
        Assert.Equal("no data", card.StateLabel);
        Assert.Null(card.Reason);
        Assert.Null(connection.Overview.State);

        // The tag keeps its name and settings, but its stored reading is not applied.
        Assert.True(tags.TryGet(TagBindingKey.Create(connection.CacheKey, "sim", "PS1"), out MultiBridgeTagEntry? entry));
        Assert.Equal("Pressure switch PS1", entry!.DisplayName);
        Assert.Null(entry.Value);
        Assert.Equal("—", LogicTagText.Format(entry));

        // Stored is not offline yet — the first call decides that.
        Assert.False(connection.IsOffline);
        Assert.False(connection.CanUseOffline);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheStructureAndClearsTheReadings()
    {
        LogicBlockDto block = Block();
        MultiBridgeTagCache tags = new();
        MemoryMobileStore store = new();
        store.SaveDataset("b1", Dataset(block));
        BridgeConnection connection = Connection(store, "http://127.0.0.1:" + ClosedPort(), tags);

        await connection.ConnectAsync(CancellationToken.None);

        Assert.False(connection.IsConnected);
        Assert.True(connection.IsOffline);
        Assert.StartsWith("offline · structure only", connection.OfflineText);
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
        Assert.StartsWith("offline", connection.SyncText);

        // The stored structure plus the lost link is what offers the no-login offline view.
        Assert.True(connection.CanUseOffline);
    }

    [Fact]
    public async Task FailedConnect_WithoutStoredDataset_SaysNothingIsStored()
    {
        BridgeConnection connection = Connection(new MemoryMobileStore(), "http://127.0.0.1:" + ClosedPort());

        await connection.ConnectAsync(CancellationToken.None);

        Assert.True(connection.IsOffline);
        Assert.Equal("offline · no stored structure yet", connection.OfflineText);

        // Nothing is stored, so the no-login offline view has nothing to open.
        Assert.False(connection.CanUseOffline);
    }

    [Fact]
    public async Task SuccessfulSync_ReplacesTheStoredDataset()
    {
        using FakeBridge bridge = FakeBridge.Start("Line 01 interlock", "Permit", value: true);
        MemoryMobileStore store = new();
        using BridgeConnection connection = Connection(store, bridge.Url);

        await connection.ConnectAsync(CancellationToken.None);

        BridgeDataset? stored = store.LoadDataset("b1");
        Assert.NotNull(stored);
        Assert.Equal("Line 01 interlock", stored!.Blocks[0].Name);
        Assert.Single(stored.Tags);
        Assert.True(stored.SyncedUtc >= SavedUtc);

        // A later sync replaces the dataset; nothing from the previous copy lingers.
        bridge.BlockName = "Line 02 interlock";
        await connection.RefreshAsync(CancellationToken.None);

        stored = store.LoadDataset("b1");
        Assert.NotNull(stored);
        Assert.Equal("Line 02 interlock", Assert.Single(stored!.Blocks).Name);
    }

    [Fact]
    public async Task FailedSync_KeepsThePreviousDataset()
    {
        using FakeBridge bridge = FakeBridge.Start("Line 01 interlock", "Permit", value: true);
        MemoryMobileStore store = new();
        using BridgeConnection connection = Connection(store, bridge.Url);
        await connection.ConnectAsync(CancellationToken.None);

        bridge.LogicStatus = 401;
        await connection.RefreshAsync(CancellationToken.None);

        BridgeDataset? stored = store.LoadDataset("b1");
        Assert.NotNull(stored);
        Assert.Equal("Line 01 interlock", Assert.Single(stored!.Blocks).Name);
        Assert.True(connection.IsOffline);
    }
}
