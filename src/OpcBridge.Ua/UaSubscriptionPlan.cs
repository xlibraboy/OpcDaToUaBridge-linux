// src/OpcBridge.Ua/UaSubscriptionPlan.cs
using OpcBridge.Core;

namespace OpcBridge.Ua;

/// <summary>
/// Partitions the desired mapped-tag set into per-subscription buckets (spec §5).
/// Pure function — no session/SDK types — so bucket grouping is unit-testable in isolation.
/// Filter parity with <see cref="UaSamplingRates.BuildDesiredSampling"/>: enabled, non-Manual,
/// non-empty NodeId, non-Write-only tags only.
/// </summary>
public static class UaSubscriptionPlan
{
    /// <summary>Bucket key for unassigned tags (the implicit source-default subscription).</summary>
    public const string DefaultBucketKey = "";

    /// <summary>
    /// Bucket key → (nodeId → sampling interval ms), preserving desired order within a bucket.
    /// Named buckets sample every member at the bucket's configured rate (clamped ≥ 100 ms);
    /// the default bucket keeps legacy per-tag override semantics.
    /// </summary>
    public static Dictionary<string, Dictionary<string, int>> GroupByBucket(
        IReadOnlyList<TagMapping> desiredMappings,
        IReadOnlyList<UaSubscriptionSettings>? subscriptions,
        int defaultSamplingMs)
    {
        Dictionary<string, Dictionary<string, int>> plan = new(StringComparer.Ordinal);
        if (desiredMappings is null)
        {
            return plan;
        }

        // Case-insensitive lookup: normalized name → canonical bucket key.
        Dictionary<string, string> bucketByKey = new(StringComparer.OrdinalIgnoreCase);
        if (subscriptions is not null)
        {
            foreach (UaSubscriptionSettings sub in subscriptions)
            {
                string key = NormalizeName(sub.Name);
                if (key.Length == 0 || bucketByKey.ContainsKey(key))
                {
                    continue;
                }

                bucketByKey[key] = key;
            }
        }

        for (int i = 0; i < desiredMappings.Count; i++)
        {
            TagMapping mapping = desiredMappings[i];
            if (!mapping.Enabled
                || string.Equals(mapping.Mode, TagMode.Manual, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(mapping.ItemId)
                || string.Equals(mapping.AccessRights, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string nodeId = mapping.ItemId.Trim();
            string requested = NormalizeName(mapping.Subscription);
            string bucketKey = requested.Length > 0 && bucketByKey.ContainsKey(requested)
                ? bucketByKey[requested]
                : DefaultBucketKey;

            int sampling;
            if (bucketKey == DefaultBucketKey)
            {
                sampling = mapping.PollRateMs > 0 ? mapping.PollRateMs : defaultSamplingMs;
                if (sampling < 0)
                {
                    sampling = defaultSamplingMs;
                }
            }
            else
            {
                int configured = subscriptions!.First(s => NormalizeName(s.Name) == bucketKey).UpdateRateMs;
                sampling = Math.Max(100, configured);
            }

            if (!plan.TryGetValue(bucketKey, out Dictionary<string, int>? items))
            {
                items = new Dictionary<string, int>(StringComparer.Ordinal);
                plan[bucketKey] = items;
            }

            // First wins; Diff keys are unique per bucket.
            if (!items.ContainsKey(nodeId))
            {
                items[nodeId] = sampling;
            }
        }

        return plan;
    }

    /// <summary>Trimmed bucket name; empty string when null/whitespace (the default bucket).</summary>
    public static string NormalizeName(string? name) => name?.Trim() ?? string.Empty;
}
