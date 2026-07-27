using System.Globalization;
using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.Melsec.Addressing;
using OpcBridge.Drivers.Melsec.Protocol;
using OpcBridge.Drivers.Melsec.Transport;

namespace OpcBridge.Drivers.Melsec;

/// <summary>
/// MELSEC A3N (1C Frame / ACPU common) <see cref="IDaClient"/> over <see cref="IMelsecTransport"/>.
/// </summary>
public sealed class MelsecA3nClient : IDaClient
{
    private const int MaxWordsPerBatch = 64;
    private const int MaxBitsPerBatch = 256;
    private const int DaQualityGood = 0xC0;
    private const int DaQualityBad = 0x00;

    // COM VARIANT type codes used by existing DA metadata consumers.
    private const short VtBool = 11; // VT_BOOL
    private const short VtI2 = 2;    // VT_I2 (Int16)
    private const int AccessReadWrite = 3; // OPC DA readable|writeable

    private readonly MelsecA3nClientOptions _options;
    private readonly IMelsecTransport _transport;
    private readonly ILogger? _logger;
    private readonly bool _ownsTransport;
    private readonly string _station;
    private readonly string _pc;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;
    private readonly SemaphoreSlim _io = new(1, 1);

    private bool _connected;
    private bool _disposed;

    /// <summary>Production ctor: builds <see cref="SerialMelsecTransport"/> from options.</summary>
    public MelsecA3nClient(MelsecA3nClientOptions options, ILogger? logger = null)
        : this(options, CreateSerialTransport(options), logger, ownsTransport: true)
    {
    }

    /// <summary>Test/injection ctor.</summary>
    public MelsecA3nClient(MelsecA3nClientOptions options, IMelsecTransport transport, ILogger? logger = null)
        : this(options, transport, logger, ownsTransport: false)
    {
    }

    private MelsecA3nClient(
        MelsecA3nClientOptions options,
        IMelsecTransport transport,
        ILogger? logger,
        bool ownsTransport)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);

        _options = options;
        _transport = transport;
        _logger = logger;
        _ownsTransport = ownsTransport;

        _station = NormalizeTwoChar(options.StationNo, "00");
        _pc = NormalizeTwoChar(options.PcNo, "FF");
        int timeoutMs = options.TimeoutMs > 0 ? options.TimeoutMs : 3000;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
        int retries = options.RetryCount < 0 ? 0 : options.RetryCount;
        _maxAttempts = retries + 1;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected && _transport.IsOpen)
            {
                return;
            }

            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Lightweight probe: word-read D0 (1 word) with retries.
                await ExecuteWithRetryAsync(
                    () => WordReadAsync("D0000", 1, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MELSEC A3N connect probe failed for source {SourceId}", _options.SourceId);
                try
                {
                    await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception closeEx)
                {
                    _logger?.LogDebug(closeEx, "Close after failed probe threw");
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
            var parsed = new ParsedReadItem?[mappings.Count];

            for (int i = 0; i < mappings.Count; i++)
            {
                TagMapping mapping = mappings[i];
                string itemId = mapping.DaItemId ?? string.Empty;
                if (!MelsecAddressParser.TryParse(itemId, out MelsecAddress address, out string error))
                {
                    _logger?.LogWarning(
                        "Invalid MELSEC address '{ItemId}' on source {SourceId}: {Error}",
                        itemId,
                        _options.SourceId,
                        error);
                    results[i] = Bad(itemId, null);
                    parsed[i] = null;
                    continue;
                }

                parsed[i] = new ParsedReadItem(i, itemId, address);
            }

            // Walk in order; batch consecutive pure bits / pure D words; bit-in-word solo.
            int index = 0;
            while (index < mappings.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ParsedReadItem? item = parsed[index];
                if (item is null)
                {
                    index++;
                    continue;
                }

                MelsecAddress addr = item.Address;
                if (IsPureBit(addr))
                {
                    int end = index + 1;
                    while (end < mappings.Count
                           && parsed[end] is { } next
                           && IsPureBit(next.Address)
                           && next.Address.Device == addr.Device
                           && next.Address.Number == addr.Number + (end - index))
                    {
                        end++;
                    }

                    await ReadBitRunAsync(parsed, results, index, end, cancellationToken).ConfigureAwait(false);
                    index = end;
                }
                else if (IsPureDWord(addr))
                {
                    int end = index + 1;
                    while (end < mappings.Count
                           && parsed[end] is { } next
                           && IsPureDWord(next.Address)
                           && next.Address.Number == addr.Number + (end - index))
                    {
                        end++;
                    }

                    await ReadWordRunAsync(parsed, results, index, end, cancellationToken).ConfigureAwait(false);
                    index = end;
                }
                else if (IsBitInWord(addr))
                {
                    await ReadBitInWordAsync(item, results, cancellationToken).ConfigureAwait(false);
                    index++;
                }
                else
                {
                    // Defensive: unsupported shape
                    results[item.Index] = Bad(item.ItemId, null);
                    index++;
                }
            }

            return results;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task<bool> WriteAsync(string daItemId, object? value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(daItemId))
        {
            return false;
        }

        if (!MelsecAddressParser.TryParse(daItemId, out MelsecAddress address, out string error))
        {
            _logger?.LogWarning(
                "Write rejected — invalid MELSEC address '{ItemId}': {Error}",
                daItemId,
                error);
            return false;
        }

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();

            if (IsPureBit(address))
            {
                if (!TryCoerceBool(value, out bool bit))
                {
                    return false;
                }

                string head = Melsec1CDeviceCodes.FormatHead(address);
                string body = Melsec1CCommands.BuildBitWriteBody(head, bit ? "1" : "0");
                byte[] request = Melsec1CFrameCodec.BuildRequest(_station, _pc, "BW", body);
                await ExecuteWithRetryAsync(
                    async () =>
                    {
                        byte[] response = await _transport.TransactAsync(request, _timeout, cancellationToken)
                            .ConfigureAwait(false);
                        Melsec1CFrameCodec.EnsureAckOrThrow(response);
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (IsPureDWord(address))
            {
                if (!TryCoerceUInt16(value, out ushort word))
                {
                    return false;
                }

                string head = Melsec1CDeviceCodes.FormatHead(address);
                string body = Melsec1CCommands.BuildWordWriteBody(head, new[] { word });
                byte[] request = Melsec1CFrameCodec.BuildRequest(_station, _pc, "WW", body);
                await ExecuteWithRetryAsync(
                    async () =>
                    {
                        byte[] response = await _transport.TransactAsync(request, _timeout, cancellationToken)
                            .ConfigureAwait(false);
                        Melsec1CFrameCodec.EnsureAckOrThrow(response);
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (IsBitInWord(address))
            {
                if (!TryCoerceBool(value, out bool bit))
                {
                    return false;
                }

                int bitIndex = address.BitIndex!.Value;
                string head = Melsec1CDeviceCodes.FormatHead(
                    new MelsecAddress(MelsecDeviceKind.D, address.Number, null, address.Canonical));

                // RMW: WR one word, modify bit, WW one word.
                await ExecuteWithRetryAsync(
                    async () =>
                    {
                        string readBody = Melsec1CCommands.BuildWordReadBody(head, 1);
                        byte[] readReq = Melsec1CFrameCodec.BuildRequest(_station, _pc, "WR", readBody);
                        byte[] readResp = await _transport.TransactAsync(readReq, _timeout, cancellationToken)
                            .ConfigureAwait(false);
                        string data = Melsec1CFrameCodec.ParseDataResponse(readResp);
                        ushort[] words = Melsec1CCommands.ParseWordReadData(data, 1);
                        ushort current = words[0];
                        if (bit)
                        {
                            current = (ushort)(current | (1 << bitIndex));
                        }
                        else
                        {
                            current = (ushort)(current & ~(1 << bitIndex));
                        }

                        string writeBody = Melsec1CCommands.BuildWordWriteBody(head, new[] { current });
                        byte[] writeReq = Melsec1CFrameCodec.BuildRequest(_station, _pc, "WW", writeBody);
                        byte[] writeResp = await _transport.TransactAsync(writeReq, _timeout, cancellationToken)
                            .ConfigureAwait(false);
                        Melsec1CFrameCodec.EnsureAckOrThrow(writeResp);
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is TimeoutException or MelsecProtocolException)
        {
            _logger?.LogWarning(ex, "MELSEC write failed for {ItemId}", daItemId);
            return false;
        }
        finally
        {
            _io.Release();
        }
    }

    public bool TryGetTagMetadata(string daItemId, out short? canonicalDataType, out int? accessRights)
    {
        canonicalDataType = null;
        accessRights = null;

        if (string.IsNullOrWhiteSpace(daItemId))
        {
            return false;
        }

        if (!MelsecAddressParser.TryParse(daItemId, out MelsecAddress address, out _))
        {
            return false;
        }

        if (IsPureBit(address) || IsBitInWord(address))
        {
            canonicalDataType = VtBool;
        }
        else if (IsPureDWord(address))
        {
            canonicalDataType = VtI2;
        }
        else
        {
            return false;
        }

        accessRights = AccessReadWrite;
        return true;
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
            await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }

        if (_ownsTransport)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        _io.Dispose();
        _connected = false;
    }

    private async Task ReadBitRunAsync(
        ParsedReadItem?[] parsed,
        BridgeValue[] results,
        int start,
        int end,
        CancellationToken cancellationToken)
    {
        int total = end - start;
        int offset = 0;
        while (offset < total)
        {
            int count = Math.Min(MaxBitsPerBatch, total - offset);
            int batchStart = start + offset;
            ParsedReadItem first = parsed[batchStart]!;
            string head = Melsec1CDeviceCodes.FormatHead(first.Address);

            try
            {
                string data = await ExecuteWithRetryAsync(
                    async () =>
                    {
                        string body = Melsec1CCommands.BuildBitReadBody(head, count);
                        byte[] request = Melsec1CFrameCodec.BuildRequest(_station, _pc, "BR", body);
                        byte[] response = await _transport.TransactAsync(request, _timeout, cancellationToken)
                            .ConfigureAwait(false);
                        return Melsec1CFrameCodec.ParseDataResponse(response);
                    },
                    cancellationToken).ConfigureAwait(false);

                bool[] bits = Melsec1CCommands.ParseBitReadData(data, count);
                DateTime ts = DateTime.UtcNow;
                for (int i = 0; i < count; i++)
                {
                    ParsedReadItem item = parsed[batchStart + i]!;
                    results[item.Index] = Good(item.ItemId, bits[i], ts);
                }
            }
            catch (Exception ex) when (ex is TimeoutException or MelsecProtocolException)
            {
                _logger?.LogWarning(ex, "MELSEC bit read failed for head {Head} count {Count}", head, count);
                for (int i = 0; i < count; i++)
                {
                    ParsedReadItem item = parsed[batchStart + i]!;
                    results[item.Index] = Bad(item.ItemId, null);
                }
            }

            offset += count;
        }
    }

    private async Task ReadWordRunAsync(
        ParsedReadItem?[] parsed,
        BridgeValue[] results,
        int start,
        int end,
        CancellationToken cancellationToken)
    {
        int total = end - start;
        int offset = 0;
        while (offset < total)
        {
            int count = Math.Min(MaxWordsPerBatch, total - offset);
            int batchStart = start + offset;
            ParsedReadItem first = parsed[batchStart]!;
            string head = Melsec1CDeviceCodes.FormatHead(first.Address);

            try
            {
                string data = await ExecuteWithRetryAsync(
                    async () =>
                    {
                        string body = Melsec1CCommands.BuildWordReadBody(head, count);
                        byte[] request = Melsec1CFrameCodec.BuildRequest(_station, _pc, "WR", body);
                        byte[] response = await _transport.TransactAsync(request, _timeout, cancellationToken)
                            .ConfigureAwait(false);
                        return Melsec1CFrameCodec.ParseDataResponse(response);
                    },
                    cancellationToken).ConfigureAwait(false);

                ushort[] words = Melsec1CCommands.ParseWordReadData(data, count);
                DateTime ts = DateTime.UtcNow;
                for (int i = 0; i < count; i++)
                {
                    ParsedReadItem item = parsed[batchStart + i]!;
                    // Int16 view of the register (matches metadata VT_I2).
                    short signed = unchecked((short)words[i]);
                    results[item.Index] = Good(item.ItemId, signed, ts);
                }
            }
            catch (Exception ex) when (ex is TimeoutException or MelsecProtocolException)
            {
                _logger?.LogWarning(ex, "MELSEC word read failed for head {Head} count {Count}", head, count);
                for (int i = 0; i < count; i++)
                {
                    ParsedReadItem item = parsed[batchStart + i]!;
                    results[item.Index] = Bad(item.ItemId, null);
                }
            }

            offset += count;
        }
    }

    private async Task ReadBitInWordAsync(
        ParsedReadItem item,
        BridgeValue[] results,
        CancellationToken cancellationToken)
    {
        MelsecAddress wordAddress = new(MelsecDeviceKind.D, item.Address.Number, null, item.Address.Canonical);
        string head = Melsec1CDeviceCodes.FormatHead(wordAddress);
        int bitIndex = item.Address.BitIndex!.Value;

        try
        {
            string data = await ExecuteWithRetryAsync(
                async () =>
                {
                    string body = Melsec1CCommands.BuildWordReadBody(head, 1);
                    byte[] request = Melsec1CFrameCodec.BuildRequest(_station, _pc, "WR", body);
                    byte[] response = await _transport.TransactAsync(request, _timeout, cancellationToken)
                        .ConfigureAwait(false);
                    return Melsec1CFrameCodec.ParseDataResponse(response);
                },
                cancellationToken).ConfigureAwait(false);

            ushort[] words = Melsec1CCommands.ParseWordReadData(data, 1);
            bool bit = ((words[0] >> bitIndex) & 1) != 0;
            results[item.Index] = Good(item.ItemId, bit, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is TimeoutException or MelsecProtocolException)
        {
            _logger?.LogWarning(ex, "MELSEC bit-in-word read failed for {ItemId}", item.ItemId);
            results[item.Index] = Bad(item.ItemId, null);
        }
    }

    private async Task<string> WordReadAsync(string head, int wordCount, CancellationToken cancellationToken)
    {
        string body = Melsec1CCommands.BuildWordReadBody(head, wordCount);
        byte[] request = Melsec1CFrameCodec.BuildRequest(_station, _pc, "WR", body);
        byte[] response = await _transport.TransactAsync(request, _timeout, cancellationToken).ConfigureAwait(false);
        return Melsec1CFrameCodec.ParseDataResponse(response);
    }

    private async Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or MelsecProtocolException)
            {
                last = ex;
                if (attempt + 1 >= _maxAttempts)
                {
                    break;
                }

                _logger?.LogDebug(
                    ex,
                    "MELSEC I/O attempt {Attempt}/{Max} failed; retrying",
                    attempt + 1,
                    _maxAttempts);
            }
        }

        throw last ?? new TimeoutException("MELSEC I/O failed with no exception detail.");
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or MelsecProtocolException)
            {
                last = ex;
                if (attempt + 1 >= _maxAttempts)
                {
                    break;
                }

                _logger?.LogDebug(
                    ex,
                    "MELSEC I/O attempt {Attempt}/{Max} failed; retrying",
                    attempt + 1,
                    _maxAttempts);
            }
        }

        throw last ?? new TimeoutException("MELSEC I/O failed with no exception detail.");
    }

    private BridgeValue Good(string itemId, object? value, DateTime timestampUtc) =>
        new(_options.SourceId, itemId, value, timestampUtc, DaQualityGood, true);

    private BridgeValue Bad(string itemId, object? value) =>
        new(_options.SourceId, itemId, value, DateTime.UtcNow, DaQualityBad, false);

    private void EnsureConnected()
    {
        if (!_connected || !_transport.IsOpen)
        {
            throw new InvalidOperationException("MELSEC A3N client is not connected.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool IsPureBit(MelsecAddress address) =>
        address.BitIndex is null
        && address.Device is MelsecDeviceKind.M or MelsecDeviceKind.X or MelsecDeviceKind.Y;

    private static bool IsPureDWord(MelsecAddress address) =>
        address.Device == MelsecDeviceKind.D && address.BitIndex is null;

    private static bool IsBitInWord(MelsecAddress address) =>
        address.Device == MelsecDeviceKind.D && address.BitIndex is not null;

    private static bool TryCoerceBool(object? value, out bool bit)
    {
        bit = false;
        switch (value)
        {
            case null:
                return false;
            case bool b:
                bit = b;
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                bit = Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
                return true;
            case float f:
                bit = Math.Abs(f) > float.Epsilon;
                return true;
            case double d:
                bit = Math.Abs(d) > double.Epsilon;
                return true;
            case string s:
                if (bool.TryParse(s, out bit))
                {
                    return true;
                }

                if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    bit = n != 0;
                    return true;
                }

                if (string.Equals(s.Trim(), "1", StringComparison.Ordinal)
                    || string.Equals(s.Trim(), "on", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    bit = true;
                    return true;
                }

                if (string.Equals(s.Trim(), "0", StringComparison.Ordinal)
                    || string.Equals(s.Trim(), "off", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                {
                    bit = false;
                    return true;
                }

                return false;
            default:
                try
                {
                    bit = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    return false;
                }
        }
    }

    private static bool TryCoerceUInt16(object? value, out ushort word)
    {
        word = 0;
        switch (value)
        {
            case null:
                return false;
            case bool b:
                word = b ? (ushort)1 : (ushort)0;
                return true;
            case byte by:
                word = by;
                return true;
            case sbyte sb:
                word = unchecked((ushort)sb);
                return true;
            case short sh:
                word = unchecked((ushort)sh);
                return true;
            case ushort us:
                word = us;
                return true;
            case int i when i is >= 0 and <= ushort.MaxValue:
                word = (ushort)i;
                return true;
            case int i:
                word = unchecked((ushort)i);
                return true;
            case uint ui when ui <= ushort.MaxValue:
                word = (ushort)ui;
                return true;
            case long l:
                word = unchecked((ushort)l);
                return true;
            case float or double:
                try
                {
                    word = Convert.ToUInt16(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    return false;
                }
            case string s:
                s = s.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return ushort.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out word);
                }

                if (ushort.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out word))
                {
                    return true;
                }

                if (short.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out short signed))
                {
                    word = unchecked((ushort)signed);
                    return true;
                }

                if (bool.TryParse(s, out bool b2))
                {
                    word = b2 ? (ushort)1 : (ushort)0;
                    return true;
                }

                return false;
            default:
                try
                {
                    word = Convert.ToUInt16(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    return false;
                }
        }
    }

    private static string NormalizeTwoChar(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string t = value.Trim().ToUpperInvariant();
        if (t.Length == 1)
        {
            return "0" + t;
        }

        if (t.Length >= 2)
        {
            return t[..2];
        }

        return fallback;
    }

    private static SerialMelsecTransport CreateSerialTransport(MelsecA3nClientOptions options)
    {
        Parity parity = ParseParity(options.Parity);
        StopBits stopBits = ParseStopBits(options.StopBits);
        int baud = options.BaudRate > 0 ? options.BaudRate : 9600;
        int dataBits = options.DataBits is 7 or 8 ? options.DataBits : 8;
        return new SerialMelsecTransport(
            options.SerialPortName,
            baud,
            dataBits,
            parity,
            stopBits);
    }

    private static Parity ParseParity(string? parity) =>
        (parity?.Trim().ToUpperInvariant()) switch
        {
            "NONE" => Parity.None,
            "EVEN" => Parity.Even,
            "MARK" => Parity.Mark,
            "SPACE" => Parity.Space,
            _ => Parity.Odd
        };

    private static StopBits ParseStopBits(string? stopBits) =>
        (stopBits?.Trim().ToUpperInvariant()) switch
        {
            "TWO" or "2" => StopBits.Two,
            _ => StopBits.One
        };

    private sealed record ParsedReadItem(int Index, string ItemId, MelsecAddress Address);
}
