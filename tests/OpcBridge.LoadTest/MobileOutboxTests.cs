using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The sync outbox is durable and conservative: it survives a restart, an item caught mid-send
/// becomes "not confirmed" and is never replayed on its own, and the queue is bounded.
/// </summary>
public sealed class MobileOutboxTests
{
    private static OutboxItem Note(string text) => new()
    {
        Kind = OutboxKind.Note,
        BlockId = Guid.NewGuid(),
        BlockName = "Line 01 Start",
        NoteText = text
    };

    private static OutboxItem Write() => new()
    {
        Kind = OutboxKind.Write,
        BlockId = Guid.NewGuid(),
        BlockName = "Line 01 Start",
        ActionLabel = "Open valve",
        TagLabel = "Valve01",
        SourceId = "sim",
        ItemId = "Valve01",
        ValueText = "true"
    };

    [Fact]
    public void NotesAndWrites_RoundTripThroughTheStore()
    {
        MemoryMobileStore store = new();
        MobileOutbox outbox = new(store, "b1");
        OutboxItem note = Note("Waiting on permit");
        OutboxItem write = Write();

        Assert.True(outbox.TryAdd(note, out string? noteError), noteError);
        Assert.True(outbox.TryAdd(write, out string? writeError), writeError);
        Assert.Equal(2, outbox.PendingCount);

        MobileOutbox reloaded = new(store, "b1");
        Assert.Equal(2, reloaded.PendingCount);
        Assert.Equal(0, reloaded.FailedCount);
        OutboxItem loadedNote = reloaded.All.Single(item => item.Kind == OutboxKind.Note);
        Assert.Equal(note.Id, loadedNote.Id);
        Assert.Equal("Waiting on permit", loadedNote.NoteText);
        Assert.Equal("Line 01 Start", loadedNote.TargetText);
        OutboxItem loadedWrite = reloaded.All.Single(item => item.Kind == OutboxKind.Write);
        Assert.Equal(write.Id, loadedWrite.Id);
        Assert.Equal("Valve01", loadedWrite.TargetText);
        Assert.Equal("true", loadedWrite.ValueText);
    }

    [Fact]
    public void AnItemCaughtSending_LoadsAsNotConfirmed()
    {
        MemoryMobileStore store = new();
        MobileOutbox outbox = new(store, "b1");
        OutboxItem note = Note("Maybe delivered");
        Assert.True(outbox.TryAdd(note, out _));

        note.State = OutboxItemState.Sending;
        outbox.Update(note);

        MobileOutbox reloaded = new(store, "b1");
        OutboxItem loaded = Assert.Single(reloaded.All);
        Assert.Equal(OutboxItemState.Uncertain, loaded.State);
        Assert.Equal(0, reloaded.PendingCount);
        Assert.Equal(1, reloaded.FailedCount);
        Assert.Contains("stopped before", loaded.Error);
    }

    [Fact]
    public void RetryAndDiscard_ChangeTheQueue()
    {
        MemoryMobileStore store = new();
        MobileOutbox outbox = new(store, "b1");
        OutboxItem note = Note("retry me");
        Assert.True(outbox.TryAdd(note, out _));
        note.State = OutboxItemState.Failed;
        note.Error = "the bridge rejected the note";
        outbox.Update(note);

        outbox.Retry(note.Id);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(0, outbox.FailedCount);
        Assert.Null(Assert.Single(outbox.All).Error);

        outbox.Remove(note.Id);
        Assert.Empty(outbox.All);
        Assert.Equal(0, outbox.PendingCount);
    }

    [Fact]
    public void QueueIsBounded()
    {
        MemoryMobileStore store = new();
        MobileOutbox outbox = new(store, "b1");
        for (int i = 0; i < MobileOutbox.MaxItems; i++)
        {
            Assert.True(outbox.TryAdd(Note("n" + i), out _));
        }

        Assert.False(outbox.TryAdd(Note("overflow"), out string? error));
        Assert.Contains("queue is full", error);
        Assert.Equal(MobileOutbox.MaxItems, outbox.PendingCount);
    }

    [Fact]
    public void OutboxValue_KeepsTheLiteralShape()
    {
        Assert.Equal(true, OutboxValue.Parse("true"));
        Assert.Equal(2.5d, OutboxValue.Parse("2.5"));
        Assert.Equal("Run", OutboxValue.Parse("Run"));
    }
}
