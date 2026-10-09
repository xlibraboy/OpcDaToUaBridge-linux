using System.Net;
using System.Net.Http.Json;
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

        MobileResult<IReadOnlyList<LogicBlockDto>> blocks = await client.GetLogicBlocksAsync(CancellationToken.None);
        Assert.True(blocks.Ok, blocks.Error);
        Assert.Single(blocks.Value!);
        Assert.Equal("Line 01 Start", blocks.Value![0].Name);

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
    public async Task Client_ReportsUnreachableBridgeAsAFailedResult()
    {
        using LogicApiClient client = new();
        client.SetBaseAddress("http://127.0.0.1:1");

        MobileResult<LogicStateSnapshot> state = await client.GetLogicStateAsync(CancellationToken.None);
        MobileResult<LogicNoteDto> note = await client.AddNoteAsync(
            new LogicNoteAddRequest { BlockId = Guid.NewGuid(), Text = "x" },
            CancellationToken.None);
        HmiWriteResponse write = await client.WriteAsync(
            new HmiWriteRequest { SourceId = "sim", ItemId = "Permit", Value = true },
            CancellationToken.None);

        Assert.False(state.Ok);
        Assert.False(note.Ok);
        Assert.False(write.Ok);
        Assert.NotNull(write.Error);
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

            MobileResult<IReadOnlyList<LogicBlockDto>> blocks = await client.GetLogicBlocksAsync(CancellationToken.None);
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
