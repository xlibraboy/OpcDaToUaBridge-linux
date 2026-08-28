using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Core;

namespace OpcBridge.App;

public sealed class InterlinkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private List<InterlinkRule> rules_;
    private long version_;

    public InterlinkStore(IOptions<BridgeOptions> options)
    {
        _ = options;
        persist_path_ = Path.Combine(AppContext.BaseDirectory, "links.json");
        rules_ = LoadFromDisk() ?? new List<InterlinkRule>();
    }

    public (IReadOnlyList<InterlinkRule> Rules, long Version) GetSnapshot()
    {
        lock (sync_)
        {
            return (rules_.ToArray(), version_);
        }
    }

    public long SetAll(IEnumerable<InterlinkRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        lock (sync_)
        {
            List<InterlinkRule> normalized = new();
            HashSet<Guid> ids = new();
            HashSet<(string SourceId, string ItemId)> consumers = new(ConsumerKeyComparer.Instance);

            foreach (InterlinkRule rule in rules)
            {
                if (!TryNormalize(rule, out InterlinkRule normalizedRule, out string? error))
                {
                    throw new InvalidOperationException(error);
                }

                if (!ids.Add(normalizedRule.Id))
                {
                    throw new InvalidOperationException("Rule already exists.");
                }

                if (!consumers.Add((normalizedRule.ConsumerSourceId, normalizedRule.ConsumerItemId)))
                {
                    throw new InvalidOperationException("Consumer already has a provider.");
                }

                normalized.Add(normalizedRule);
            }

            rules_ = normalized;
            version_++;
            Persist();
            return version_;
        }
    }

    public bool TryAdd(InterlinkRule rule, out long version, out string? error)
    {
        lock (sync_)
        {
            if (!TryNormalize(rule, out InterlinkRule normalized, out error))
            {
                version = version_;
                return false;
            }

            if (rules_.Any(existing => existing.Id == normalized.Id))
            {
                version = version_;
                error = "Rule already exists.";
                return false;
            }

            if (HasConsumerConflict(normalized.ConsumerSourceId, normalized.ConsumerItemId, normalized.Id))
            {
                version = version_;
                error = "Consumer already has a provider.";
                return false;
            }

            rules_.Add(normalized);
            version_++;
            Persist();
            version = version_;
            error = null;
            return true;
        }
    }

    public bool TryUpdate(InterlinkRule rule, out long version, out string? error)
    {
        lock (sync_)
        {
            if (!TryNormalize(rule, out InterlinkRule normalized, out error))
            {
                version = version_;
                return false;
            }

            int index = rules_.FindIndex(existing => existing.Id == normalized.Id);
            if (index < 0)
            {
                version = version_;
                error = "Rule not found.";
                return false;
            }

            if (HasConsumerConflict(normalized.ConsumerSourceId, normalized.ConsumerItemId, normalized.Id))
            {
                version = version_;
                error = "Consumer already has a provider.";
                return false;
            }

            rules_[index] = normalized;
            version_++;
            Persist();
            version = version_;
            error = null;
            return true;
        }
    }

    public bool TryRemove(Guid id, out long version)
    {
        lock (sync_)
        {
            int removed = rules_.RemoveAll(existing => existing.Id == id);
            if (removed == 0)
            {
                version = version_;
                return false;
            }

            version_++;
            Persist();
            version = version_;
            return true;
        }
    }
    public long RemoveBySource(string sourceId)
    {
        string normalizedSourceId = NormalizeSourceId(sourceId);

        lock (sync_)
        {
            int removed = rules_.RemoveAll(existing =>
                string.Equals(existing.ProviderSourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existing.ConsumerSourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                version_++;
                Persist();
            }

            return version_;
        }
    }

    public int MigrateFromMappings(IEnumerable<TagMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        lock (sync_)
        {
            List<InterlinkRule> migratedRules = new();
            foreach (TagMapping mapping in mappings)
            {
                if (TryCreateMigratedRule(mapping, out InterlinkRule rule))
                {
                    migratedRules.Add(rule);
                }
            }

            if (migratedRules.Count == 0)
            {
                return 0;
            }

            List<InterlinkRule> combined = new(rules_.Count + migratedRules.Count);
            combined.AddRange(rules_);
            combined.AddRange(migratedRules);

            List<InterlinkRule> normalized = NormalizeAllOrThrow(combined);
            rules_ = normalized;
            version_++;
            Persist();
            return migratedRules.Count;
        }
    }

    private static bool TryCreateMigratedRule(TagMapping mapping, out InterlinkRule rule)
    {
        string providerSourceId = NormalizeSourceId(mapping.ProviderSourceId);
        string providerItemId = mapping.ProviderItemId?.Trim() ?? string.Empty;
        string consumerSourceId = NormalizeSourceId(mapping.SourceId);
        string consumerItemId = mapping.ItemId?.Trim() ?? string.Empty;

        if (providerItemId.Length == 0 || consumerItemId.Length == 0)
        {
            rule = default!;
            return false;
        }

        if (string.Equals(providerSourceId, consumerSourceId, StringComparison.OrdinalIgnoreCase))
        {
            rule = default!;
            return false;
        }

        rule = new InterlinkRule(
            Guid.NewGuid(),
            providerSourceId,
            providerItemId,
            consumerSourceId,
            consumerItemId,
            mapping.Enabled,
            null,
            null);
        return true;
    }

    private bool HasConsumerConflict(string consumerSourceId, string consumerItemId, Guid currentId)
    {
        return rules_.Any(existing =>
            existing.Id != currentId &&
            string.Equals(existing.ConsumerSourceId, consumerSourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.ConsumerItemId, consumerItemId, StringComparison.OrdinalIgnoreCase));
    }

    private static List<InterlinkRule> NormalizeAllOrThrow(IEnumerable<InterlinkRule> rules)
    {
        List<InterlinkRule> normalized = new();
        HashSet<Guid> ids = new();
        HashSet<(string SourceId, string ItemId)> consumers = new(ConsumerKeyComparer.Instance);

        foreach (InterlinkRule rule in rules)
        {
            if (!TryNormalize(rule, out InterlinkRule normalizedRule, out string? error))
            {
                throw new InvalidOperationException(error);
            }

            if (!ids.Add(normalizedRule.Id))
            {
                throw new InvalidOperationException("Rule already exists.");
            }

            if (!consumers.Add((normalizedRule.ConsumerSourceId, normalizedRule.ConsumerItemId)))
            {
                throw new InvalidOperationException("Consumer already has a provider.");
            }

            normalized.Add(normalizedRule);
        }

        return normalized;
    }

    private static bool TryNormalize(InterlinkRule rule, out InterlinkRule normalized, out string? error)
    {
        string providerSourceId = NormalizeSourceId(rule.ProviderSourceId);
        string providerItemId = rule.ProviderItemId?.Trim() ?? string.Empty;
        string consumerSourceId = NormalizeSourceId(rule.ConsumerSourceId);
        string consumerItemId = rule.ConsumerItemId?.Trim() ?? string.Empty;

        if (providerItemId.Length == 0)
        {
            normalized = default!;
            error = "Provider item is required.";
            return false;
        }

        if (consumerItemId.Length == 0)
        {
            normalized = default!;
            error = "Consumer item is required.";
            return false;
        }

        if (string.Equals(providerSourceId, consumerSourceId, StringComparison.OrdinalIgnoreCase))
        {
            normalized = default!;
            error = "Provider and consumer must be on different sources.";
            return false;
        }

        if (rule.ProviderCanonicalType != rule.ConsumerCanonicalType)
        {
            normalized = default!;
            error = "Provider and consumer must use the same data type.";
            return false;
        }

        Guid id = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id;
        normalized = rule with
        {
            Id = id,
            ProviderSourceId = providerSourceId,
            ProviderItemId = providerItemId,
            ConsumerSourceId = consumerSourceId,
            ConsumerItemId = consumerItemId
        };
        error = null;
        return true;
    }

    private static string NormalizeSourceId(string? sourceId)
    {
        string value = sourceId?.Trim() ?? string.Empty;
        return value.Length == 0 ? DaRuntimeSettings.DefaultSourceId : value;
    }

    private void Persist()
    {
        Persist(rules_);
    }

    private void Persist(List<InterlinkRule> rules)
    {
        try
        {
            string json = JsonSerializer.Serialize(rules, JsonOptions);
            File.WriteAllText(persist_path_, json);
        }
        catch
        {
        }
    }

    private List<InterlinkRule>? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_))
            {
                return null;
            }

            string json = File.ReadAllText(persist_path_);
            List<InterlinkRule>? loaded = JsonSerializer.Deserialize<List<InterlinkRule>>(json);
            if (loaded is null)
            {
                return null;
            }

            // Drop legacy rules that violate the different-sources invariant so they
            // cannot keep forwarding values after upgrade, then rewrite the file clean.
            List<InterlinkRule> sanitized = loaded.Where(IsDifferentSourceRule).ToList();
            if (sanitized.Count != loaded.Count)
            {
                Persist(sanitized);
            }

            return sanitized;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDifferentSourceRule(InterlinkRule rule)
    {
        string providerSourceId = NormalizeSourceId(rule.ProviderSourceId);
        string consumerSourceId = NormalizeSourceId(rule.ConsumerSourceId);
        return !string.Equals(providerSourceId, consumerSourceId, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ConsumerKeyComparer : IEqualityComparer<(string SourceId, string ItemId)>
    {
        public static ConsumerKeyComparer Instance { get; } = new();

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