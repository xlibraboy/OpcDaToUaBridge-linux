using System.Net;
using System.Net.Sockets;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The no-login offline view: offered only when the bridge is unreachable and a synced dataset
/// is stored, read-only while it is open, and over as soon as the bridge answers again.
/// </summary>
public sealed class MobileOfflineViewerTests
{
    private static readonly DateTime SavedUtc = new(2026, 11, 3, 7, 0, 0, DateTimeKind.Utc);

    private static LogicBlockDto Block() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Line 01 Start",
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

    /// <summary>A port nothing listens on: the connection is refused at once, no timeout.</summary>
    private static int ClosedPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static BridgeConnection Connection(bool withStoredData)
    {
        MemoryMobileStore store = new();
        if (withStoredData)
        {
            store.SaveDataset(
                "b1",
                new BridgeDataset(
                    SavedUtc,
                    new List<LogicBlockDto> { Block() },
                    Array.Empty<LogicAreaTagsDto>(),
                    Array.Empty<HmiTagDto>()));
        }

        return new BridgeConnection(
            new SavedBridge("b1", "Plant 1", "http://127.0.0.1:" + ClosedPort()),
            new MultiBridgeTagCache(),
            post: null,
            store: store);
    }

    [Fact]
    public async Task OfflineWithStoredData_OffersTheViewAndGrantsAccess()
    {
        BridgeConnection connection = Connection(withStoredData: true);
        await connection.ConnectAsync(CancellationToken.None);

        // Offered, but nothing opens until the operator asks for it.
        Assert.True(connection.CanUseOffline);
        Assert.False(MobileAccess.IsGranted(connection));

        connection.EnterOfflineViewer();

        Assert.True(connection.IsOfflineViewer);
        Assert.True(MobileAccess.IsGranted(connection));

        // The bridge answers again: online access needs a session, so the grant ends.
        connection.IsOffline = false;
        Assert.False(MobileAccess.IsGranted(connection));
    }

    [Fact]
    public async Task OfflineWithoutStoredData_OffersNothing()
    {
        BridgeConnection connection = Connection(withStoredData: false);
        await connection.ConnectAsync(CancellationToken.None);

        Assert.True(connection.IsOffline);
        Assert.False(connection.CanUseOffline);

        connection.EnterOfflineViewer();

        Assert.False(connection.IsOfflineViewer);
        Assert.False(MobileAccess.IsGranted(connection));
    }

    [Fact]
    public async Task OfflineViewer_CannotQueueNotesOrActions()
    {
        BridgeConnection connection = Connection(withStoredData: true);
        await connection.ConnectAsync(CancellationToken.None);
        connection.EnterOfflineViewer();
        LogicBlockDto block = connection.Overview.Definitions[0];

        OutboxSubmitResult note = await connection.SubmitNoteAsync(block.Id, "Look at the arm", CancellationToken.None);
        OutboxSubmitResult action = await connection.SubmitActionAsync(
            block.Id,
            block.Name,
            "Open valve",
            "Valve 01",
            "sim",
            "Valve01",
            "true",
            CancellationToken.None);

        Assert.Equal(OutboxSubmitKind.Rejected, note.Kind);
        Assert.Contains("read-only", note.Error);
        Assert.Equal(OutboxSubmitKind.Rejected, action.Kind);
        Assert.Contains("read-only", action.Error);
        Assert.Empty(connection.OutboxItems);
        Assert.Equal(0, connection.PendingCount);
    }
}
