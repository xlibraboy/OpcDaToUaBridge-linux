using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App.Hmi;

public static class HmiTagSnapshot
{
    public static HmiTagsResponse Build(
        MappingStore mappingStore,
        BridgeState bridgeState,
        DaRuntimeSettingsSnapshot sourceSettings,
        LogicStore? logicStore = null)
    {
        (IReadOnlyList<TagMapping> mappings, long version) = mappingStore.GetSnapshot();
        IReadOnlyList<BridgeValueSnapshot> values = bridgeState.GetValues();

        // A tag bound to an internal relay the plant logic drives is shown read-only: the logic
        // owns its value, so the faceplate must not offer a write the bridge would refuse.
        LogicRelayRules relayRules = LogicRelayRules.None;
        if (logicStore is not null)
        {
            (IReadOnlyList<LogicBlockDto> blocks, IReadOnlyList<LogicRelayDto> relays, long _) = logicStore.GetSnapshot();
            relayRules = LogicRelayRules.Build(relays, blocks);
        }

        // Source identity per tag: the HMI tag browser separates tags by source, so every
        // tag carries its source's display name and type. Mappings whose source has been
        // removed keep their SourceId as the name and report no type.
        Dictionary<string, DaSourceRuntimeSettings> sourcesBySourceId = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sourceSettings.Sources.Count; i++)
        {
            DaSourceRuntimeSettings source = sourceSettings.Sources[i];
            sourcesBySourceId[source.SourceId] = source;
        }

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
            sourcesBySourceId.TryGetValue(m.SourceId, out DaSourceRuntimeSettings? source);
            string sourceName = source is null || string.IsNullOrWhiteSpace(source.DisplayName)
                ? m.SourceId
                : source.DisplayName;
            tags.Add(new HmiTagDto
            {
                SourceId = m.SourceId,
                SourceName = sourceName,
                SourceType = source?.SourceType ?? (TagMode.IsInternal(m.Mode) ? "Internal" : string.Empty),
                ItemId = m.ItemId,
                DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.ItemId : m.DisplayName,
                Description = m.Description,
                DataType = m.DataType,
                Value = snap?.Value,
                TimestampUtc = snap?.TimestampUtc,
                DaQuality = snap?.DaQuality,
                IsGood = snap?.IsGood,
                Writeable = EffectiveWriteable(m, relayRules),
                UpdateRateMs = DashboardValues.LookupUpdateRate(updateRateByKey, m.SourceId, m.ItemId),
                Unit = string.IsNullOrWhiteSpace(m.Unit) ? null : m.Unit,
                RangeMin = m.RangeMin,
                RangeMax = m.RangeMax,
                TrendStyle = TrendStyleTypes.Normalize(m.TrendStyle),
                InfluxEnabled = m.InfluxEnabled,
                Digital = TagDigital.Resolve(m),
                OnText = string.IsNullOrWhiteSpace(m.OnText) ? null : m.OnText.Trim(),
                OffText = string.IsNullOrWhiteSpace(m.OffText) ? null : m.OffText.Trim()
            });
        }

        tags.Sort((a, b) =>
        {
            int c = string.Compare(a.SourceId, b.SourceId, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.ItemId, b.ItemId, StringComparison.OrdinalIgnoreCase);
        });

        return new HmiTagsResponse { Version = version, Tags = tags };
    }

    /// <summary>
    /// The tag's own rights, minus the plant logic's claim: a tag bound to a relay the logic
    /// drives is read-only whatever the mapping says — the same rule the write path enforces.
    /// </summary>
    private static bool EffectiveWriteable(TagMapping mapping, LogicRelayRules relayRules) =>
        mapping.Writeable
        && (!relayRules.TryGetByTag(mapping.SourceId, mapping.ItemId, out LogicRelayDto relay)
            || relayRules.IsWritable(relay));
}
