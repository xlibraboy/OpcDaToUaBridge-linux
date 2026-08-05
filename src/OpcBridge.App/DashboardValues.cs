using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Joins live value snapshots with the data type configured on their mapping.
/// Kept separate from the dashboard endpoint so the lookup semantics are unit-testable.
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
}
