using System.IO.Ports;

namespace OpcBridge.Drivers.S7.Transport;

/// <summary>
/// <see cref="IS7Transport"/> over <see cref="SerialPort"/> with single-flight I/O.
/// </summary>
public sealed class SerialS7Transport : IS7Transport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly int _dataBits;
    private readonly Parity _parity;
    private readonly StopBits _stopBits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _portLock = new();

    private SerialPort? _port;
    private bool _disposed;

    public SerialS7Transport(
        string portName,
        int baudRate,
        int dataBits,
        Parity parity,
        StopBits stopBits)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Serial port name is required.", nameof(portName));
        }

        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), baudRate, "Baud rate must be positive.");
        }

        if (dataBits is not (7 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(dataBits), dataBits, "Data bits must be 7 or 8.");
        }

        if (stopBits is StopBits.None or StopBits.OnePointFive)
        {
            throw new ArgumentOutOfRangeException(nameof(stopBits), stopBits, "Stop bits must be One or Two.");
        }

        _portName = portName.Trim();
        _baudRate = baudRate;
        _dataBits = dataBits;
        _parity = parity;
        _stopBits = stopBits;
    }

    public bool IsOpen
    {
        get
        {
            lock (_portLock)
            {
                return _port is { IsOpen: true };
            }
        }
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsOpen)
            {
                return;
            }

            var port = CreatePort();
            try
            {
                await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                port.Dispose();
                throw;
            }

            lock (_portLock)
            {
                _port?.Dispose();
                _port = port;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClosePortCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SerialPort port = RequireOpenPort();

            if (data.IsEmpty)
            {
                return;
            }

            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            var buffer = data.ToArray();
            port.WriteTimeout = 3000;
            await Task.Run(() => port.Write(buffer, 0, buffer.Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
        {
            return 0;
        }

        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SerialPort port = RequireOpenPort();

            var deadline = timeout == TimeSpan.Zero
                ? DateTime.UtcNow
                : DateTime.UtcNow + timeout;

            int total = 0;
            while (total < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    if (total == 0)
                    {
                        throw new TimeoutException("Timed out waiting for PPI serial data.");
                    }

                    break;
                }

                int ms = remaining.TotalMilliseconds > int.MaxValue
                    ? int.MaxValue
                    : Math.Max(1, (int)remaining.TotalMilliseconds);
                port.ReadTimeout = ms;

                try
                {
                    int n = await Task.Run(
                            () =>
                            {
                                var tmp = new byte[buffer.Length - total];
                                int read = port.Read(tmp, 0, tmp.Length);
                                if (read > 0)
                                {
                                    tmp.AsSpan(0, read).CopyTo(buffer.Span[total..]);
                                }

                                return read;
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (n <= 0)
                    {
                        if (total == 0)
                        {
                            throw new TimeoutException("Timed out waiting for PPI serial data.");
                        }

                        break;
                    }

                    total += n;
                }
                catch (TimeoutException) when (total > 0)
                {
                    break;
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException("Timed out waiting for PPI serial data.", ex);
                }
            }

            return total;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ClosePortCore();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private SerialPort RequireOpenPort()
    {
        lock (_portLock)
        {
            if (_port is not { IsOpen: true })
            {
                throw new InvalidOperationException("S7 serial transport is not open.");
            }

            return _port;
        }
    }

    private SerialPort CreatePort() =>
        new(_portName, _baudRate, _parity, _dataBits, _stopBits)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };

    private void ClosePortCore()
    {
        lock (_portLock)
        {
            if (_port is null)
            {
                return;
            }

            try
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                }
            }
            catch
            {
                // ignore close errors
            }

            _port.Dispose();
            _port = null;
        }
    }
}
