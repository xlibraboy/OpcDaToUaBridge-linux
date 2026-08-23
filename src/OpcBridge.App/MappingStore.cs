using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Core;

namespace OpcBridge.App;

public sealed class MappingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private List<TagMapping> mappings_;
    private long version_;

    public event Action<long>? Changed;

    public MappingStore(IOptions<BridgeOptions> options)
    {
        persist_path_ = Path.Combine(AppContext.BaseDirectory, "mappings.json");
        mappings_ = NormalizeAll(LoadFromDisk() ?? options.Value.Mappings ?? new List<TagMapping>());
    }

    public (IReadOnlyList<TagMapping> Mappings, long Version) GetSnapshot()
    {
        lock (sync_)
        {
            return (mappings_.ToArray(), version_);
        }
    }

    public long Version
    {
        get { lock (sync_) { return version_; } }
    }

    public long Add(IEnumerable<TagMapping> tags)
    {
        long raisedVersion = 0;
        bool raise = false;
        lock (sync_)
        {
            // Build a HashSet of existing keys for O(1) duplicate lookup
            // instead of O(n) per-tag scan (O(n²) total for bulk imports).
            HashSet<(string SourceId, string ItemId)> existing = new(mappings_.Count, StringTupleComparer.Instance);
            for (int i = 0; i < mappings_.Count; i++)
            {
                existing.Add((mappings_[i].SourceId, mappings_[i].ItemId));
            }

            bool changed = false;

            foreach (TagMapping tag in tags)
            {
                TagMapping normalized = Normalize(tag);
                if (normalized.ItemId.Length == 0)
                {
                    continue;
                }

                if (!existing.Add((normalized.SourceId, normalized.ItemId)))
                {
                    continue;
                }

                mappings_.Add(normalized);
                changed = true;
            }

            if (changed)
            {
                version_++;
                Persist();
                raisedVersion = version_;
                raise = true;
            }

            if (!raise)
            {
                return version_;
            }
        }

        if (raise)
        {
            Changed?.Invoke(raisedVersion);
        }

        return raisedVersion;
    }
    public bool TryUpdate(TagMapping tag, out long version)
    {
        long raisedVersion = 0;
        bool raise = false;
        lock (sync_)
        {
            TagMapping normalized = Normalize(tag);
            int index = mappings_.FindIndex(mapping =>
                string.Equals(mapping.SourceId, normalized.SourceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(mapping.ItemId, normalized.ItemId, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                version = version_;
                return false;
            }

            mappings_[index] = normalized;
            version_++;
            Persist();
            raisedVersion = version_;
            version = raisedVersion;
            raise = true;
        }

        if (raise)
        {
            Changed?.Invoke(raisedVersion);
        }

        return true;
    }

    public long Remove(string sourceId, string itemId)
    {
        string normalizedSourceId = NormalizeSourceId(sourceId);
        string normalizedItemId = itemId?.Trim() ?? string.Empty;

        long raisedVersion = 0;
        bool raise = false;
        lock (sync_)
        {
            int removed = mappings_.RemoveAll(mapping =>
                string.Equals(mapping.SourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(mapping.ItemId, normalizedItemId, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                version_++;
                Persist();
                raisedVersion = version_;
                raise = true;
            }

            if (!raise)
            {
                return version_;
            }
        }

        if (raise)
        {
            Changed?.Invoke(raisedVersion);
        }

        return raisedVersion;
    }

    public long RemoveSource(string sourceId)
    {
        string normalizedSourceId = NormalizeSourceId(sourceId);

        long raisedVersion = 0;
        bool raise = false;
        lock (sync_)
        {
            int removed = mappings_.RemoveAll(mapping =>
                string.Equals(mapping.SourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                version_++;
                Persist();
                raisedVersion = version_;
                raise = true;
            }

            if (!raise)
            {
                return version_;
            }
        }

        if (raise)
        {
            Changed?.Invoke(raisedVersion);
        }

        return raisedVersion;
    }

    /// <summary>
    /// Move every mapping of one source off a named subscription back onto the source default
    /// (empty Subscription). Used when a named subscription is deleted (spec §6). Returns count moved.
    /// </summary>
    public int ReassignSubscription(string sourceId, string subscriptionName)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(subscriptionName))
        {
            return 0;
        }

        string target = subscriptionName.Trim();
        (IReadOnlyList<TagMapping> mappings, _) = GetSnapshot();
        int moved = 0;
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            if (!string.Equals(mapping.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals((mapping.Subscription ?? string.Empty).Trim(), target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TagMapping updated = CloneMapping(mapping);
            updated.Subscription = string.Empty;
            if (TryUpdate(updated, out _))
            {
                moved++;
            }
        }

        return moved;
    }

    private static TagMapping CloneMapping(TagMapping m) => new()
    {
        ProviderSourceId = m.ProviderSourceId,
        ProviderItemId = m.ProviderItemId,
        SourceId = m.SourceId,
        ItemId = m.ItemId,
        UaNodeId = m.UaNodeId,
        DisplayName = m.DisplayName,
        Description = m.Description,
        DataType = m.DataType,
        Enabled = m.Enabled,
        Mode = m.Mode,
        ManualValue = m.ManualValue,
        PollRateMs = m.PollRateMs,
        DaGroup = m.DaGroup,
        DeadbandPct = m.DeadbandPct,
        Writeable = m.Writeable,
        AccessRights = m.AccessRights,
        MqttEnabled = m.MqttEnabled,
        MqttTopic = m.MqttTopic,
        InfluxEnabled = m.InfluxEnabled,
        Subscription = m.Subscription
    };

    public long SetAll(IEnumerable<TagMapping> tags)
    {
        long raisedVersion;
        lock (sync_)
        {
            mappings_ = NormalizeAll(tags);
            version_++;
            Persist();
            raisedVersion = version_;
        }

        Changed?.Invoke(raisedVersion);
        return raisedVersion;
    }

    /// <summary>
    /// Rewrites DaGroup references after a group rename. Returns the number of
    /// mappings updated (name comparison is OrdinalIgnoreCase, matching the
    /// group upsert path).
    /// </summary>
    public int RenameDaGroup(string sourceId, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return 0;
        int updated;
        lock (sync_)
        {
            updated = 0;
            for (int i = 0; i < mappings_.Count; i++)
            {
                TagMapping m = mappings_[i];
                if (!string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(m.DaGroup, oldName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                TagMapping copy = Normalize(m); copy.DaGroup = newName.Trim(); mappings_[i] = copy;
                updated++;
            }
            if (updated > 0) { version_++; Persist(); }
        }
        if (updated > 0) Changed?.Invoke(version_);
        return updated;
    }

    /// <summary>
    /// Detaches every mapping from a deleted group: DaGroup cleared and the poll
    /// rate reset to 0 (= Source Default fallback). Returns the count detached.
    /// </summary>
    public int ClearDaGroup(string sourceId, string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName)) return 0;
        int updated;
        lock (sync_)
        {
            updated = 0;
            for (int i = 0; i < mappings_.Count; i++)
            {
                TagMapping m = mappings_[i];
                if (!string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(m.DaGroup, groupName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                TagMapping copy = Normalize(m); copy.DaGroup = null; copy.PollRateMs = 0; mappings_[i] = copy;
                updated++;
            }
            if (updated > 0) { version_++; Persist(); }
        }
        if (updated > 0) Changed?.Invoke(version_);
        return updated;
    }

    /// <summary>
    /// Keeps member tags' numeric rate aligned with their named group's current
    /// rate (the COM bucket is still rate-keyed). Returns the count aligned.
    /// </summary>
    public int SyncDaGroupRate(string sourceId, string groupName, int rateMs)
    {
        if (string.IsNullOrWhiteSpace(groupName) || rateMs < 100) return 0;
        int updated;
        lock (sync_)
        {
            updated = 0;
            for (int i = 0; i < mappings_.Count; i++)
            {
                TagMapping m = mappings_[i];
                if (!string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(m.DaGroup, groupName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (m.PollRateMs == rateMs) continue;
                TagMapping copy = Normalize(m); copy.PollRateMs = rateMs; mappings_[i] = copy;
                updated++;
            }
            if (updated > 0) { version_++; Persist(); }
        }
        if (updated > 0) Changed?.Invoke(version_);
        return updated;
    }

    public IReadOnlyList<TagMapping> GetBySource(string sourceId)
    {
        string normalizedSourceId = NormalizeSourceId(sourceId);

        lock (sync_)
        {
            return mappings_
                .Where(mapping => string.Equals(mapping.SourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    private static List<TagMapping> NormalizeAll(IEnumerable<TagMapping> tags)
    {
        return tags
            .Select(Normalize)
            .Where(tag => tag.ItemId.Length > 0)
            .GroupBy(tag => (tag.SourceId, tag.ItemId), StringTupleComparer.Instance)
            .Select(group => group.First())
            .ToList();
    }

    private static TagMapping Normalize(TagMapping tag)
    {
        string sourceId = NormalizeSourceId(tag.SourceId);
        string itemId = tag.ItemId?.Trim() ?? string.Empty;
        string defaultNodeId = itemId.Length == 0 ? string.Empty : $"ns=2;s={sourceId}/{itemId}";

        string accessRights = NormalizeAccessRights(tag.AccessRights, tag.Mode, tag.Writeable);
        bool writeable = accessRights is TagAccessRights.ReadWrite or TagAccessRights.Write;
        string mode = NormalizeMode(tag.Mode);
        // Migration: legacy Write-mode-with-writeable maps to AccessRights=Write + Mode=Source
        if (accessRights == TagAccessRights.Write && mode == TagMode.Manual)
        {
            mode = TagMode.Source;
        }

        (string? providerSourceId, string? providerItemId) = NormalizeProvider(tag, sourceId, itemId);

        return new TagMapping
        {
            ProviderSourceId = providerSourceId,
            ProviderItemId = providerItemId,
            SourceId = sourceId,
            ItemId = itemId,
            UaNodeId = string.IsNullOrWhiteSpace(tag.UaNodeId) ? defaultNodeId : tag.UaNodeId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(tag.DisplayName) ? itemId : tag.DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(tag.Description) ? null : tag.Description.Trim(),
            DataType = string.IsNullOrWhiteSpace(tag.DataType) ? "Auto" : tag.DataType.Trim(),
            Enabled = tag.Enabled,
            Mode = mode,
            ManualValue = string.IsNullOrWhiteSpace(tag.ManualValue) ? null : tag.ManualValue.Trim(),
            PollRateMs = Math.Max(0, tag.PollRateMs),
            DaGroup = string.IsNullOrWhiteSpace(tag.DaGroup) ? null : tag.DaGroup.Trim(),
            DeadbandPct = Math.Clamp(tag.DeadbandPct, 0f, 100f),
            Writeable = writeable,
            AccessRights = accessRights,
            MqttEnabled = tag.MqttEnabled,
            MqttTopic = string.IsNullOrWhiteSpace(tag.MqttTopic) ? null : tag.MqttTopic.Trim(),
            InfluxEnabled = tag.InfluxEnabled,
            Subscription = (tag.Subscription ?? string.Empty).Trim()
        };
    }

    /// <summary>
    /// Normalizes the optional provider link. Returns nulls when no link is set, or when the
    /// link points at the tag itself (a self-link is rejected to avoid a write loop).
    /// </summary>
    private static (string? SourceId, string? ItemId) NormalizeProvider(TagMapping tag, string sourceId, string itemId)
    {
        string? providerSourceId = tag.ProviderSourceId?.Trim();
        string? providerItemId = tag.ProviderItemId?.Trim();
        if (string.IsNullOrEmpty(providerSourceId) || string.IsNullOrEmpty(providerItemId))
        {
            return (null, null);
        }

        providerSourceId = NormalizeSourceId(providerSourceId);
        if (string.Equals(providerSourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(providerItemId, itemId, StringComparison.OrdinalIgnoreCase))
        {
            // Self-link would create a write loop; drop it.
            return (null, null);
        }

        return (providerSourceId, providerItemId);
    }

    private static string NormalizeSourceId(string? sourceId)
    {
        string value = sourceId?.Trim() ?? string.Empty;
        return value.Length == 0 ? DaRuntimeSettings.DefaultSourceId : value;
    }

    private static string NormalizeMode(string? mode)
    {
        return string.Equals(mode?.Trim(), TagMode.Manual, StringComparison.OrdinalIgnoreCase)
            ? TagMode.Manual
            : TagMode.Source;
    }

    private static string NormalizeAccessRights(string? accessRights, string mode, bool writeable)
    {
        string value = accessRights?.Trim() ?? string.Empty;
        // Tolerate common spellings ("ReadWrite", "Read-Write", "readwrite") so a
        // variant can never silently downgrade rights to Read.
        string compact = value.Replace("-", string.Empty, StringComparison.Ordinal)
                              .Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.Equals(compact, "ReadWrite", StringComparison.OrdinalIgnoreCase))
            return TagAccessRights.ReadWrite;
        if (string.Equals(compact, "Write", StringComparison.OrdinalIgnoreCase))
            return TagAccessRights.Write;
        if (string.Equals(compact, "Read", StringComparison.OrdinalIgnoreCase))
            return TagAccessRights.Read;
        // Migration from legacy Mode+Writeable when AccessRights is absent
        if (string.Equals(mode, TagMode.Manual, StringComparison.OrdinalIgnoreCase) && writeable)
            return TagAccessRights.Write;
        if (writeable)
            return TagAccessRights.ReadWrite;
        return TagAccessRights.Read;
    }


    private void Persist()
    {
        try
        {
            string json = JsonSerializer.Serialize(mappings_, JsonOptions);
            File.WriteAllText(persist_path_, json);
        }
        catch
        {
        }
    }

    private List<TagMapping>? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_)) return null;
            string json = File.ReadAllText(persist_path_);
            // Phase 2: accept legacy DaItemId / ProviderDaItemId on disk.
            json = json
                .Replace("\"DaItemId\"", "\"itemId\"", StringComparison.Ordinal)
                .Replace("\"ProviderDaItemId\"", "\"providerItemId\"", StringComparison.Ordinal);
            return JsonSerializer.Deserialize<List<TagMapping>>(json);
        }
        catch
        {
            return null;
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string SourceId, string ItemId)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string SourceId, string ItemId) x, (string SourceId, string ItemId) y)
        {
            return string.Equals(x.SourceId, y.SourceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ItemId, y.ItemId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string SourceId, string ItemId) value)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.SourceId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ItemId));
        }
    }
}
