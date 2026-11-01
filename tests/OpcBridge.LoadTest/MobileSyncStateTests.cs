using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The sync badge follows the link and the outbox together: an offline bridge with queued work
/// reads offline and names the pending count, a failed flush leaves the item pending, and the
/// captured last-known data stays available for the Elements screen.
/// </summary>
public sealed class MobileSyncStateTests
{
    private static readonly DateTime SavedUtc = new(2026, 11, 2, 1, 55, 22, DateTimeKind.Utc);

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

    private static BridgeConnection Connection(LogicBlockDto block, MemoryBridgeCacheStore store)
    {
        store.Write(
            BridgeCacheCodec.Key("b1"),
            BridgeCacheCodec.Serialize(new BridgeCacheSnapshot
            {
                SavedUtc = SavedUtc,
                Blocks = new List<LogicBlockDto> { block },
                State = new LogicStateSnapshot
                {
                    EvaluatedUtc = SavedUtc,
                    Blocks =
                    {
                        new LogicBlockStateDto
                        {
                            Id = block.Id,
                            State = LogicBlockStates.Blocked,
                            Reason = "Pressure switch PS1 must read 1"
                        }
                    }
                },
                Tags = new List<HmiTagDto>
                {
                    new()
                    {
                        SourceId = "sim",
                        ItemId = "PS1",
                        DisplayName = "Pressure switch PS1",
                        Digital = true,
                        Value = false,
                        IsGood = true,
                        TimestampUtc = SavedUtc
                    }
                }
            }));

        return new BridgeConnection(
            new SavedBridge("b1", "Plant 1", "http://127.0.0.1:1"),
            new MultiBridgeTagCache(),
            post: null,
            cache: store);
    }

    [Fact]
    public async Task OfflineWithAQueuedNote_ReadsOfflineAndPending()
    {
        LogicBlockDto block = Block();
        BridgeConnection connection = Connection(block, new MemoryBridgeCacheStore());

        await connection.ConnectAsync(CancellationToken.None);

        Assert.Equal(BridgeSyncState.Offline, connection.SyncState);
        Assert.Contains("offline", connection.SyncText);
        Assert.NotNull(connection.LastKnown);
        Assert.Equal(LogicBlockStates.Blocked, connection.LastKnown!.State!.Blocks[0].State);

        OutboxSubmitResult submitted = await connection.SubmitNoteAsync(
            block.Id,
            "Waiting on the permit key",
            CancellationToken.None);

        Assert.Equal(OutboxSubmitKind.Queued, submitted.Kind);
        Assert.Equal(1, connection.PendingCount);
        Assert.Equal(BridgeSyncState.Offline, connection.SyncState);
        Assert.Contains("1 pending", connection.SyncText);
        OutboxItem item = Assert.Single(connection.OutboxItems);
        Assert.Equal(OutboxKind.Note, item.Kind);
        Assert.Equal("Line 01 Start", item.TargetText);
    }

    [Fact]
    public async Task FailedFlush_KeepsTheItemPending_AndDiscardClearsIt()
    {
        LogicBlockDto block = Block();
        BridgeConnection connection = Connection(block, new MemoryBridgeCacheStore());
        await connection.ConnectAsync(CancellationToken.None);
        await connection.SubmitNoteAsync(block.Id, "Waiting", CancellationToken.None);
        OutboxItem item = Assert.Single(connection.OutboxItems);

        await connection.FlushOutboxAsync(CancellationToken.None);

        Assert.Equal(1, connection.PendingCount);
        Assert.Equal(0, connection.FailedCount);
        Assert.Equal(OutboxItemState.Pending, Assert.Single(connection.OutboxItems).State);

        connection.DiscardOutboxItem(item.Id);

        Assert.Equal(0, connection.PendingCount);
        Assert.Empty(connection.OutboxItems);
        Assert.Equal(BridgeSyncState.Offline, connection.SyncState);
    }
}
