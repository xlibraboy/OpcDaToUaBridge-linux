using System.Buffers.Binary;
using System.Globalization;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.S7.Addressing;
using OpcBridge.Drivers.S7.Protocol;
using OpcBridge.Drivers.S7.Transport;

namespace OpcBridge.Drivers.S7;

/// <summary>
/// Siemens S7-200 PPI <see cref="ISourceClient"/> over <see cref="IS7Transport"/>.
/// </summary>
public sealed class S7200Client : ISourceClient
{
    private const int MaxBytesPerBatch = 64;
    private const int DaQualityGood = 0xC0;
    private const int DaQualityBad = 0x00;
    private const short VtBool = 11;
    private const short VtI2 = 2;
    private const short VtI4 = 3;
    private const int AccessRead = 1;
    private const int AccessReadWrite = 3;

    private readonly S7200ClientOptions _options;
    private readonly IS7Transport _transport;
    private readonly ILogger? _logger;
    private readonly bool _ownsTransport;
    private readonly byte _local;
    private readonly byte _remote;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;
    private readonly SemaphoreSlim _io = new(1, 1);
    private ushort _pduNumber = 1;

    private bool _connected;
    private bool _disposed;

    public S7200Client(S7200ClientOptions options, ILogger? logger = null)
        : this(options, CreateSerialTransport(options), logger, ownsTransport: true)
    {
    }

    public S7200Client(S7200ClientOptions options, IS7Transport transport, ILogger? logger = null)
        : this(options, transport, logger, ownsTransport: false)
    {
    }

    private S7200Client(
        S7200ClientOptions options,
        IS7Transport transport,
        ILogger? logger,
        bool ownsTransport)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);

        _options = options;
        _transport = transport;
        _logger = logger;
        _ownsTransport = ownsTransport;

        int local = options.LocalPpiAddress;
        int remote = options.RemotePpiAddress;
        if (local is < 0 or > 126)
        {
            local = 0;
        }

        if (remote is < 0 or > 126)
        {
            remote = 2;
        }

        _local = (byte)local;
        _remote = (byte)remote;

        int timeoutMs = options.TimeoutMs > 0 ? options.TimeoutMs : 3000;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
        int retries = options.RetryCount < 0 ? 2 : options.RetryCount;
        _maxAttempts = Math.Max(1, retries + 1);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_transport.IsOpen)
            {
                await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                // Probe: PDU-length negotiate (libnodave daveConnectPLC / PPI path).
                await ExchangeAsync(
                    PpiFrameCodec.BuildNegotiateRequest(_remote, _local, NextPdu()),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "S7-200 PPI connect probe failed for source {SourceId}", _options.SourceId);
                try
                {
                    await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception closeEx)
                {
                    _logger?.LogDebug(closeEx, "Close after failed S7 probe threw");
                }

                _connected = false;
                throw;
            }

            _connected = true;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task<IReadOnlyList<BridgeValue>> ReadAsync(
        IReadOnlyList<TagMapping> mappings,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mappings);

        if (mappings.Count == 0)
        {
            return Array.Empty<BridgeValue>();
        }

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();

            var results = new BridgeValue[mappings.Count];
            var parsed = new ParsedItem?[mappings.Count];

            for (int i = 0; i < mappings.Count; i++)
            {
                string itemId = mappings[i].ItemId ?? string.Empty;
                if (!S7AddressParser.TryParse(itemId, out S7Address address, out string error))
                {
                    _logger?.LogWarning(
                        "Invalid S7 address '{ItemId}' on source {SourceId}: {Error}",
                        itemId,
                        _options.SourceId,
                        error);
                    results[i] = Bad(itemId);
                    parsed[i] = null;
                    continue;
                }

                parsed[i] = new ParsedItem(i, address.Canonical, address);
            }

            // Simple per-item reads (correctness first). Consecutive batching can come later.
            for (int i = 0; i < mappings.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parsed[i] is not { } item)
                {
                    continue;
                }

                try
                {
                    results[i] = await ReadOneAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or PpiException or IOException)
                {
                    _logger?.LogWarning(ex, "S7 read failed for {ItemId}", item.ItemId);
                    results[i] = Bad(item.ItemId);
                }
            }

            return results;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        if (!S7AddressParser.TryParse(itemId, out S7Address address, out _))
        {
            return false;
        }

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();

            MapArea(address.Area, out byte areaCode, out int dbNumber);

            if (address.BitIndex is int bit)
            {
                bool on = CoerceBool(value);
                byte[] bitData = [(byte)(on ? 0x01 : 0x00)];
                int startBit = address.ByteOffset * 8 + bit;
                byte[] frame = PpiFrameCodec.BuildWriteBitsRequest(
                    _remote,
                    _local,
                    areaCode,
                    dbNumber,
                    startBit,
                    bitCount: 1,
                    bitData,
                    NextPdu());
                byte[] response = await ExchangeAsync(frame, cancellationToken).ConfigureAwait(false);
                PpiFrameCodec.EnsureWriteSuccess(response);
                return true;
            }

            byte[] payload = CoerceBytes(value, address.SizeBytes);
            byte[] writeFrame = PpiFrameCodec.BuildWriteBytesRequest(
                _remote,
                _local,
                areaCode,
                dbNumber,
                address.ByteOffset,
                payload,
                NextPdu());
            byte[] writeResp = await ExchangeAsync(writeFrame, cancellationToken).ConfigureAwait(false);
            PpiFrameCodec.EnsureWriteSuccess(writeResp);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or PpiException or IOException or FormatException or OverflowException or InvalidCastException)
        {
            _logger?.LogWarning(ex, "S7 write failed for {ItemId}", itemId);
            return false;
        }
        finally
        {
            _io.Release();
        }
    }

    public bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights)
    {
        canonicalDataType = null;
        accessRights = null;

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        if (!S7AddressParser.TryParse(itemId, out S7Address address, out _))
        {
            return false;
        }

        if (address.BitIndex is not null)
        {
            canonicalDataType = VtBool;
        }
        else
        {
            canonicalDataType = address.SizeBytes switch
            {
                1 => VtI2,
                2 => VtI2,
                4 => VtI4,
                _ => VtI2
            };
        }

        accessRights = address.Area == S7Area.Inputs ? AccessRead : AccessReadWrite;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connected = false;

        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ownsTransport)
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _io.Release();
            _io.Dispose();
        }
    }

    private async Task<BridgeValue> ReadOneAsync(ParsedItem item, CancellationToken cancellationToken)
    {
        S7Address address = item.Address;
        MapArea(address.Area, out byte areaCode, out int dbNumber);

        int byteCount = address.BitIndex is null ? Math.Max(1, address.SizeBytes) : 1;
        if (byteCount > MaxBytesPerBatch)
        {
            throw new PpiException($"Read size {byteCount} exceeds batch cap {MaxBytesPerBatch}.");
        }

        byte[] request = PpiFrameCodec.BuildReadBytesRequest(
            _remote,
            _local,
            areaCode,
            dbNumber,
            address.ByteOffset,
            byteCount,
            NextPdu());

        byte[] response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] data = PpiFrameCodec.ParseReadResponse(response);

        object? value;
        if (address.BitIndex is int bit)
        {
            if (data.Length < 1)
            {
                throw new PpiException("Empty bit read payload.");
            }

            value = ((data[0] >> bit) & 0x01) != 0;
        }
        else
        {
            value = DecodeSized(data, address.SizeBytes);
        }

        return new BridgeValue(
            _options.SourceId,
            item.ItemId,
            value,
            DateTime.UtcNow,
            DaQualityGood,
            IsGood: true);
    }

    /// <summary>
    /// PPI multi-step exchange (libnodave _daveExchangePPI):
    /// send request → expect E5 → send request-data → read SD2 response to SYN.
    /// </summary>
    private async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _transport.WriteAsync(request, cancellationToken).ConfigureAwait(false);

                byte[] ackBuf = new byte[1];
                int ackN = await _transport.ReadAsync(ackBuf, _timeout, cancellationToken).ConfigureAwait(false);
                if (ackN < 1)
                {
                    throw new TimeoutException("No PPI E5 ack.");
                }

                if (ackBuf[0] == PpiFrameCodec.Nak)
                {
                    throw new PpiException("PPI NAK on request.") { ErrorCode = PpiFrameCodec.Nak };
                }

                if (!PpiFrameCodec.IsAckE5(ackBuf.AsSpan(0, 1)))
                {
                    // Some adapters may skip E5 and return SD2 immediately — handle if SD2.
                    if (ackBuf[0] == PpiFrameCodec.Sd2)
                    {
                        return await ReadSd2ContinuationAsync(ackBuf[0], cancellationToken).ConfigureAwait(false);
                    }

                    throw new PpiException($"Expected PPI E5 ack, got 0x{ackBuf[0]:X2}.");
                }

                byte[] reqData = PpiFrameCodec.BuildRequestDataFrame(_remote, _local, alternate: attempt > 0);
                await _transport.WriteAsync(reqData, cancellationToken).ConfigureAwait(false);

                return await ReadFullSd2Async(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or PpiException or IOException)
            {
                last = ex;
                _logger?.LogDebug(ex, "S7 PPI exchange attempt {Attempt}/{Max} failed", attempt + 1, _maxAttempts);
            }
        }

        throw last ?? new TimeoutException("S7 PPI exchange failed.");
    }

    private async Task<byte[]> ReadFullSd2Async(CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        int total = 0;
        var deadline = DateTime.UtcNow + _timeout;

        while (total < 6)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Timed out reading PPI SD2 header.");
            }

            int n = await _transport.ReadAsync(buffer.AsMemory(total), remaining, cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new TimeoutException("Timed out reading PPI SD2 header.");
            }

            total += n;
        }

        if (buffer[0] != PpiFrameCodec.Sd2)
        {
            throw new PpiException($"Expected SD2 (0x68), got 0x{buffer[0]:X2}.");
        }

        int bodyLen = buffer[1];
        int expected = bodyLen + 6;
        if (expected > buffer.Length)
        {
            throw new PpiException($"PPI frame too large ({expected}).");
        }

        while (total < expected)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Timed out reading PPI SD2 body.");
            }

            int n = await _transport.ReadAsync(buffer.AsMemory(total, expected - total), remaining, cancellationToken)
                .ConfigureAwait(false);
            if (n <= 0)
            {
                throw new TimeoutException("Timed out reading PPI SD2 body.");
            }

            total += n;
        }

        var frame = new byte[expected];
        Buffer.BlockCopy(buffer, 0, frame, 0, expected);
        return frame;
    }

    private async Task<byte[]> ReadSd2ContinuationAsync(byte first, CancellationToken cancellationToken)
    {
        // Rare path: first byte already SD2.
        var prefix = new byte[] { first };
        // Read rest into full frame via ReadFullSd2 but we already consumed first byte.
        // Simpler: push into a local assembler.
        var buffer = new byte[512];
        buffer[0] = first;
        int total = 1;
        var deadline = DateTime.UtcNow + _timeout;

        while (total < 6)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Timed out reading PPI SD2 header.");
            }

            int n = await _transport.ReadAsync(buffer.AsMemory(total), remaining, cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new TimeoutException("Timed out reading PPI SD2 header.");
            }

            total += n;
        }

        int bodyLen = buffer[1];
        int expected = bodyLen + 6;
        while (total < expected)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Timed out reading PPI SD2 body.");
            }

            int n = await _transport.ReadAsync(buffer.AsMemory(total, expected - total), remaining, cancellationToken)
                .ConfigureAwait(false);
            if (n <= 0)
            {
                throw new TimeoutException("Timed out reading PPI SD2 body.");
            }

            total += n;
        }

        var frame = new byte[expected];
        Buffer.BlockCopy(buffer, 0, frame, 0, expected);
        _ = prefix;
        return frame;
    }

    private ushort NextPdu()
    {
        ushort n = _pduNumber;
        _pduNumber = (ushort)(_pduNumber == ushort.MaxValue ? 1 : _pduNumber + 1);
        return n;
    }

    private void EnsureConnected()
    {
        if (!_connected || !_transport.IsOpen)
        {
            throw new InvalidOperationException("S7-200 client is not connected.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private BridgeValue Bad(string itemId) =>
        new(_options.SourceId, itemId, null, DateTime.UtcNow, DaQualityBad, IsGood: false);

    private static void MapArea(S7Area area, out byte areaCode, out int dbNumber)
    {
        switch (area)
        {
            case S7Area.Inputs:
                areaCode = PpiAreas.Inputs;
                dbNumber = 0;
                break;
            case S7Area.Outputs:
                areaCode = PpiAreas.Outputs;
                dbNumber = 0;
                break;
            case S7Area.Flags:
                areaCode = PpiAreas.Flags;
                dbNumber = 0;
                break;
            case S7Area.V:
                areaCode = PpiAreas.DB;
                dbNumber = 1;
                break;
            default:
                throw new PpiException($"Unsupported S7 area {area}.");
        }
    }

    private static object DecodeSized(byte[] data, int sizeBytes)
    {
        if (data.Length < sizeBytes)
        {
            throw new PpiException($"Payload shorter than size: need {sizeBytes}, got {data.Length}.");
        }

        return sizeBytes switch
        {
            1 => data[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)),
            4 => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)),
            _ => throw new PpiException($"Unsupported size {sizeBytes}.")
        };
    }

    private static bool CoerceBool(object? value) =>
        value switch
        {
            null => false,
            bool b => b,
            byte by => by != 0,
            sbyte sb => sb != 0,
            short s => s != 0,
            ushort us => us != 0,
            int i => i != 0,
            uint ui => ui != 0,
            long l => l != 0,
            float f => Math.Abs(f) > float.Epsilon,
            double d => Math.Abs(d) > double.Epsilon,
            string str when bool.TryParse(str, out bool pb) => pb,
            string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pi) => pi != 0,
            IConvertible c => Convert.ToBoolean(c, CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot coerce {value.GetType().Name} to bool.")
        };

    private static byte[] CoerceBytes(object? value, int sizeBytes)
    {
        if (value is byte[] raw)
        {
            if (raw.Length < sizeBytes)
            {
                throw new FormatException("Byte array too short for address size.");
            }

            return raw.AsSpan(0, sizeBytes).ToArray();
        }

        long n = value switch
        {
            null => 0,
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => ui,
            long l => l,
            float f => (long)f,
            double d => (long)d,
            bool bo => bo ? 1 : 0,
            string str => long.Parse(str, NumberStyles.Integer, CultureInfo.InvariantCulture),
            IConvertible c => Convert.ToInt64(c, CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot coerce {value.GetType().Name} to integer.")
        };

        var buf = new byte[sizeBytes];
        switch (sizeBytes)
        {
            case 1:
                buf[0] = (byte)(n & 0xFF);
                break;
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)(n & 0xFFFF));
                break;
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)n);
                break;
            default:
                throw new PpiException($"Unsupported write size {sizeBytes}.");
        }

        return buf;
    }

    private static IS7Transport CreateSerialTransport(S7200ClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SerialPortName))
        {
            throw new InvalidOperationException("SerialPortName is required for S7-200 PPI.");
        }

        return new SerialS7Transport(
            options.SerialPortName,
            options.BaudRate > 0 ? options.BaudRate : 9600,
            options.DataBits is 7 or 8 ? options.DataBits : 8,
            ParseParity(options.Parity),
            ParseStopBits(options.StopBits));
    }

    private static Parity ParseParity(string? parity) =>
        parity?.Trim().ToLowerInvariant() switch
        {
            "even" => Parity.Even,
            "odd" => Parity.Odd,
            "none" => Parity.None,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => Parity.Even
        };

    private static StopBits ParseStopBits(string? stopBits) =>
        stopBits?.Trim().ToLowerInvariant() switch
        {
            "two" or "2" => StopBits.Two,
            _ => StopBits.One
        };

    private sealed record ParsedItem(int Index, string ItemId, S7Address Address);
}
