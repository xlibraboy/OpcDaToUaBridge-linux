namespace OpcBridge.Hmi.Core;

/// <summary>
/// One entry in the HMI tag browser's source selector. The entry with a null
/// <see cref="SourceId"/> is "all sources" and matches every tag.
/// </summary>
public sealed record SourceFilterOption(string? BridgeId, string? SourceId, string Name, string TypeLabel)
{
    /// <summary>The entry that shows tags from every source and every bridge.</summary>
    public static SourceFilterOption All { get; } = new(null, null, "All sources", SourceTypeLabels.Unknown);

    public bool IsAll => SourceId is null;

    /// <summary>False for sources whose type is unknown (removed from the bridge registry).</summary>
    public bool HasTypeLabel => TypeLabel.Length > 0;

    /// <summary>True when this entry covers the given tag's source; "all sources" covers every tag.</summary>
    public bool Matches(TagBindingKey key) =>
        IsAll
        || (string.Equals(key.BridgeId, BridgeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(key.SourceId, SourceId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Builds the tag browser's source selector from the live tag set: one entry per
/// (bridge, source), labelled with the source's configured name and a short type badge.
/// Pass a <paramref name="bridgeId"/> to list only that bridge's sources.
/// </summary>
public static class SourceFilterOptions
{
    public static IReadOnlyList<SourceFilterOption> Build(
        IEnumerable<MultiBridgeTagEntry> tags,
        string? bridgeId = null)
    {
        ArgumentNullException.ThrowIfNull(tags);

        MultiBridgeTagEntry[] entries = tags as MultiBridgeTagEntry[] ?? tags.ToArray();
        if (!string.IsNullOrWhiteSpace(bridgeId))
        {
            entries = entries
                .Where(entry => string.Equals(entry.Key.BridgeId, bridgeId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        // While the list spans more than one bridge the same source name can appear on several
        // bridges (e.g. two "default" sources), so the bridge id disambiguates it. Scoping the
        // list to one bridge already removes that ambiguity.
        bool multipleBridges = entries
            .Select(entry => entry.Key.BridgeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();

        Dictionary<string, SourceFilterOption> bySource = new(StringComparer.OrdinalIgnoreCase);
        foreach (MultiBridgeTagEntry entry in entries)
        {
            TagBindingKey key = entry.Key;
            string sourceKey = string.Concat(key.BridgeId, "::", key.SourceId);
            if (bySource.ContainsKey(sourceKey))
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(entry.SourceName) ? key.SourceId : entry.SourceName.Trim();
            bySource[sourceKey] = new SourceFilterOption(
                key.BridgeId,
                key.SourceId,
                multipleBridges ? $"{name} · {key.BridgeId}" : name,
                SourceTypeLabels.ShortLabel(entry.SourceType));
        }

        List<SourceFilterOption> options = new(bySource.Count + 1) { SourceFilterOption.All };
        options.AddRange(bySource.Values
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.BridgeId, StringComparer.OrdinalIgnoreCase));
        return options;
    }
}
