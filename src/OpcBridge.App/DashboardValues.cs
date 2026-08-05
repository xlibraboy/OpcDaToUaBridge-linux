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
