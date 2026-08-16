using OpcBridge.Core;

namespace OpcBridge.Ua;

/// <summary>
/// Computes the monitored-item sampling intervals and the subscription publishing
/// interval for a UA source. Per-tag <c>PollRateMs</c> wins; tags without an override
/// fall back to the source default rate. The publishing interval tracks the FASTEST
/// desired sampling so per-tag rates — not just the source rate — actually drive the
/// delivery cadence (a server only sends notifications as often as the subscription
/// publishes, no matter how fast individual items sample).
/// </summary>
public static class UaSamplingRates
{
    /// <summary>
    /// Per-NodeId sampling interval (ms) for the source-read, enabled, non-manual tags.
    /// A tag's own <c>PollRateMs</c> wins; otherwise the source default applies.
    /// </summary>
    public static Dictionary<string, int> BuildDesiredSampling(
        IReadOnlyList<TagMapping> desiredMappings,
        int defaultSamplingMs)
    {
        Dictionary<string, int> desired = new(StringComparer.Ordinal);
        if (desiredMappings is null)
        {
            return desired;
        }

        for (int i = 0; i < desiredMappings.Count; i++)
        {
            TagMapping mapping = desiredMappings[i];
            if (!mapping.Enabled)
            {
                continue;
            }

            if (string.Equals(mapping.Mode, TagMode.Manual, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.ItemId))
            {
                continue;
            }

            // Write-only tags are not source-read (matches SourceMappingCache.SourceRead filter).
            if (string.Equals(mapping.AccessRights, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string nodeId = mapping.ItemId.Trim();
            int sampling = mapping.PollRateMs > 0 ? mapping.PollRateMs : defaultSamplingMs;
            if (sampling < 0)
            {
                sampling = defaultSamplingMs;
            }

            // First wins; Diff keys are unique.
            if (!desired.ContainsKey(nodeId))
            {
                desired[nodeId] = sampling;
            }
        }

        return desired;
    }

    /// <summary>
    /// Subscription publishing interval (ms): the fastest desired sampling across the
    /// mapped tags, clamped to a minimum of 100 ms. Falls back to the source default
    /// when nothing is mapped. Setting the publishing interval to the minimum lets a
    /// tag with a faster per-tag rate deliver at that rate instead of being capped by
    /// the source rate.
    /// </summary>
    public static int DesiredPublishingInterval(
        IReadOnlyDictionary<string, int> desiredSampling,
        int defaultSamplingMs)
    {
        int min = desiredSampling is { Count: > 0 }
            ? desiredSampling.Values.Min()
            : defaultSamplingMs;
        return Math.Max(100, min);
    }
}
