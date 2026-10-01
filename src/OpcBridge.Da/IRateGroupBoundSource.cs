namespace OpcBridge.Da;

/// <summary>
/// Marker for source clients whose items are bound into OPC DA rate groups at connect time:
/// adding or removing mappings for such a source requires rebuilding its session (stop its
/// pollers, force a reconnect) so the new item set is re-bound. Implemented by the real
/// <c>OpcDaClient</c> and by the worker proxy, so isolated DA sources keep that behaviour.
/// </summary>
public interface IRateGroupBoundSource
{
}
