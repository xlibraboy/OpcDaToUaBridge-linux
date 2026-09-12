using OpcBridge.Client;

namespace OpcBridge.Hmi.Core;

public sealed class MultiBridgeTagEntry
{
    public required TagBindingKey Key { get; init; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Tag description for the trend pen table (e.g. "Level of Tank 1").</summary>
    public string Description { get; set; } = string.Empty;
    public string DataType { get; set; } = "Double";
    public object? Value { get; set; }
    public DateTime? TimestampUtc { get; set; }
    public int? DaQuality { get; set; }
    public bool? IsGood { get; set; }
    public bool Writeable { get; set; }
    public string? Unit { get; set; }

    /// <summary>
    /// How this tag's history renders in the HMI trend charts: "Continuous" (line, default)
    /// or "Step" (sample-and-hold). Set per-tag in the dashboard Maps faceplate.
    /// </summary>
    public string TrendStyle { get; set; } = "Continuous";

    /// <summary>
    /// Whether this tag's values are written to InfluxDB. Only tags with history enabled
    /// can display trends — the HMI disables trend actions for the rest.
    /// </summary>
    public bool InfluxEnabled { get; set; }

    /// <summary>
    /// True when the faceplate renders this tag as a two-state on/off signal (Boolean tags
    /// automatically, others only when explicitly marked in the dashboard).
    /// </summary>
    public bool Digital { get; set; }

    /// <summary>Faceplate label for the on state. null = render the raw value.</summary>
    public string? OnText { get; set; }

    /// <summary>Faceplate label for the off state. null = render the raw value.</summary>
    public string? OffText { get; set; }
}

/// <summary>
/// Live tag cache keyed by (bridgeId, sourceId, daItemId).
/// </summary>
public sealed class MultiBridgeTagCache
{
    private readonly Dictionary<TagBindingKey, MultiBridgeTagEntry> tags_ = new(TagBindingKeyComparer.Instance);
    private readonly object sync_ = new();

    public IReadOnlyCollection<MultiBridgeTagEntry> Tags
    {
        get
        {
            lock (sync_)
            {
                return tags_.Values.ToArray();
            }
        }
    }

    public bool TryGet(TagBindingKey key, out MultiBridgeTagEntry? entry)
    {
        lock (sync_)
        {
            if (tags_.TryGetValue(key, out MultiBridgeTagEntry? found))
            {
                entry = found;
                return true;
            }

            entry = null;
            return false;
        }
    }

    public void ReplaceBridge(string bridgeId, IEnumerable<HmiTagDto> tags)
    {
        string id = (bridgeId ?? string.Empty).Trim();
        lock (sync_)
        {
            List<TagBindingKey> remove = tags_.Keys
                .Where(k => string.Equals(k.BridgeId, id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (TagBindingKey key in remove)
            {
                tags_.Remove(key);
            }

            foreach (HmiTagDto dto in tags)
            {
                TagBindingKey key = TagBindingKey.Create(id, dto.SourceId, dto.ItemId);
                tags_[key] = FromDto(key, dto);
            }
        }
    }

    public void ApplyDeltas(string bridgeId, IEnumerable<HmiValueDelta> deltas)
    {
        string id = (bridgeId ?? string.Empty).Trim();
        lock (sync_)
        {
            foreach (HmiValueDelta delta in deltas)
            {
                TagBindingKey key = TagBindingKey.Create(id, delta.SourceId, delta.ItemId);
                if (!tags_.TryGetValue(key, out MultiBridgeTagEntry? entry))
                {
                    continue;
                }

                entry.Value = delta.Value;
                entry.TimestampUtc = delta.TimestampUtc;
                entry.DaQuality = delta.DaQuality;
                entry.IsGood = delta.IsGood;
            }
        }
    }

    public void Clear()
    {
        lock (sync_)
        {
            tags_.Clear();
        }
    }

    public void ClearBridge(string bridgeId)
    {
        string id = (bridgeId ?? string.Empty).Trim();
        lock (sync_)
        {
            List<TagBindingKey> remove = tags_.Keys
                .Where(k => string.Equals(k.BridgeId, id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (TagBindingKey key in remove)
            {
                tags_.Remove(key);
            }
        }
    }

    private static MultiBridgeTagEntry FromDto(TagBindingKey key, HmiTagDto dto) => new()
    {
        Key = key,
        DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.ItemId : dto.DisplayName,
        Description = dto.Description ?? string.Empty,
        DataType = dto.DataType,
        Value = dto.Value,
        TimestampUtc = dto.TimestampUtc,
        DaQuality = dto.DaQuality,
        IsGood = dto.IsGood,
        Writeable = dto.Writeable,
        Unit = string.IsNullOrWhiteSpace(dto.Unit) ? null : dto.Unit,
        TrendStyle = NormalizeTrendStyle(dto.TrendStyle),
        InfluxEnabled = dto.InfluxEnabled,
        Digital = dto.Digital,
        OnText = string.IsNullOrWhiteSpace(dto.OnText) ? null : dto.OnText,
        OffText = string.IsNullOrWhiteSpace(dto.OffText) ? null : dto.OffText
    };

    private static string NormalizeTrendStyle(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value.Trim(), "Step", StringComparison.OrdinalIgnoreCase)
            ? "Step"
            : "Continuous";
    }
}
