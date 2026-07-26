using System.IO.Ports;
using System.Text;

namespace OpcBridge.Drivers.Melsec.Transport;

/// <summary>
/// <see cref="IMelsecTransport"/> over <see cref="SerialPort"/> with single-flight transactions.
/// </summary>
public sealed class SerialMelsecTransport : IMelsecTransport
{
    private const byte Cr = 0x0D;

    private readonly string _portName;
    private readonly int _baudRate;
    private readonly int _dataBits;
    private readonly Parity _parity;
    private readonly StopBits _stopBits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _portLock = new();

    private SerialPort? _port;
    private bool _disposed;

    public SerialMelsecTransport(
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

    public async Task<byte[]> TransactAsync(
        ReadOnlyMemory<byte> request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be non-negative.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            SerialPort port;
            lock (_portLock)
            {
                if (_port is not { IsOpen: true })
                {
                    throw new InvalidOperationException("MELSEC serial transport is not open.");
                }

                port = _port;
            }

            var deadline = timeout == TimeSpan.Zero
                ? DateTime.UtcNow
                : DateTime.UtcNow + timeout;

            // Drop stale RX before writing so a previous partial frame cannot poison this transaction.
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            if (!request.IsEmpty)
            {
                ApplyWriteTimeout(port, deadline);
                var buffer = request.ToArray();
                await Task.Run(
                        () => port.Write(buffer, 0, buffer.Length),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            using var response = new MemoryStream(capacity: 256);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Timed out waiting for MELSEC response CR.");
                }

                ApplyReadTimeout(port, remaining);

                int value;
                try
                {
                    value = await Task.Run(port.ReadByte, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException("Timed out waiting for MELSEC response CR.", ex);
                }

                if (value < 0)
                {
                    throw new EndOfStreamException("Serial port closed while reading MELSEC response.");
                }

                var b = (byte)value;
                response.WriteByte(b);
                if (b == Cr)
                {
                    break;
                }
            }

            return response.ToArray();
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

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClosePortCore();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private SerialPort CreatePort()
    {
        return new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
        {
            Handshake = Handshake.None,
            Encoding = Encoding.ASCII,
            DtrEnable = true,
            RtsEnable = true,
            ReadBufferSize = 4096,
            WriteBufferSize = 4096,
        };
    }

    private void ClosePortCore()
    {
        SerialPort? port;
        lock (_portLock)
        {
            port = _port;
            _port = null;
        }

        if (port is null)
        {
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    private static void ApplyWriteTimeout(SerialPort port, DateTime deadlineUtc)
    {
        var remainingMs = (int)Math.Ceiling((deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
        port.WriteTimeout = remainingMs <= 0 ? 1 : remainingMs;
    }

    private static void ApplyReadTimeout(SerialPort port, TimeSpan remaining)
    {
        var remainingMs = (int)Math.Ceiling(remaining.TotalMilliseconds);
        port.ReadTimeout = remainingMs <= 0 ? 1 : remainingMs;
    }
}
