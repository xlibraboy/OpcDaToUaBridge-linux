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
    /// Effective update rate per mapped tag: the per-tag <c>PollRateMs</c> override wins;
    /// otherwise the source's default rate applies. Unknown sources fall back to 0.
    /// </summary>
    public static Dictionary<string, int> BuildUpdateRateLookup(
        IReadOnlyList<TagMapping> mappings,
        IReadOnlyDictionary<string, int> sourceDefaultRates)
    {
        Dictionary<string, int> lookup = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            int rate = mapping.PollRateMs > 0
                ? mapping.PollRateMs
                : (sourceDefaultRates.TryGetValue(mapping.SourceId, out int sourceRate) ? sourceRate : 0);
            lookup[BridgeState.NormalizeKey(mapping.SourceId, mapping.ItemId)] = rate;
        }

        return lookup;
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
