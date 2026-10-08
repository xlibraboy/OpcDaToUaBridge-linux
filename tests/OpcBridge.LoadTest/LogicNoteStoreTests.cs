using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Client;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(InterlinkApiAppCollection))]
public sealed class LogicNoteStoreTests
{
    private static string PersistPath => Path.Combine(AppContext.BaseDirectory, "logic-notes.json");

    private static LogicNoteStore CreateStore()
    {
        if (File.Exists(PersistPath))
        {
            File.Delete(PersistPath);
        }

        return new LogicNoteStore(Options.Create(new BridgeOptions()));
    }

    [Fact]
    public void TryAdd_RejectsMissingBlockOrBlankText()
    {
        LogicNoteStore store = CreateStore();

        Assert.False(store.TryAdd(new LogicNoteAddRequest { BlockId = Guid.Empty, Text = "hi" }, "op", out _, out string? blockError));
        Assert.Equal("Block is required.", blockError);

        Assert.False(store.TryAdd(new LogicNoteAddRequest { BlockId = Guid.NewGuid(), Text = "   " }, "op", out _, out string? textError));
        Assert.Equal("Note text is required (max 500 characters).", textError);

        Assert.False(store.TryAdd(new LogicNoteAddRequest { BlockId = Guid.NewGuid(), Text = new string('x', 501) }, "op", out _, out string? longError));
        Assert.Equal("Note text is required (max 500 characters).", longError);
    }

    [Fact]
    public void TryAdd_TrimsTextAndFallsBackToAppAuthor()
    {
        LogicNoteStore store = CreateStore();
        Guid blockId = Guid.NewGuid();

        bool ok = store.TryAdd(new LogicNoteAddRequest { BlockId = blockId, Text = "  Waiting on permit key  " }, "   ", out LogicNoteDto? note, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(note);
        Assert.Equal("Waiting on permit key", note!.Text);
        Assert.Equal("app", note.Author);
        Assert.Equal(blockId, note.BlockId);
        Assert.Equal(DateTimeKind.Utc, note.CreatedUtc.Kind);

        IReadOnlyList<LogicNoteDto> notes = store.Get(blockId, 50);
        Assert.Single(notes);
        Assert.Equal(note.Id, notes[0].Id);
    }

    [Fact]
    public void Get_FiltersByBlockAndReturnsNewestFirst()
    {
        LogicNoteStore store = CreateStore();
        Guid blockA = Guid.NewGuid();
        Guid blockB = Guid.NewGuid();

        store.TryAdd(new LogicNoteAddRequest { BlockId = blockA, Text = "old" }, "op", out LogicNoteDto? first, out _);
        Thread.Sleep(5);

        store.TryAdd(new LogicNoteAddRequest { BlockId = blockA, Text = "new" }, "op", out LogicNoteDto? second, out _);
        store.TryAdd(new LogicNoteAddRequest { BlockId = blockB, Text = "other block" }, "op", out _, out _);

        IReadOnlyList<LogicNoteDto> blockANotes = store.Get(blockA, 50);
        Assert.Equal(2, blockANotes.Count);
        Assert.Equal(first!.Id, blockANotes[1].Id);
        Assert.Equal(second!.Id, blockANotes[0].Id);

        IReadOnlyList<LogicNoteDto> blockBNotes = store.Get(blockB, 50);
        Assert.Single(blockBNotes);
        Assert.Equal("other block", blockBNotes[0].Text);

        Assert.Equal(3, store.Get(null, 50).Count);
    }

    [Fact]
    public void Persist_CapsNotesAtMaxAndReloads()
    {
        LogicNoteStore store = CreateStore();
        Guid blockId = Guid.NewGuid();

        for (int i = 0; i < LogicNoteStore.MaxNotes + 7; i++)
        {
            store.TryAdd(new LogicNoteAddRequest { BlockId = blockId, Text = $"note-{i}" }, "op", out _, out _);
        }

        List<LogicNoteDto> persisted = JsonSerializer.Deserialize<List<LogicNoteDto>>(File.ReadAllText(PersistPath))!;
        Assert.Equal(LogicNoteStore.MaxNotes, persisted.Count);
        Assert.Equal("note-7", persisted[0].Text);

        LogicNoteStore reopened = new(Options.Create(new BridgeOptions()));
        IReadOnlyList<LogicNoteDto> newest = reopened.Get(blockId, 200);
        Assert.Equal(200, newest.Count);
        Assert.Contains(newest, note => note.Text == $"note-{LogicNoteStore.MaxNotes + 6}");
        Assert.DoesNotContain(newest, note => note.Text == "note-0");
    }
}
