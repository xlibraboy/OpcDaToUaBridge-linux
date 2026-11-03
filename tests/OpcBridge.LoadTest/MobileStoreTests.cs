using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The phone's dataset store: one durable copy per bridge, replaced in one step on every sync,
/// with its notes and outbox. SQLite is exercised on the real file so a restart of the app is
/// what these tests simulate by reopening the database.
/// </summary>
public sealed class MobileStoreTests
{
    private static readonly DateTime SavedUtc = new(2026, 11, 3, 6, 30, 0, DateTimeKind.Utc);

    private static LogicBlockDto Block(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Group = "Primary Arm",
        Kind = LogicBlockKinds.Interlock
    };

    private static BridgeDataset Dataset(string blockName, DateTime syncedUtc) => new(
        syncedUtc,
        new List<LogicBlockDto> { Block(blockName) },
        new List<LogicAreaTagsDto>
        {
            new()
            {
                Name = "DRY END",
                Tags = new List<LogicTagDto>
                {
                    new() { Name = "START_PB", SourceId = "sim", ItemId = "Start" }
                }
            }
        },
        new List<HmiTagDto>
        {
            new()
            {
                SourceId = "sim",
                ItemId = "PS1",
                DisplayName = "Pressure switch PS1",
                Digital = true,
                OnText = "On",
                OffText = "Off",
                DataType = "Boolean"
            }
        });

    private static LogicNoteDto Note(Guid blockId, string text) => new()
    {
        Id = Guid.NewGuid(),
        BlockId = blockId,
        Text = text,
        Author = "op",
        CreatedUtc = SavedUtc
    };

    private static OutboxItem QueuedNote() => new()
    {
        Kind = OutboxKind.Note,
        BlockId = Guid.NewGuid(),
        BlockName = "Line 01 Start",
        NoteText = "Waiting on permit"
    };

    [Fact]
    public void SqliteStore_RoundTripsTheDataset()
    {
        using TempDatabase database = new();
        using SqliteMobileStore store = new(database.Path);

        Assert.Null(store.LoadDataset("b1"));

        store.SaveDataset("b1", Dataset("Line 01 interlock", SavedUtc));
        BridgeDataset? loaded = store.LoadDataset("b1");

        Assert.NotNull(loaded);
        Assert.Equal(SavedUtc, loaded!.SyncedUtc);
        Assert.Equal("Line 01 interlock", Assert.Single(loaded.Blocks).Name);
        Assert.Equal("START_PB", Assert.Single(Assert.Single(loaded.TagSources).Tags).Name);
        Assert.Equal("Pressure switch PS1", Assert.Single(loaded.Tags).DisplayName);
        Assert.Null(store.LoadDataset("another-bridge"));
    }

    [Fact]
    public void SqliteStore_ReplacesTheDatasetOnSave()
    {
        using TempDatabase database = new();
        using SqliteMobileStore store = new(database.Path);
        store.SaveDataset("b1", Dataset("Line 01 interlock", SavedUtc));

        DateTime later = SavedUtc.AddMinutes(10);
        store.SaveDataset("b1", Dataset("Line 02 interlock", later));

        BridgeDataset? loaded = store.LoadDataset("b1");
        Assert.NotNull(loaded);
        Assert.Equal(later, loaded!.SyncedUtc);
        Assert.Equal("Line 02 interlock", Assert.Single(loaded.Blocks).Name);
    }

    [Fact]
    public void SqliteStore_PersistsAcrossInstances()
    {
        using TempDatabase database = new();
        using (SqliteMobileStore first = new(database.Path))
        {
            first.SaveDataset("b1", Dataset("Line 01 interlock", SavedUtc));
            first.SaveNotes("b1", new Dictionary<Guid, List<LogicNoteDto>>
            {
                [Guid.NewGuid()] = new List<LogicNoteDto>()
            });
            first.SaveOutbox("b1", new[] { QueuedNote() });
        }

        using SqliteMobileStore second = new(database.Path);
        BridgeDataset? loaded = second.LoadDataset("b1");

        Assert.NotNull(loaded);
        Assert.Equal("Line 01 interlock", Assert.Single(loaded!.Blocks).Name);
        Assert.Equal("Waiting on permit", Assert.Single(second.LoadOutbox("b1")).NoteText);
    }

    [Fact]
    public void SqliteStore_StoresNotesPerBlockAndReplacesThem()
    {
        using TempDatabase database = new();
        using SqliteMobileStore store = new(database.Path);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        store.SaveNotes("b1", new Dictionary<Guid, List<LogicNoteDto>>
        {
            [first] = new List<LogicNoteDto> { Note(first, "Grease the arm weekly") },
            [second] = new List<LogicNoteDto> { Note(second, "Choked the line, cleared") }
        });

        IReadOnlyDictionary<Guid, List<LogicNoteDto>> loaded = store.LoadNotes("b1");
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Grease the arm weekly", Assert.Single(loaded[first]).Text);

        // A later save is the whole set for the bridge: a block with no notes disappears.
        store.SaveNotes("b1", new Dictionary<Guid, List<LogicNoteDto>>
        {
            [first] = new List<LogicNoteDto> { Note(first, "Grease the arm weekly") }
        });

        loaded = store.LoadNotes("b1");
        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey(first));
        Assert.False(loaded.ContainsKey(second));
    }

    [Fact]
    public void SqliteStore_ForgetsEverythingForABridge()
    {
        using TempDatabase database = new();
        using SqliteMobileStore store = new(database.Path);
        Guid blockId = Guid.NewGuid();
        store.SaveDataset("b1", Dataset("Line 01 interlock", SavedUtc));
        store.SaveNotes("b1", new Dictionary<Guid, List<LogicNoteDto>>
        {
            [blockId] = new List<LogicNoteDto> { Note(blockId, "note") }
        });
        store.SaveOutbox("b1", new[] { QueuedNote() });

        store.ForgetBridge("b1");

        Assert.Null(store.LoadDataset("b1"));
        Assert.Empty(store.LoadNotes("b1"));
        Assert.Empty(store.LoadOutbox("b1"));
    }

    [Fact]
    public void SqliteStore_RecoversFromACorruptFile()
    {
        using TempDatabase database = new();
        File.WriteAllText(database.Path, "this is not a sqlite database");

        using SqliteMobileStore store = new(database.Path);
        store.SaveDataset("b1", Dataset("Line 01 interlock", SavedUtc));

        BridgeDataset? loaded = store.LoadDataset("b1");
        Assert.NotNull(loaded);
        Assert.Equal("Line 01 interlock", Assert.Single(loaded!.Blocks).Name);
    }

    [Fact]
    public void MemoryStore_BehavesLikeTheDurableStore()
    {
        MemoryMobileStore store = new();
        Guid blockId = Guid.NewGuid();

        Assert.Null(store.LoadDataset("b1"));
        Assert.Empty(store.LoadNotes("b1"));
        Assert.Empty(store.LoadOutbox("b1"));

        store.SaveDataset("b1", Dataset("Line 01 interlock", SavedUtc));
        store.SaveNotes("b1", new Dictionary<Guid, List<LogicNoteDto>>
        {
            [blockId] = new List<LogicNoteDto> { Note(blockId, "note") }
        });
        store.SaveOutbox("b1", new[] { QueuedNote() });

        Assert.Equal("Line 01 interlock", Assert.Single(store.LoadDataset("b1")!.Blocks).Name);
        Assert.Equal("note", Assert.Single(store.LoadNotes("b1")[blockId]).Text);
        Assert.Equal("Waiting on permit", Assert.Single(store.LoadOutbox("b1")).NoteText);

        store.ForgetBridge("b1");
        Assert.Null(store.LoadDataset("b1"));
        Assert.Empty(store.LoadNotes("b1"));
        Assert.Empty(store.LoadOutbox("b1"));
    }

    /// <summary>A unique database file that removes itself (and SQLite's WAL siblings).</summary>
    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "opcbridge-store-" + Guid.NewGuid().ToString("N") + ".db");
        }

        public string Path { get; }

        public void Dispose()
        {
            foreach (string file in new[] { Path, Path + "-wal", Path + "-shm" })
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
