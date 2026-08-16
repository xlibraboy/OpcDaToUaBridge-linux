namespace OpcBridge.Drivers.S7.Transport;

/// <summary>
/// Byte-oriented transport for PPI (serial today; TCP tunnel later).
/// Client sequences multi-step exchanges (E5 ack → request-data → SD2 frame).
/// </summary>
public interface IS7Transport : IAsyncDisposable
{
    bool IsOpen { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Reads up to <paramref name="buffer"/>.Length bytes within <paramref name="timeout"/>.
    /// Returns bytes actually read (may be partial). Throws <see cref="TimeoutException"/> if zero bytes before timeout.
    /// </summary>
    Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken);
}
