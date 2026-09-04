using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App.Hmi;

public static class HmiTagSnapshot
{
    public static HmiTagsResponse Build(MappingStore mappingStore, BridgeState bridgeState)
    {
        (IReadOnlyList<TagMapping> mappings, long version) = mappingStore.GetSnapshot();
        IReadOnlyList<BridgeValueSnapshot> values = bridgeState.GetValues();

        Dictionary<string, BridgeValueSnapshot> byKey = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < values.Count; i++)
        {
            BridgeValueSnapshot v = values[i];
            byKey[string.Concat(v.SourceId, "::", v.ItemId)] = v;
        }

        // Effective update rate per tag: per-tag PollRateMs wins, else the source default.
        Dictionary<string, int> sourceRates = bridgeState.GetStatus().Sources
            .GroupBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().UpdateRateMs, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> updateRateByKey = DashboardValues.BuildUpdateRateLookup(mappings, sourceRates);

        List<HmiTagDto> tags = new();
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping m = mappings[i];
            if (!m.Enabled)
            {
                continue;
            }

            byKey.TryGetValue(string.Concat(m.SourceId, "::", m.ItemId), out BridgeValueSnapshot? snap);
            tags.Add(new HmiTagDto
            {
                SourceId = m.SourceId,
                ItemId = m.ItemId,
                DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.ItemId : m.DisplayName,
                DataType = m.DataType,
                Value = snap?.Value,
                TimestampUtc = snap?.TimestampUtc,
                DaQuality = snap?.DaQuality,
                IsGood = snap?.IsGood,
                Writeable = m.Writeable,
                UpdateRateMs = DashboardValues.LookupUpdateRate(updateRateByKey, m.SourceId, m.ItemId),
                Unit = string.IsNullOrWhiteSpace(m.Unit) ? null : m.Unit,
                TrendStyle = TrendStyleTypes.Normalize(m.TrendStyle)
            });
        }

        tags.Sort((a, b) =>
        {
            int c = string.Compare(a.SourceId, b.SourceId, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.ItemId, b.ItemId, StringComparison.OrdinalIgnoreCase);
        });

        return new HmiTagsResponse { Version = version, Tags = tags };
    }
}
