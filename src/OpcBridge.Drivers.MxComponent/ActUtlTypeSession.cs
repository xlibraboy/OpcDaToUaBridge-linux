using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace OpcBridge.Drivers.MxComponent;

/// <summary>
/// <see cref="IMxComponentSession"/> over MELSOFT MX Component 4's <c>ActUtlType</c> COM object,
/// late-bound via <see cref="Type.GetTypeFromProgID(string)"/> (no compile-time type library).
///
/// The connection itself (serial/Ethernet port, protocol, baud, PLC station) is configured once
/// in MX Component's Communication Settings Utility, which assigns a logical station number;
/// this session only sets <c>ActLogicalStationNumber</c> and calls <c>Open()</c>.
///
/// Windows-only: MX Component is a Windows COM component. On non-Windows every entry point
/// throws <see cref="PlatformNotSupportedException"/> (same pattern as <c>OpcDaClient</c>).
/// </summary>
internal sealed class ActUtlTypeSession : IMxComponentSession
{
    // ProgID of the ActUtlType coclass as registered by the MX Component 4 installer.
    private const string ActUtlTypeProgId = "MITSUBISHI.ActUtlType.1";

    private readonly MxComponentClientOptions _options;
    private readonly ILogger? _logger;
    private object? _com;
    private bool _open;
    private bool _disposed;

    public ActUtlTypeSession(MxComponentClientOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
    }

    public bool IsOpen => _open;

    [SupportedOSPlatform("windows")]
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "MX Component 4 (ActUtlType) requires Windows. Install MELSOFT MX Component 4 on a Windows host and configure a logical station in its Communication Settings Utility.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_open)
        {
            return Task.CompletedTask;
        }

        Type? type = Type.GetTypeFromProgID(ActUtlTypeProgId);
        if (type is null)
        {
            throw new InvalidOperationException(
                $"MX Component 4 is not registered on this machine (ProgID '{ActUtlTypeProgId}' not found). " +
                "Install MELSOFT MX Component 4 and configure a logical station in its Communication Settings Utility.");
        }

        object com = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Failed to create the '{ActUtlTypeProgId}' COM object.");

        _com = com;
        try
        {
            dynamic act = com;
            act.ActLogicalStationNumber = _options.LogicalStationNumber;
            int rc = act.Open();
            if (rc != 0)
            {
                throw new InvalidOperationException(
                    $"MX Component Open failed with error code {rc} (0x{rc:X8}). " +
                    $"Verify logical station {_options.LogicalStationNumber} is configured in the MX Component Communication Settings Utility and the PLC is reachable.");
            }

            // Probe: reading the CPU type proves the link is live (like the DA/serial probes).
            _ = GetCpuTypeCore(act);
            _open = true;
            _logger?.LogInformation(
                "MX Component session opened (logical station {Station})",
                _options.LogicalStationNumber);
        }
        catch
        {
            TryCloseCore(com);
            _com = null;
            throw;
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        TryCloseCore(_com);
        _open = false;
        _com = null;
        return Task.CompletedTask;
    }

    public Task<ushort[]> ReadWordsAsync(string device, int count, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfNotWindows();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        if (string.IsNullOrWhiteSpace(device))
        {
            throw new ArgumentException("Device is required.", nameof(device));
        }

        if (count <= 0)
        {
            return Task.FromResult(Array.Empty<ushort>());
        }

        short[] data = new short[count];
        int rc = Call(() => ((dynamic)_com!).ReadDeviceBlock(device, count, ref data[0]));
        if (rc != 0)
        {
            throw MxError("ReadDeviceBlock", rc, device);
        }

        var result = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = unchecked((ushort)data[i]);
        }

        return Task.FromResult(result);
    }

    public Task WriteWordsAsync(string device, IReadOnlyList<ushort> words, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfNotWindows();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        if (string.IsNullOrWhiteSpace(device))
        {
            throw new ArgumentException("Device is required.", nameof(device));
        }

        if (words is null || words.Count == 0)
        {
            return Task.CompletedTask;
        }

        var data = new short[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            data[i] = unchecked((short)words[i]);
        }

        int rc = Call(() => ((dynamic)_com!).WriteDeviceBlock(device, data.Length, ref data[0]));
        if (rc != 0)
        {
            throw MxError("WriteDeviceBlock", rc, device);
        }

        return Task.CompletedTask;
    }

    public Task<(string CpuName, string CpuCode)> GetCpuTypeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfNotWindows();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        var cpu = GetCpuTypeCore((dynamic)_com!);
        return Task.FromResult(cpu);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    private (string CpuName, string CpuCode) GetCpuTypeCore(dynamic act)
    {
        string cpuName = string.Empty;
        string cpuCode = string.Empty;
        int rc = Call(() => act.GetCpuType(out cpuName, out cpuCode));
        if (rc != 0)
        {
            throw MxError("GetCpuType", rc, null);
        }

        _logger?.LogDebug("MX Component CPU type: {CpuName} ({CpuCode})", cpuName, cpuCode);
        return (cpuName, cpuCode);
    }

    private static int Call(Func<int> comCall)
    {
        try
        {
            return comCall();
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                $"MX Component COM call failed (HRESULT 0x{ex.HResult:X8}): {ex.Message}", ex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"MX Component COM call failed: {ex.Message}", ex);
        }
    }

    private static InvalidOperationException MxError(string operation, int rc, string? device)
    {
        string target = string.IsNullOrWhiteSpace(device) ? string.Empty : $" on '{device}'";
        return new InvalidOperationException(
            $"MX Component {operation}{target} failed with error code {rc} (0x{rc:X8}). " +
            "Verify the logical station in the MX Component Communication Settings Utility and the PLC connection.");
    }

    private static void TryCloseCore(object? com)
    {
        if (com is null)
        {
            return;
        }

        try
        {
            ((dynamic)com).Close();
        }
        catch
        {
            // best-effort
        }
    }

    private void EnsureOpen()
    {
        if (!_open || _com is null)
        {
            throw new InvalidOperationException("MX Component session is not open.");
        }
    }

    private void ThrowIfNotWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("MX Component 4 (ActUtlType) requires Windows.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
