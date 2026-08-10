using System.Globalization;
using Microsoft.Extensions.Logging;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.Melsec.Addressing;

namespace OpcBridge.Drivers.MxComponent;

/// <summary>
/// MELSEC A3N <see cref="ISourceClient"/> over MELSOFT MX Component 4 (ActUtlType COM).
///
/// The physical link (RS-422/RS-232C or Ethernet, baud, PLC station) is configured inside
/// MX Component's Communication Settings Utility; this client only references a logical
/// station number. Device addresses use the same MELSEC A3N space as the serial driver:
/// <c>D100</c>, <c>M10</c>, <c>X20</c>, <c>Y0F</c>, bit-in-word <c>D100:8</c>.
///
/// Windows-only at runtime (MX Component is a Windows COM component). On non-Windows
/// <see cref="ConnectAsync"/> throws <see cref="PlatformNotSupportedException"/>.
/// </summary>
public sealed class MxComponentClient : ISourceClient
{
    private const int MaxWordsPerBatch = 64;
    private const int MaxBitsPerBatch = 256;
    private const int DaQualityGood = 0xC0;
    private const int DaQualityBad = 0x00;

    // COM VARIANT type codes used by existing DA metadata consumers.
    private const short VtBool = 11; // VT_BOOL
    private const short VtI2 = 2;    // VT_I2 (Int16)
    private const int AccessReadWrite = 3; // OPC DA readable|writeable

    private readonly MxComponentClientOptions _options;
    private readonly IMxComponentSession _session;
    private readonly ILogger? _logger;
    private readonly bool _ownsSession;
    private readonly int _maxAttempts;
    private readonly SemaphoreSlim _io = new(1, 1);

    private bool _connected;
    private bool _disposed;

    /// <summary>Production ctor: builds the ActUtlType COM session from options.</summary>
    public MxComponentClient(MxComponentClientOptions options, ILogger? logger = null)
        : this(options, new ActUtlTypeSession(options, logger), logger, ownsSession: true)
    {
    }

    /// <summary>Test/injection ctor.</summary>
    public MxComponentClient(MxComponentClientOptions options, IMxComponentSession session, ILogger? logger = null)
        : this(options, session, logger, ownsSession: false)
    {
    }

    private MxComponentClient(
        MxComponentClientOptions options,
        IMxComponentSession session,
        ILogger? logger,
        bool ownsSession)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(session);

        _options = options;
        _session = session;
        _logger = logger;
        _ownsSession = ownsSession;

        // The ActUtlType COM calls are synchronous and cannot be cancelled mid-flight;
        // MX Component's own Communication Settings Utility governs link timeouts.
        int retries = options.RetryCount < 0 ? 0 : options.RetryCount;
        _maxAttempts = retries + 1;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected && _session.IsOpen)
            {
                return;
            }

            await ExecuteWithRetryAsync(
                () => _session.ConnectAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

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
                string itemId = mapping.ItemId ?? string.Empty;
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

    public async Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        if (!MelsecAddressParser.TryParse(itemId, out MelsecAddress address, out string error))
        {
            _logger?.LogWarning(
                "Write rejected — invalid MELSEC address '{ItemId}': {Error}",
                itemId,
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

                await ExecuteWithRetryAsync(
                    () => _session.WriteBitAsync(DeviceName(address), bit, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (IsPureDWord(address))
            {
                if (!TryCoerceUInt16(value, out ushort word))
                {
                    return false;
                }

                await ExecuteWithRetryAsync(
                    () => _session.WriteWordsAsync(DeviceName(address), new[] { word }, cancellationToken),
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
                string wordDevice = WordDeviceName(address.Number);

                // RMW: read one word, modify bit, write one word.
                await ExecuteWithRetryAsync(
                    async () =>
                    {
                        ushort[] words = await _session.ReadWordsAsync(wordDevice, 1, cancellationToken)
                            .ConfigureAwait(false);
                        ushort current = words[0];
                        if (bit)
                        {
                            current = (ushort)(current | (1 << bitIndex));
                        }
                        else
                        {
                            current = (ushort)(current & ~(1 << bitIndex));
                        }

                        await _session.WriteWordsAsync(wordDevice, new[] { current }, cancellationToken)
                            .ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "MX Component write failed for {ItemId}", itemId);
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

        if (!MelsecAddressParser.TryParse(itemId, out MelsecAddress address, out _))
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
            await _session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }

        if (_ownsSession)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
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

            // MX Component packs 16 bit devices per element and requires the start
            // device to be a multiple of 16 (Programming Manual §"How to specify
            // devices", ReadDeviceBlock2). Align the request down and mask the
            // addressed bits out of the returned words.
            int baseNumber = (first.Address.Number / 16) * 16;
            int words = ((first.Address.Number - baseNumber) + count + 15) / 16;
            string device = BitDeviceName(first.Address.Device, baseNumber);

            try
            {
                ushort[] data = await ExecuteWithRetryAsync(
                    () => _session.ReadWordsAsync(device, words, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                DateTime ts = DateTime.UtcNow;
                for (int i = 0; i < count; i++)
                {
                    ParsedReadItem item = parsed[batchStart + i]!;
                    int bitIndex = item.Address.Number - baseNumber;
                    bool bit = ((data[bitIndex / 16] >> (bitIndex % 16)) & 1) != 0;
                    results[item.Index] = Good(item.ItemId, bit, ts);
                }
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                _logger?.LogWarning(ex, "MX Component bit read failed for {Device} words {Words}", device, words);
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
            string device = DeviceName(first.Address);

            try
            {
                ushort[] data = await ExecuteWithRetryAsync(
                    () => _session.ReadWordsAsync(device, count, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                DateTime ts = DateTime.UtcNow;
                for (int i = 0; i < count; i++)
                {
                    ParsedReadItem item = parsed[batchStart + i]!;
                    // Int16 view of the register (matches metadata VT_I2).
                    short signed = unchecked((short)data[i]);
                    results[item.Index] = Good(item.ItemId, signed, ts);
                }
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                _logger?.LogWarning(ex, "MX Component word read failed for {Device} count {Count}", device, count);
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
        string wordDevice = WordDeviceName(item.Address.Number);
        int bitIndex = item.Address.BitIndex!.Value;

        try
        {
            ushort[] data = await ExecuteWithRetryAsync(
                () => _session.ReadWordsAsync(wordDevice, 1, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            bool bit = ((data[0] >> bitIndex) & 1) != 0;
            results[item.Index] = Good(item.ItemId, bit, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "MX Component bit-in-word read failed for {ItemId}", item.ItemId);
            results[item.Index] = Bad(item.ItemId, null);
        }
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
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                last = ex;
                if (attempt + 1 >= _maxAttempts)
                {
                    break;
                }

                _logger?.LogDebug(
                    ex,
                    "MX Component I/O attempt {Attempt}/{Max} failed; retrying",
                    attempt + 1,
                    _maxAttempts);
            }
        }

        throw last ?? new TimeoutException("MX Component I/O failed with no exception detail.");
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
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                last = ex;
                if (attempt + 1 >= _maxAttempts)
                {
                    break;
                }

                _logger?.LogDebug(
                    ex,
                    "MX Component I/O attempt {Attempt}/{Max} failed; retrying",
                    attempt + 1,
                    _maxAttempts);
            }
        }

        throw last ?? new TimeoutException("MX Component I/O failed with no exception detail.");
    }

    private BridgeValue Good(string itemId, object? value, DateTime timestampUtc) =>
        new(_options.SourceId, itemId, value, timestampUtc, DaQualityGood, true);

    private BridgeValue Bad(string itemId, object? value) =>
        new(_options.SourceId, itemId, value, DateTime.UtcNow, DaQualityBad, false);

    private void EnsureConnected()
    {
        if (!_connected || !_session.IsOpen)
        {
            throw new InvalidOperationException("MX Component client is not connected.");
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

    /// <summary>MELSEC device name for MX Component, e.g. "D100", "M10", "X20".</summary>
    private static string DeviceName(MelsecAddress address)
    {
        // Canonical is already e.g. "D100" / "M10" / "X020"; MX Component accepts leading zeros.
        return address.Canonical;
    }

    /// <summary>Device name for a 16-aligned bit base number (M/X/Y). X/Y are octal in
    /// the AnN series (Programming Manual §"Device Types"), so the base number is
    /// formatted back as octal, matching the canonical form of parsed X/Y addresses.</summary>
    private static string BitDeviceName(MelsecDeviceKind device, int baseNumber)
    {
        return device switch
        {
            MelsecDeviceKind.M => "M" + baseNumber.ToString(CultureInfo.InvariantCulture),
            MelsecDeviceKind.X => "X" + Convert.ToString(baseNumber, 8).ToUpperInvariant().PadLeft(3, '0'),
            MelsecDeviceKind.Y => "Y" + Convert.ToString(baseNumber, 8).ToUpperInvariant().PadLeft(3, '0'),
            _ => throw new ArgumentOutOfRangeException(nameof(device), device, "Unsupported bit device kind.")
        };
    }

    /// <summary>Plain word device for a D register (canonical without any :bit suffix).</summary>
    private static string WordDeviceName(int dNumber) => $"D{dNumber}";

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

    private sealed record ParsedReadItem(int Index, string ItemId, MelsecAddress Address);
}
