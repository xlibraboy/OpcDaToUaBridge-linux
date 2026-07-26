using OpcBridge.Core;

namespace OpcBridge.Da;

/// <summary>
/// Optional subscription surface for source clients that can push values
/// (OPC DA IOPCDataCallback, OPC UA MonitoredItems, etc.).
/// </summary>
public interface ISubscribableSourceClient
{
    event Action<IReadOnlyList<BridgeValue>>? ValuesReceived;
}
