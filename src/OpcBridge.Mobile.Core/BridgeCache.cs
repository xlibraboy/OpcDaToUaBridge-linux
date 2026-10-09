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
}

/// <summary>The last successful refresh of one bridge: definitions, evaluated state and values.</summary>
public sealed class BridgeCacheSnapshot
{
    /// <summary>When the bridge served this — what the offline banner reports.</summary>
    public DateTime SavedUtc { get; set; }

    public List<LogicBlockDto> Blocks { get; set; } = new();

    public LogicStateSnapshot? State { get; set; }

    public List<HmiTagDto> Tags { get; set; } = new();
}

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
}
