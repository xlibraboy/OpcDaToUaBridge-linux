using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Composes the live logic snapshot from the definition store, the mappings and the value
/// cache. Kept out of <see cref="LogicStateEvaluator"/> so the derivation stays pure, and
/// shared by <c>GET /api/logic/state</c> and the HMI broadcaster so both always agree.
/// </summary>
public static class LogicStateRead
{
    public static LogicStateSnapshot Snapshot(
        LogicStore logicStore,
        MappingStore mappingStore,
        BridgeState bridgeState,
        DateTime nowUtc,
        LogicStateStore? states = null)
    {
        (IReadOnlyList<LogicBlockDto> blocks, long version) = logicStore.GetSnapshot();
        (IReadOnlyList<TagMapping> mappings, _) = mappingStore.GetSnapshot();
        IReadOnlyDictionary<string, TagMapping> index = LogicStateEvaluator.BuildMappingIndex(mappings);

        return LogicStateEvaluator.Evaluate(
            blocks,
            version,
            (sourceId, itemId) =>
                bridgeState.TryGetSnapshot(sourceId, itemId, out BridgeValueSnapshot snapshot) ? snapshot : null,
            (sourceId, itemId) =>
                index.TryGetValue(LogicStateEvaluator.Key(sourceId, itemId), out TagMapping mapping) ? mapping : null,
            nowUtc,
            states);
    }
}
