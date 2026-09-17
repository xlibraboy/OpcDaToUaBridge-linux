namespace OpcBridge.Hmi.Core;

/// <summary>
/// One entry in the HMI tag browser's bridge selector. The entry with a null
/// <see cref="BridgeId"/> is "all bridges" and matches every tag.
/// </summary>
public sealed record BridgeFilterOption(string? BridgeId, string Name)
{
    /// <summary>The entry that shows tags from every connected bridge.</summary>
    public static BridgeFilterOption All { get; } = new(null, "All bridges");

    public bool IsAll => BridgeId is null;

    /// <summary>True when this entry covers the given bridge; "all bridges" covers every tag.</summary>
    public bool Matches(string? bridgeId) =>
        IsAll || string.Equals(bridgeId, BridgeId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Builds the tag browser's bridge selector from the live tag set: one entry per connected
/// bridge, labelled with the bridge id (the same identity the tag rows show).
/// </summary>
public static class BridgeFilterOptions
{
    public static IReadOnlyList<BridgeFilterOption> Build(IEnumerable<MultiBridgeTagEntry> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        List<BridgeFilterOption> options = new() { BridgeFilterOption.All };
        options.AddRange(tags
            .Select(entry => entry.Key.BridgeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new BridgeFilterOption(id, id)));
        return options;
    }
}
