using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpcBridge.Client;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// The last dataset a bridge served, kept as the phone's own copy: the block definitions, the
/// areas' tags, the tag metadata its labels resolve through and the operator notes. It is
/// structure only — live readings and evaluated states are never part of it.
/// </summary>
public sealed record BridgeDataset(
    DateTime SyncedUtc,
    IReadOnlyList<LogicBlockDto> Blocks,
    IReadOnlyList<LogicAreaTagsDto> TagSources,
    IReadOnlyList<HmiTagDto> Tags);

/// <summary>
/// The phone's durable copy of what each bridge served. The first sync seeds it, every later
/// sync replaces it, and the viewer reads it at start-up and whenever the bridge is away.
/// Implemented by <see cref="SqliteMobileStore"/> on the phone and
/// <see cref="MemoryMobileStore"/> in tests.
/// </summary>
public interface IMobileStore
{
    /// <summary>The stored dataset, or null when nothing has synced yet.</summary>
    BridgeDataset? LoadDataset(string bridgeId);

    /// <summary>Replaces a bridge's dataset in one step; a failed sync keeps the previous one.</summary>
    void SaveDataset(string bridgeId, BridgeDataset dataset);

    /// <summary>Notes per block, as last served by the bridge.</summary>
    IReadOnlyDictionary<Guid, List<LogicNoteDto>> LoadNotes(string bridgeId);

    /// <summary>Replaces every note stored for the bridge.</summary>
    void SaveNotes(string bridgeId, IReadOnlyDictionary<Guid, List<LogicNoteDto>> notes);

    IReadOnlyList<OutboxItem> LoadOutbox(string bridgeId);

    void SaveOutbox(string bridgeId, IReadOnlyList<OutboxItem> items);

    /// <summary>Drops everything stored for a bridge the operator removed.</summary>
    void ForgetBridge(string bridgeId);
}

/// <summary>The JSON shapes the store keeps in its columns, shared by both implementations.</summary>
internal static class MobileStoreJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions OutboxOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Write<T>(T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(value, options);

    public static T? Read<T>(string? json, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

/// <summary>
/// The phone's dataset as a SQLite database: one row per bridge for the dataset, one per
/// block for notes, one per bridge for the outbox. A dataset save is a single transaction, so
/// a sync that fails halfway leaves the previous copy intact.
/// </summary>
public sealed class SqliteMobileStore : IMobileStore, IDisposable
{
    private const int SchemaVersion = 1;

    private readonly object sync_ = new();
    private readonly string path_;
    private SqliteConnection? connection_;

    public SqliteMobileStore(string path)
    {
        path_ = path;
        lock (sync_)
        {
            OpenLocked();
        }
    }

    public BridgeDataset? LoadDataset(string bridgeId)
    {
        lock (sync_)
        {
            if (connection_ is null)
            {
                return null;
            }

            try
            {
                using SqliteCommand command = connection_.CreateCommand();
                command.CommandText =
                    "SELECT synced_utc, blocks_json, tag_sources_json, tags_json " +
                    "FROM bridge_dataset WHERE bridge_id = $bridge;";
                command.Parameters.AddWithValue("$bridge", bridgeId);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                List<LogicBlockDto>? blocks = MobileStoreJson.Read<List<LogicBlockDto>>(
                    reader.GetString(1),
                    MobileStoreJson.Options);
                if (blocks is null)
                {
                    return null;
                }

                DateTime syncedUtc = DateTime.TryParse(
                    reader.GetString(0),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTime parsed)
                    ? parsed
                    : DateTime.MinValue;

                return new BridgeDataset(
                    syncedUtc,
                    blocks,
                    MobileStoreJson.Read<List<LogicAreaTagsDto>>(reader.GetString(2), MobileStoreJson.Options)
                        ?? new List<LogicAreaTagsDto>(),
                    MobileStoreJson.Read<List<HmiTagDto>>(reader.GetString(3), MobileStoreJson.Options)
                        ?? new List<HmiTagDto>());
            }
            catch (SqliteException)
            {
                return null;
            }
        }
    }

    public void SaveDataset(string bridgeId, BridgeDataset dataset)
    {
        lock (sync_)
        {
            if (connection_ is null)
            {
                return;
            }

            try
            {
                using SqliteTransaction transaction = connection_.BeginTransaction();
                using SqliteCommand command = connection_.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO bridge_dataset (bridge_id, synced_utc, blocks_json, tag_sources_json, tags_json) " +
                    "VALUES ($bridge, $synced, $blocks, $tagsources, $tags) " +
                    "ON CONFLICT(bridge_id) DO UPDATE SET " +
                    "synced_utc = excluded.synced_utc, blocks_json = excluded.blocks_json, " +
                    "tag_sources_json = excluded.tag_sources_json, tags_json = excluded.tags_json;";
                command.Parameters.AddWithValue("$bridge", bridgeId);
                command.Parameters.AddWithValue("$synced", dataset.SyncedUtc.ToString("O"));
                command.Parameters.AddWithValue(
                    "$blocks",
                    MobileStoreJson.Write(dataset.Blocks, MobileStoreJson.Options));
                command.Parameters.AddWithValue(
                    "$tagsources",
                    MobileStoreJson.Write(dataset.TagSources, MobileStoreJson.Options));
                command.Parameters.AddWithValue(
                    "$tags",
                    MobileStoreJson.Write(dataset.Tags, MobileStoreJson.Options));
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch (SqliteException)
            {
                // A failed write keeps the previous dataset; the next sync tries again.
            }
        }
    }

    public IReadOnlyDictionary<Guid, List<LogicNoteDto>> LoadNotes(string bridgeId)
    {
        Dictionary<Guid, List<LogicNoteDto>> notes = new();
        lock (sync_)
        {
            if (connection_ is null)
            {
                return notes;
            }

            try
            {
                using SqliteCommand command = connection_.CreateCommand();
                command.CommandText =
                    "SELECT block_id, notes_json FROM block_notes WHERE bridge_id = $bridge;";
                command.Parameters.AddWithValue("$bridge", bridgeId);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!Guid.TryParse(reader.GetString(0), out Guid blockId))
                    {
                        continue;
                    }

                    List<LogicNoteDto>? stored = MobileStoreJson.Read<List<LogicNoteDto>>(
                        reader.GetString(1),
                        MobileStoreJson.Options);
                    if (stored is not null && stored.Count > 0)
                    {
                        notes[blockId] = stored;
                    }
                }
            }
            catch (SqliteException)
            {
            }
        }

        return notes;
    }

    public void SaveNotes(string bridgeId, IReadOnlyDictionary<Guid, List<LogicNoteDto>> notes)
    {
        lock (sync_)
        {
            if (connection_ is null)
            {
                return;
            }

            try
            {
                using SqliteTransaction transaction = connection_.BeginTransaction();
                using (SqliteCommand delete = connection_.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM block_notes WHERE bridge_id = $bridge;";
                    delete.Parameters.AddWithValue("$bridge", bridgeId);
                    delete.ExecuteNonQuery();
                }

                foreach ((Guid blockId, List<LogicNoteDto> blockNotes) in notes)
                {
                    if (blockId == Guid.Empty || blockNotes.Count == 0)
                    {
                        continue;
                    }

                    using SqliteCommand insert = connection_.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO block_notes (bridge_id, block_id, notes_json) " +
                        "VALUES ($bridge, $block, $notes);";
                    insert.Parameters.AddWithValue("$bridge", bridgeId);
                    insert.Parameters.AddWithValue("$block", blockId.ToString());
                    insert.Parameters.AddWithValue(
                        "$notes",
                        MobileStoreJson.Write(blockNotes, MobileStoreJson.Options));
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (SqliteException)
            {
            }
        }
    }

    public IReadOnlyList<OutboxItem> LoadOutbox(string bridgeId)
    {
        lock (sync_)
        {
            if (connection_ is null)
            {
                return Array.Empty<OutboxItem>();
            }

            try
            {
                using SqliteCommand command = connection_.CreateCommand();
                command.CommandText = "SELECT items_json FROM bridge_outbox WHERE bridge_id = $bridge;";
                command.Parameters.AddWithValue("$bridge", bridgeId);
                object? json = command.ExecuteScalar();
                List<OutboxItem>? items = MobileStoreJson.Read<List<OutboxItem>>(
                    json as string,
                    MobileStoreJson.OutboxOptions);
                return items ?? (IReadOnlyList<OutboxItem>)Array.Empty<OutboxItem>();
            }
            catch (SqliteException)
            {
                return Array.Empty<OutboxItem>();
            }
        }
    }

    public void SaveOutbox(string bridgeId, IReadOnlyList<OutboxItem> items)
    {
        lock (sync_)
        {
            if (connection_ is null)
            {
                return;
            }

            try
            {
                using SqliteCommand command = connection_.CreateCommand();
                command.CommandText =
                    "INSERT INTO bridge_outbox (bridge_id, items_json) VALUES ($bridge, $items) " +
                    "ON CONFLICT(bridge_id) DO UPDATE SET items_json = excluded.items_json;";
                command.Parameters.AddWithValue("$bridge", bridgeId);
                command.Parameters.AddWithValue(
                    "$items",
                    MobileStoreJson.Write(items, MobileStoreJson.OutboxOptions));
                command.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
            }
        }
    }

    public void ForgetBridge(string bridgeId)
    {
        lock (sync_)
        {
            if (connection_ is null)
            {
                return;
            }

            try
            {
                using SqliteTransaction transaction = connection_.BeginTransaction();
                foreach (string table in new[] { "bridge_dataset", "block_notes", "bridge_outbox" })
                {
                    using SqliteCommand command = connection_.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {table} WHERE bridge_id = $bridge;";
                    command.Parameters.AddWithValue("$bridge", bridgeId);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (SqliteException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (sync_)
        {
            connection_?.Dispose();
            connection_ = null;
        }
    }

    private void OpenLocked()
    {
        try
        {
            OpenAndInitialize();
        }
        catch (SqliteException)
        {
            // A corrupt database must not take the app down: start a fresh one. The first
            // sync refills it, and the outbox was at worst already uncertain.
            connection_?.Dispose();
            connection_ = null;
            TryDelete();
            OpenAndInitialize();
        }
    }

    private void OpenAndInitialize()
    {
        string? directory = Path.GetDirectoryName(path_);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = path_, Pooling = false }.ToString());
        connection.Open();
        connection_ = connection;

        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 2000;";
        pragma.ExecuteNonQuery();

        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText =
            "CREATE TABLE IF NOT EXISTS bridge_dataset (" +
            "  bridge_id TEXT NOT NULL PRIMARY KEY," +
            "  synced_utc TEXT NOT NULL," +
            "  blocks_json TEXT NOT NULL," +
            "  tag_sources_json TEXT NOT NULL," +
            "  tags_json TEXT NOT NULL" +
            ");" +
            "CREATE TABLE IF NOT EXISTS block_notes (" +
            "  bridge_id TEXT NOT NULL," +
            "  block_id TEXT NOT NULL," +
            "  notes_json TEXT NOT NULL," +
            "  PRIMARY KEY (bridge_id, block_id)" +
            ");" +
            "CREATE TABLE IF NOT EXISTS bridge_outbox (" +
            "  bridge_id TEXT NOT NULL PRIMARY KEY," +
            "  items_json TEXT NOT NULL" +
            ");" +
            "PRAGMA user_version = " + SchemaVersion + ";";
        schema.ExecuteNonQuery();
    }

    private void TryDelete()
    {
        // The WAL and shared-memory files belong to the database that is being replaced; a
        // reset that leaves them behind can confuse the next open.
        foreach (string file in new[] { path_, path_ + "-wal", path_ + "-shm" })
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// The in-process store used by tests and by a connection built without a durable store. It
/// keeps the same JSON shapes as <see cref="SqliteMobileStore"/>, so a test observes the same
/// replacement and serialization behaviour as the phone.
/// </summary>
public sealed class MemoryMobileStore : IMobileStore
{
    private readonly object sync_ = new();
    private readonly Dictionary<string, DatasetRow> datasets_ = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> notes_ = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> outboxes_ = new(StringComparer.Ordinal);

    public BridgeDataset? LoadDataset(string bridgeId)
    {
        lock (sync_)
        {
            if (!datasets_.TryGetValue(bridgeId, out DatasetRow? row))
            {
                return null;
            }

            List<LogicBlockDto>? blocks = MobileStoreJson.Read<List<LogicBlockDto>>(
                row.Blocks,
                MobileStoreJson.Options);
            if (blocks is null)
            {
                return null;
            }

            return new BridgeDataset(
                row.SyncedUtc,
                blocks,
                MobileStoreJson.Read<List<LogicAreaTagsDto>>(row.TagSources, MobileStoreJson.Options)
                    ?? new List<LogicAreaTagsDto>(),
                MobileStoreJson.Read<List<HmiTagDto>>(row.Tags, MobileStoreJson.Options)
                    ?? new List<HmiTagDto>());
        }
    }

    public void SaveDataset(string bridgeId, BridgeDataset dataset)
    {
        lock (sync_)
        {
            datasets_[bridgeId] = new DatasetRow(
                dataset.SyncedUtc,
                MobileStoreJson.Write(dataset.Blocks, MobileStoreJson.Options),
                MobileStoreJson.Write(dataset.TagSources, MobileStoreJson.Options),
                MobileStoreJson.Write(dataset.Tags, MobileStoreJson.Options));
        }
    }

    public IReadOnlyDictionary<Guid, List<LogicNoteDto>> LoadNotes(string bridgeId)
    {
        lock (sync_)
        {
            List<LogicNoteDto>? all = MobileStoreJson.Read<List<LogicNoteDto>>(
                notes_.TryGetValue(bridgeId, out string? json) ? json : null,
                MobileStoreJson.Options);
            return (all ?? new List<LogicNoteDto>())
                .Where(note => note.BlockId != Guid.Empty)
                .GroupBy(note => note.BlockId)
                .ToDictionary(group => group.Key, group => group.ToList());
        }
    }

    public void SaveNotes(string bridgeId, IReadOnlyDictionary<Guid, List<LogicNoteDto>> notes)
    {
        lock (sync_)
        {
            List<LogicNoteDto> all = notes
                .Where(pair => pair.Key != Guid.Empty)
                .SelectMany(pair => pair.Value)
                .ToList();
            notes_[bridgeId] = MobileStoreJson.Write(all, MobileStoreJson.Options);
        }
    }

    public IReadOnlyList<OutboxItem> LoadOutbox(string bridgeId)
    {
        lock (sync_)
        {
            List<OutboxItem>? items = MobileStoreJson.Read<List<OutboxItem>>(
                outboxes_.TryGetValue(bridgeId, out string? json) ? json : null,
                MobileStoreJson.OutboxOptions);
            return items ?? (IReadOnlyList<OutboxItem>)Array.Empty<OutboxItem>();
        }
    }

    public void SaveOutbox(string bridgeId, IReadOnlyList<OutboxItem> items)
    {
        lock (sync_)
        {
            outboxes_[bridgeId] = MobileStoreJson.Write(items, MobileStoreJson.OutboxOptions);
        }
    }

    public void ForgetBridge(string bridgeId)
    {
        lock (sync_)
        {
            datasets_.Remove(bridgeId);
            notes_.Remove(bridgeId);
            outboxes_.Remove(bridgeId);
        }
    }

    private sealed record DatasetRow(DateTime SyncedUtc, string Blocks, string TagSources, string Tags);
}
