namespace OpcBridge.Hmi.Core;

/// <summary>
/// Stable live-tag identity across multiple bridges.
/// </summary>
public readonly record struct TagBindingKey(string BridgeId, string SourceId, string DaItemId)
{
    public static TagBindingKey Create(string bridgeId, string sourceId, string daItemId) =>
        new(
            (bridgeId ?? string.Empty).Trim(),
            (sourceId ?? string.Empty).Trim(),
            (daItemId ?? string.Empty).Trim());

    public string CacheKey => string.Concat(BridgeId, "::", SourceId, "::", DaItemId);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(BridgeId)
        && string.IsNullOrWhiteSpace(SourceId)
        && string.IsNullOrWhiteSpace(DaItemId);

    public bool EqualsIgnoreCase(TagBindingKey other) =>
        string.Equals(BridgeId, other.BridgeId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(SourceId, other.SourceId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(DaItemId, other.DaItemId, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => CacheKey;
}

public sealed class TagBindingKeyComparer : IEqualityComparer<TagBindingKey>
{
    public static readonly TagBindingKeyComparer Instance = new();

    public bool Equals(TagBindingKey x, TagBindingKey y) => x.EqualsIgnoreCase(y);

    public int GetHashCode(TagBindingKey obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.BridgeId ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceId ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.DaItemId ?? string.Empty));
}
