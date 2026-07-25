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
            HashSet<(string SourceId, string DaItemId)> existing = new(mappings_.Count, StringTupleComparer.Instance);
            for (int i = 0; i < mappings_.Count; i++)
            {
                existing.Add((mappings_[i].SourceId, mappings_[i].DaItemId));
            }

            bool changed = false;

            foreach (TagMapping tag in tags)
            {
                TagMapping normalized = Normalize(tag);
                if (normalized.DaItemId.Length == 0)
                {
                    continue;
                }

                if (!existing.Add((normalized.SourceId, normalized.DaItemId)))
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
                string.Equals(mapping.DaItemId, normalized.DaItemId, StringComparison.OrdinalIgnoreCase));

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

    public long Remove(string sourceId, string daItemId)
    {
        string normalizedSourceId = NormalizeSourceId(sourceId);
        string normalizedItemId = daItemId?.Trim() ?? string.Empty;

        long raisedVersion = 0;
        bool raise = false;
        lock (sync_)
        {
            int removed = mappings_.RemoveAll(mapping =>
                string.Equals(mapping.SourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(mapping.DaItemId, normalizedItemId, StringComparison.OrdinalIgnoreCase));

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
            .Where(tag => tag.DaItemId.Length > 0)
            .GroupBy(tag => (tag.SourceId, tag.DaItemId), StringTupleComparer.Instance)
            .Select(group => group.First())
            .ToList();
    }

    private static TagMapping Normalize(TagMapping tag)
    {
        string sourceId = NormalizeSourceId(tag.SourceId);
        string itemId = tag.DaItemId?.Trim() ?? string.Empty;
        string defaultNodeId = itemId.Length == 0 ? string.Empty : $"ns=2;s={sourceId}/{itemId}";

        string accessRights = NormalizeAccessRights(tag.AccessRights, tag.Mode, tag.Writeable);
        bool writeable = accessRights is TagAccessRights.ReadWrite or TagAccessRights.Write;
        string mode = NormalizeMode(tag.Mode);
        // Migration: legacy Write-mode-with-writeable maps to AccessRights=Write + Mode=Source
        if (accessRights == TagAccessRights.Write && mode == TagMode.Manual)
        {
            mode = TagMode.Source;
        }

        (string? providerSourceId, string? providerDaItemId) = NormalizeProvider(tag, sourceId, itemId);

        return new TagMapping
        {
            ProviderSourceId = providerSourceId,
            ProviderDaItemId = providerDaItemId,
            SourceId = sourceId,
            DaItemId = itemId,
            UaNodeId = string.IsNullOrWhiteSpace(tag.UaNodeId) ? defaultNodeId : tag.UaNodeId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(tag.DisplayName) ? itemId : tag.DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(tag.Description) ? null : tag.Description.Trim(),
            DataType = string.IsNullOrWhiteSpace(tag.DataType) ? "Auto" : tag.DataType.Trim(),
            Enabled = tag.Enabled,
            Mode = mode,
            ManualValue = string.IsNullOrWhiteSpace(tag.ManualValue) ? null : tag.ManualValue.Trim(),
            PollRateMs = Math.Max(0, tag.PollRateMs),
            DeadbandPct = Math.Clamp(tag.DeadbandPct, 0f, 100f),
            Writeable = writeable,
            AccessRights = accessRights,
            MqttEnabled = tag.MqttEnabled,
            MqttTopic = string.IsNullOrWhiteSpace(tag.MqttTopic) ? null : tag.MqttTopic.Trim(),
            InfluxEnabled = tag.InfluxEnabled
        };
    }

    /// <summary>
    /// Normalizes the optional provider link. Returns nulls when no link is set, or when the
    /// link points at the tag itself (a self-link is rejected to avoid a write loop).
    /// </summary>
    private static (string? SourceId, string? DaItemId) NormalizeProvider(TagMapping tag, string sourceId, string itemId)
    {
        string? providerSourceId = tag.ProviderSourceId?.Trim();
        string? providerDaItemId = tag.ProviderDaItemId?.Trim();
        if (string.IsNullOrEmpty(providerSourceId) || string.IsNullOrEmpty(providerDaItemId))
        {
            return (null, null);
        }

        providerSourceId = NormalizeSourceId(providerSourceId);
        if (string.Equals(providerSourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(providerDaItemId, itemId, StringComparison.OrdinalIgnoreCase))
        {
            // Self-link would create a write loop; drop it.
            return (null, null);
        }

        return (providerSourceId, providerDaItemId);
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
        if (string.Equals(value, TagAccessRights.ReadWrite, StringComparison.OrdinalIgnoreCase))
            return TagAccessRights.ReadWrite;
        if (string.Equals(value, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase))
            return TagAccessRights.Write;
        if (string.Equals(value, TagAccessRights.Read, StringComparison.OrdinalIgnoreCase))
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
            return JsonSerializer.Deserialize<List<TagMapping>>(json);
        }
        catch
        {
            return null;
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string SourceId, string DaItemId)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string SourceId, string DaItemId) x, (string SourceId, string DaItemId) y)
        {
            return string.Equals(x.SourceId, y.SourceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.DaItemId, y.DaItemId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string SourceId, string DaItemId) value)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.SourceId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.DaItemId));
        }
    }
}
