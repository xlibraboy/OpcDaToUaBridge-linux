using System.Text.Json;
using OpcBridge.Client;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// Where a bridge's last-known logic lives on the phone, so the floor list can still be read
/// when the link is down. One string per key; a store that cannot be read behaves as empty —
/// the phone is expected to be handed any state.
/// </summary>
public interface IBridgeCacheStore
{
    string? Read(string key);

    void Write(string key, string value);

    /// <summary>Forgets one key. A store that cannot delete behaves as if it already had none.</summary>
    void Delete(string key);
}

/// <summary>The last successful refresh of one bridge: definitions, evaluated state and values.</summary>
public sealed class BridgeCacheSnapshot
{
    /// <summary>When the bridge served this — what the offline banner reports.</summary>
    public DateTime SavedUtc { get; set; }

    public List<LogicBlockDto> Blocks { get; set; } = new();

    /// <summary>
    /// The areas' tags — a block's named tags resolve through them, so labels and write targets
    /// keep working offline. Missing in a cache written before they existed.
    /// </summary>
    public List<LogicAreaTagsDto> TagSources { get; set; } = new();

    public LogicStateSnapshot? State { get; set; }

    public List<HmiTagDto> Tags { get; set; } = new();

    /// <summary>Notes last served, per block — the detail screen reads them offline too.</summary>
    public Dictionary<Guid, List<LogicNoteDto>> NotesByBlock { get; set; } = new();
}

/// <summary>
/// The last readings a bridge served, kept apart from the live view. The Logic screens clear
/// themselves when the link drops; the Elements screen shows this snapshot instead, labelled
/// with <see cref="SavedUtc"/>, so the plant's element list stays readable without pretending
/// the values are current.
/// </summary>
public sealed record BridgeLastKnown(
    DateTime SavedUtc,
    IReadOnlyList<LogicBlockDto> Blocks,
    IReadOnlyList<LogicAreaTagsDto> TagSources,
    LogicStateSnapshot? State,
    IReadOnlyList<HmiTagDto> Tags);

/// <summary>The cache envelope as it is stored, and the key one bridge's cache lives under.</summary>
public static class BridgeCacheCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Key(string bridgeId) => "logic:" + bridgeId;

    public static string Serialize(BridgeCacheSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

    public static BridgeCacheSnapshot? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            BridgeCacheSnapshot? snapshot = JsonSerializer.Deserialize<BridgeCacheSnapshot>(json, JsonOptions);
            return snapshot is { Blocks.Count: > 0 } ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A cache that lives only for the process (a bridge session without a durable store, and the
/// test seam). Writes are silently kept in memory.
/// </summary>
public sealed class MemoryBridgeCacheStore : IBridgeCacheStore
{
    private readonly object sync_ = new();
    private readonly Dictionary<string, string> values_ = new(StringComparer.Ordinal);

    public string? Read(string key)
    {
        lock (sync_)
        {
            return values_.TryGetValue(key, out string? value) ? value : null;
        }
    }

    public void Write(string key, string value)
    {
        lock (sync_)
        {
            values_[key] = value;
        }
    }

    public void Delete(string key)
    {
        lock (sync_)
        {
            values_.Remove(key);
        }
    }
}
