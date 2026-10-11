using System.Net;
using System.Net.Sockets;
using System.Text;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The detail screen's notes read offline: the last notes the bridge served are kept with the
/// block cache (old cache files without them still load), a failed read leaves them untouched,
/// and a successful read replaces them without making the list itself look fresher.
/// </summary>
public sealed class MobileOfflineNotesTests
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

    private static LogicNoteDto Note(LogicBlockDto block, string text) => new()
    {
        Id = Guid.NewGuid(),
        BlockId = block.Id,
        Text = text,
        Author = "op",
        CreatedUtc = new DateTime(2026, 10, 9, 4, 0, 0, DateTimeKind.Utc)
    };

    private static BridgeCacheSnapshot Snapshot(LogicBlockDto block) => new()
    {
        SavedUtc = SavedUtc,
        Blocks = new List<LogicBlockDto> { block },
        Tags = new List<HmiTagDto>
        {
            new() { SourceId = "sim", ItemId = "PS1", DisplayName = "Pressure switch PS1", Value = false, IsGood = true, DataType = "Boolean", Digital = true }
        },
        NotesByBlock = new Dictionary<Guid, List<LogicNoteDto>>
        {
            [block.Id] = new List<LogicNoteDto> { Note(block, "Choked the line, cleared") }
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

    private static BridgeConnection Connection(MemoryBridgeCacheStore store, string url)
    {
        BridgeConnection connection = new(
            new SavedBridge("b1", "Plant 1", url),
            new MultiBridgeTagCache(),
            post: null,
            cache: store);
        connection.Api.SetBaseAddress(url);
        return connection;
    }

    [Fact]
    public void CacheCodec_RoundTripsNotes()
    {
        LogicBlockDto block = Block();

        BridgeCacheSnapshot? copy = BridgeCacheCodec.Deserialize(BridgeCacheCodec.Serialize(Snapshot(block)));

        Assert.NotNull(copy);
        Assert.True(copy!.NotesByBlock.TryGetValue(block.Id, out List<LogicNoteDto>? notes));
        Assert.Equal("Choked the line, cleared", Assert.Single(notes!).Text);
        Assert.Equal(SavedUtc, copy.SavedUtc);
    }

    [Fact]
    public void CacheFromBeforeNotesStillLoads()
    {
        LogicBlockDto block = Block();
        string legacy =
            "{\"savedUtc\":\"2026-10-09T05:30:00Z\",\"blocks\":[{\"id\":\"" + block.Id +
            "\",\"name\":\"Primary Arm Up\"}],\"tags\":[]}";

        BridgeCacheSnapshot? snapshot = BridgeCacheCodec.Deserialize(legacy);
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.NotesByBlock);
        Assert.Empty(snapshot.NotesByBlock);

        // An explicit null notes map must not take the cached list down with it either.
        MemoryBridgeCacheStore store = new();
        store.Write(BridgeCacheCodec.Key("b1"), legacy.Replace("\"tags\":[]", "\"notesByBlock\":null,\"tags\":[]"));
        BridgeConnection connection = Connection(store, "http://127.0.0.1:1");

        Assert.Single(connection.Overview.Definitions);
        Assert.Empty(connection.CachedNotes(block.Id));
    }

    [Fact]
    public void LoadCached_KeepsTheNotesForTheDetailScreen()
    {
        LogicBlockDto block = Block();
        MemoryBridgeCacheStore store = new();
        store.Write(BridgeCacheCodec.Key("b1"), BridgeCacheCodec.Serialize(Snapshot(block)));

        BridgeConnection connection = Connection(store, "http://127.0.0.1:1");

        Assert.Equal("Choked the line, cleared", Assert.Single(connection.CachedNotes(block.Id)).Text);
        Assert.Empty(connection.CachedNotes(Guid.NewGuid()));
    }

    [Fact]
    public async Task FailedNotesRead_LeavesTheCachedNotesUntouched()
    {
        LogicBlockDto block = Block();
        MemoryBridgeCacheStore store = new();
        store.Write(BridgeCacheCodec.Key("b1"), BridgeCacheCodec.Serialize(Snapshot(block)));
        string before = store.Read(BridgeCacheCodec.Key("b1"))!;
        BridgeConnection connection = Connection(store, "http://127.0.0.1:" + ClosedPort());

        MobileResult<IReadOnlyList<LogicNoteDto>> result = await connection.GetNotesAsync(block.Id, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, result.StatusCode);
        Assert.Equal("Choked the line, cleared", Assert.Single(connection.CachedNotes(block.Id)).Text);
        Assert.Equal(before, store.Read(BridgeCacheCodec.Key("b1")));
    }

    [Fact]
    public async Task SuccessfulNotesRead_CachesWithoutAgingTheList()
    {
        LogicBlockDto block = Block();
        Guid fresh1 = Guid.NewGuid();
        Guid fresh2 = Guid.NewGuid();
        string body =
            "{\"notes\":[" +
            "{\"id\":\"" + fresh1 + "\",\"blockId\":\"" + block.Id + "\",\"text\":\"Grease the arm weekly\",\"author\":\"eng\",\"createdUtc\":\"2026-10-11T01:00:00Z\"}," +
            "{\"id\":\"" + fresh2 + "\",\"blockId\":\"" + block.Id + "\",\"text\":\"Choked the line, cleared\",\"author\":\"op\",\"createdUtc\":\"2026-10-09T04:00:00Z\"}]}";
        using NotesServer server = NotesServer.Start(body);
        MemoryBridgeCacheStore store = new();
        store.Write(BridgeCacheCodec.Key("b1"), BridgeCacheCodec.Serialize(Snapshot(block)));
        BridgeConnection connection = Connection(store, server.Url);

        MobileResult<IReadOnlyList<LogicNoteDto>> result = await connection.GetNotesAsync(block.Id, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(2, connection.CachedNotes(block.Id).Count);

        // The write landed in the store and kept the list's data age (the banner's timestamp).
        BridgeCacheSnapshot? written = BridgeCacheCodec.Deserialize(store.Read(BridgeCacheCodec.Key("b1")));
        Assert.NotNull(written);
        Assert.Equal(SavedUtc, written!.SavedUtc);
        Assert.Single(written.Blocks);
        Assert.Equal(2, written.NotesByBlock[block.Id].Count);
    }

    /// <summary>A listener that answers every request with one canned notes body.</summary>
    private sealed class NotesServer : IDisposable
    {
        private readonly HttpListener listener_ = new();

        private NotesServer(string body)
        {
            int port = OpcBridge.Core.PortHelper.FindAvailablePort(19200, 19299);
            Url = "http://127.0.0.1:" + port;
            listener_.Prefixes.Add(Url + "/");
            listener_.Start();
            _ = Task.Run(async () =>
            {
                while (listener_.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener_.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public string Url { get; }

        public static NotesServer Start(string body) => new(body);

        public void Dispose() => listener_.Stop();
    }
}
