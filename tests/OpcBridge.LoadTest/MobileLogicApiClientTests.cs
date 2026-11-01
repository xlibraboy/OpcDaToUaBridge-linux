using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using OpcBridge.Client;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The mobile client against the real bridge process: definitions, evaluated state, notes and
/// session — and an unreachable bridge reported as a failed result, never an exception.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MobileLogicApiClientTests
{
    [Fact]
    public async Task Client_ReadsDefinitionsStateAndNotes()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(static appDirectory =>
        {
            foreach (string stray in new[] { "logic.json", "logic-notes.json" })
            {
                string path = Path.Combine(appDirectory, stray);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        });

        Guid blockId = Guid.NewGuid();
        LogicBlockDto block = new()
        {
            Id = blockId,
            Name = "Line 01 Start",
            Kind = LogicBlockKinds.Interlock,
            Conditions =
            {
                new LogicConditionDto
                {
                    Id = Guid.NewGuid(),
                    Text = "Line 01 start permit must be given",
                    SourceId = "sim",
                    ItemId = "Permit",
                    Op = LogicConditionOps.On
                }
            }
        };
        using HttpResponseMessage created = await app.Client.PostAsJsonAsync("/api/logic/blocks", new { block });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using LogicApiClient client = new();
        client.SetBaseAddress(app.Client.BaseAddress!.ToString());

        MobileResult<MobileLogicDefinitions> definitions = await client.GetLogicDefinitionsAsync(CancellationToken.None);
        Assert.True(definitions.Ok, definitions.Error);
        Assert.Single(definitions.Value!.Blocks);
        Assert.Equal("Line 01 Start", definitions.Value.Blocks[0].Name);
        Assert.Empty(definitions.Value.TagSources);

        // The machines' tags sources travel with the definitions: a block's named tags resolve
        // through them on the phone, including offline.
        using HttpResponseMessage source = await app.Client.PostAsJsonAsync(
            "/api/logic/tagsources",
            new
            {
                tagSource = new
                {
                    name = "DRYEND",
                    tags = new[] { new { name = "START_PB", sourceId = "sim", itemId = "Start" } }
                }
            });
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);

        definitions = await client.GetLogicDefinitionsAsync(CancellationToken.None);
        LogicAreaTagsDto machine = Assert.Single(definitions.Value!.TagSources);
        Assert.Equal("DRYEND", machine.Name);
        Assert.Equal("START_PB", machine.Tags[0].Name);
        Assert.Equal("Start", machine.Tags[0].ItemId);

        MobileResult<LogicStateSnapshot> state = await client.GetLogicStateAsync(CancellationToken.None);
        Assert.True(state.Ok, state.Error);
        Assert.Equal(LogicBlockStates.Unknown, state.Value!.Blocks[0].State);
        Assert.Equal("no data for Permit", state.Value.Blocks[0].Reason);

        MobileResult<LogicNoteDto> note = await client.AddNoteAsync(
            new LogicNoteAddRequest { BlockId = blockId, Text = "Waiting on the permit key" },
            CancellationToken.None);
        Assert.True(note.Ok, note.Error);
        Assert.Equal("Waiting on the permit key", note.Value!.Text);
        Assert.Equal("app", note.Value.Author);

        MobileResult<IReadOnlyList<LogicNoteDto>> notes = await client.GetNotesAsync(blockId, 50, CancellationToken.None);
        Assert.True(notes.Ok, notes.Error);
        Assert.Single(notes.Value!);

        MobileResult<IReadOnlyList<HmiTagDto>> tags = await client.GetTagsAsync(CancellationToken.None);
        Assert.True(tags.Ok, tags.Error);

        MobileResult<LogicApiClient.SessionResponse> session = await client.GetSessionAsync(CancellationToken.None);
        Assert.True(session.Ok, session.Error);
        Assert.False(session.Value!.AuthEnabled);
    }

    [Fact]
    public async Task Client_CarriesASignInOnABridgeWithoutEnforcedAuth()
    {
        // The viewer requires an account even on a bridge that runs with Auth:Enabled=false.
        // The bridge must keep identifying the session it issued, or the next /api/auth/me
        // would report the phone as anonymous and close the viewer's gate.
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });
        using LogicApiClient client = new();
        client.SetBaseAddress(app.Client.BaseAddress!.ToString());

        MobileResult<LogicApiClient.SessionResponse> anonymous = await client.GetSessionAsync(CancellationToken.None);
        Assert.True(anonymous.Ok, anonymous.Error);
        Assert.False(anonymous.Value!.Authenticated);
        Assert.False(anonymous.Value.AuthEnabled);

        MobileResult<LogicApiClient.SessionResponse> login = await client.LoginAsync("admin", "admin", CancellationToken.None);
        Assert.True(login.Ok, login.Error);
        Assert.True(login.Value!.Authenticated);
        Assert.False(login.Value.AuthEnabled);

        MobileResult<LogicApiClient.SessionResponse> signedIn = await client.GetSessionAsync(CancellationToken.None);
        Assert.True(signedIn.Ok, signedIn.Error);
        Assert.True(signedIn.Value!.Authenticated);
        Assert.Equal("admin", signedIn.Value.Username);
    }

    [Fact]
    public async Task Client_ReportsUnreachableBridgeAsAFailedResult()
    {
        using LogicApiClient client = new();
        client.SetBaseAddress("http://127.0.0.1:1");

        MobileResult<LogicStateSnapshot> state = await client.GetLogicStateAsync(CancellationToken.None);
        MobileResult<LogicNoteDto> note = await client.AddNoteAsync(
            new LogicNoteAddRequest { BlockId = Guid.NewGuid(), Text = "x" },
            CancellationToken.None);
        MobileResult<HmiWriteResponse> write = await client.WriteAsync(
            new HmiWriteRequest { SourceId = "sim", ItemId = "Permit", Value = true },
            CancellationToken.None);

        Assert.False(state.Ok);
        Assert.False(note.Ok);
        Assert.False(write.Ok);
        Assert.NotNull(write.Error);
    }

    [Fact]
    public async Task Client_ReportsAClosedSocketAsAFailedResult()
    {
        // Android's handler reports "Socket closed" as WebException where the desktop wraps it
        // as HttpRequestException; either way the page must get a failed result, not a crash.
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    byte[] request = new byte[1024];
                    await client.GetStream().ReadAsync(request);
                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Close();
                }
                catch (Exception)
                {
                    // Listener stopped with the test.
                }
            });

            using LogicApiClient client = new();
            client.SetBaseAddress($"http://127.0.0.1:{port}");

            MobileResult<LogicStateSnapshot> state = await client.GetLogicStateAsync(CancellationToken.None);
            MobileResult<HmiWriteResponse> write = await client.WriteAsync(
                new HmiWriteRequest { SourceId = "sim", ItemId = "Permit", Value = true },
                CancellationToken.None);

            Assert.False(state.Ok);
            Assert.False(write.Ok);
            Assert.NotNull(write.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Client_KeepsTheSignInGateInTheWrappedResults()
    {
        // The bridge answers a sessionless read with 401 {"error"}: the wrappers must carry the
        // status through, or the phone reports a bridge that answered as one that never did.
        int port = OpcBridge.Core.PortHelper.FindAvailablePort(19000, 19099);
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext context = await listener.GetContextAsync();
                        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Sign in required.\"}");
                        context.Response.StatusCode = 401;
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(body);
                        context.Response.Close();
                    }
                }
                catch (Exception)
                {
                    // Listener stopped with the test.
                }
            });

            using LogicApiClient client = new();
            client.SetBaseAddress($"http://127.0.0.1:{port}");

            MobileResult<MobileLogicDefinitions> blocks = await client.GetLogicDefinitionsAsync(CancellationToken.None);
            MobileResult<IReadOnlyList<HmiTagDto>> tags = await client.GetTagsAsync(CancellationToken.None);
            MobileResult<IReadOnlyList<LogicNoteDto>> notes = await client.GetNotesAsync(null, 50, CancellationToken.None);

            Assert.False(blocks.Ok);
            Assert.True(blocks.IsSignInRequired);
            Assert.Equal(401, blocks.StatusCode);
            Assert.Equal("Sign in required.", blocks.Error);
            Assert.True(tags.IsSignInRequired);
            Assert.True(notes.IsSignInRequired);
        }
        finally
        {
            listener.Stop();
        }
    }
}
