namespace OpcBridge.Drivers.Melsec.Transport;

/// <summary>
/// Byte-oriented transport for MELSEC 1C Frame I/O (serial today; TCP tunnel later).
/// </summary>
public interface IMelsecTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes the full request, then reads raw response bytes until CR (0x0D) or timeout.
    /// Returned buffer includes control characters. One transaction at a time.
    /// </summary>
    Task<byte[]> TransactAsync(
        ReadOnlyMemory<byte> request,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
