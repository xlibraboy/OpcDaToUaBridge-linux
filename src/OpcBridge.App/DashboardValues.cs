using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Resolves the data type shown for a live value: the runtime type carried by
/// the value itself (what the external source actually sent) wins; the type
/// configured on the mapping is the fallback while no value exists yet.
/// Kept separate from the dashboard endpoint so the semantics are unit-testable.
/// </summary>
internal static class DashboardValues
{
    public static Dictionary<string, string> BuildDataTypeLookup(IReadOnlyList<TagMapping> mappings)
    {
        Dictionary<string, string> lookup = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            lookup[BridgeState.NormalizeKey(mapping.SourceId, mapping.ItemId)] = mapping.DataType;
        }

        return lookup;
    }

    public static string? LookupDataType(Dictionary<string, string> lookup, string sourceId, string itemId)
    {
        return lookup.TryGetValue(BridgeState.NormalizeKey(sourceId, itemId), out string? dataType)
            ? dataType
            : null;
    }

    /// <summary>
    /// Effective update rate per mapped tag: an assigned named subscription wins
    /// (clamped to ≥ 100 ms); otherwise the per-tag <c>PollRateMs</c> override;
    /// otherwise the source's default rate. Unknown sources fall back to 0.
    /// </summary>
    public static Dictionary<string, int> BuildUpdateRateLookup(
        IReadOnlyList<TagMapping> mappings,
        IReadOnlyDictionary<string, int> sourceDefaultRates)
        => BuildUpdateRateLookup(mappings, sourceDefaultRates, EmptySubscriptions);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionSettings>> EmptySubscriptions =
        new Dictionary<string, IReadOnlyList<UaSubscriptionSettings>>(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, int> BuildUpdateRateLookup(
        IReadOnlyList<TagMapping> mappings,
        IReadOnlyDictionary<string, int> sourceDefaultRates,
        IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionSettings>> uaSubscriptionsBySource)
    {
        Dictionary<string, int> lookup = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            uaSubscriptionsBySource.TryGetValue(mapping.SourceId,
                out IReadOnlyList<UaSubscriptionSettings>? subs);
            int rate = ResolveEffectiveRate(mapping, sourceDefaultRates, subs);
            lookup[BridgeState.NormalizeKey(mapping.SourceId, mapping.ItemId)] = rate;
        }

        return lookup;
    }

    private static int ResolveEffectiveRate(
        TagMapping mapping,
        IReadOnlyDictionary<string, int> sourceDefaultRates,
        IReadOnlyList<UaSubscriptionSettings>? subscriptions)
    {
        string requested = (mapping.Subscription ?? string.Empty).Trim();
        if (requested.Length > 0 && subscriptions is not null)
        {
            for (int i = 0; i < subscriptions.Count; i++)
            {
                if (string.Equals(subscriptions[i].Name.Trim(), requested, StringComparison.OrdinalIgnoreCase))
                {
                    return Math.Max(100, subscriptions[i].UpdateRateMs);
                }
            }
        }

        return mapping.PollRateMs > 0
            ? mapping.PollRateMs
            : (sourceDefaultRates.TryGetValue(mapping.SourceId, out int sourceRate) ? sourceRate : 0);
    }

    public static int LookupUpdateRate(Dictionary<string, int> lookup, string sourceId, string itemId)
    {
        return lookup.TryGetValue(BridgeState.NormalizeKey(sourceId, itemId), out int rate) ? rate : 0;
    }

    /// <summary>Maps the value's CLR type to the UA-style type name shown in the UI.</summary>
    public static string? InferDataType(object? value)
    {
        return value switch
        {
            null => null,
            bool => "Boolean",
            sbyte => "SByte",
            byte => "Byte",
            short => "Int16",
            ushort => "UInt16",
            int => "Int32",
            uint => "UInt32",
            long => "Int64",
            ulong => "UInt64",
            float => "Float",
            double => "Double",
            decimal => "Decimal",
            string => "String",
            DateTime => "DateTime",
            byte[] => "ByteString",
            _ => null
        };
    }

    /// <summary>Runtime type of the actual value; falls back to the mapping's configured type.</summary>
    public static string? ResolveDataType(object? value, Dictionary<string, string> lookup, string sourceId, string itemId)
    {
        return InferDataType(value) ?? LookupDataType(lookup, sourceId, itemId);
    }
}
