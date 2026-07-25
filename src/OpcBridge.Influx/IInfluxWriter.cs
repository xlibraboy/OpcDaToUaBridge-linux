using OpcBridge.Core;

namespace OpcBridge.Influx;

public enum InfluxConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted
}

public interface IInfluxWriter : IAsyncDisposable
{
    InfluxConnectionState State { get; }
    event Action<InfluxConnectionState>? StateChanged;
    Task ConnectAsync(InfluxOptions options, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task WritePointAsync(BridgeValue value, string? displayName, CancellationToken ct);
}
