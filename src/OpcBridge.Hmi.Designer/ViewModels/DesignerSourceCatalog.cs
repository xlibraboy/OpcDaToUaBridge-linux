using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Designer.Services;

namespace OpcBridge.Hmi.Designer.ViewModels;

/// <summary>
/// Builds the Designer's source list from what the bridge reports. Signed in, the configured
/// sources come from /api/da/sources; signed out (or with the source list unavailable), the
/// sources are derived from the tag snapshot so binding still works.
/// </summary>
public static class DesignerSourceCatalog
{
    public static IReadOnlyList<BridgeSourceInfo> Build(
        IEnumerable<MultiBridgeTagEntry> tags,
        IReadOnlyList<BridgeSourceInfo>? configured)
    {
        Dictionary<string, BridgeSourceInfo> byId = new(StringComparer.OrdinalIgnoreCase);

        if (configured is not null)
        {
            foreach (BridgeSourceInfo source in configured)
            {
                if (!string.IsNullOrWhiteSpace(source.SourceId))
                {
                    byId[source.SourceId] = source;
                }
            }
        }

        foreach (MultiBridgeTagEntry tag in tags)
        {
            string sourceId = tag.Key.SourceId;
            if (string.IsNullOrWhiteSpace(sourceId) || byId.ContainsKey(sourceId))
            {
                continue;
            }

            byId[sourceId] = new BridgeSourceInfo(
                sourceId,
                tag.SourceName,
                tag.SourceType,
                string.Empty,
                null);
        }

        return byId.Values
            .OrderBy(source => source.DisplayNameOrId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
