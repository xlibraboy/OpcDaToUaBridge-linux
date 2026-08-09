namespace OpcBridge.Drivers.MxComponent;

/// <summary>
/// Seam over a MELSOFT MX Component 4 ActUtlType COM session. Mirrors
/// <see cref="OpcBridge.Drivers.Melsec.IMelsecTransport"/> so the client logic
/// (batching, address semantics, retries) is unit-testable without COM.
/// Device names are MELSEC-style ("D100", "M10", "X20"); a block read over bit
/// devices returns one 0/1 element per bit device.
/// </summary>
public interface IMxComponentSession : IAsyncDisposable
{
    bool IsOpen { get; }

    /// <summary>Resolves the ActUtlType COM object, sets the logical station, opens the
    /// communication and probes the CPU type. Throws <see cref="PlatformNotSupportedException"/>
    /// on non-Windows and a descriptive exception when MX Component is not installed/registered.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Closes the ActUtlType communication (best-effort).</summary>
    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>Block read of <paramref name="count"/> consecutive device points starting at
    /// <paramref name="device"/> (words for D devices, 0/1 per point for bit devices).</summary>
    Task<ushort[]> ReadWordsAsync(string device, int count, CancellationToken cancellationToken);

    /// <summary>Block write of <paramref name="words"/> to consecutive device points starting at
    /// <paramref name="device"/>.</summary>
    Task WriteWordsAsync(string device, IReadOnlyList<ushort> words, CancellationToken cancellationToken);

    /// <summary>CPU name/code reported by GetCpuType (connect probe), e.g. "A3NCPU".</summary>
    Task<(string CpuName, string CpuCode)> GetCpuTypeAsync(CancellationToken cancellationToken);
}
