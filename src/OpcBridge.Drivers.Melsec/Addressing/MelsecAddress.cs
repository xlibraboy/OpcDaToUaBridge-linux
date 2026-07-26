using System.Globalization;

namespace OpcBridge.Drivers.Melsec.Addressing;

public sealed record MelsecAddress(
    MelsecDeviceKind Device,
    int Number,
    int? BitIndex,
    string Canonical);

public static class MelsecAddressParser
{
    private const int MaxD = 1023;
    private const int MaxM = 2047;
    private const int MaxXy = 0x7FF;
    private const int MaxBit = 15;

    public static bool TryParse(string? input, out MelsecAddress address, out string error)
    {
        address = null!;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Address is required.";
            return false;
        }

        var raw = input.Trim();
        if (raw.Length < 2)
        {
            error = "Address is too short.";
            return false;
        }

        var deviceChar = char.ToUpperInvariant(raw[0]);
        if (!TryMapDevice(deviceChar, out var device))
        {
            error = $"Unsupported device '{raw[0]}'. Expected D, M, X, or Y.";
            return false;
        }

        var body = raw[1..];
        int? bitIndex = null;

        var bitSep = body.IndexOfAny([':', '.']);
        if (bitSep >= 0)
        {
            var bitText = body[(bitSep + 1)..];
            body = body[..bitSep];

            if (device != MelsecDeviceKind.D)
            {
                error = "Bit-in-word suffix is only allowed on D devices.";
                return false;
            }

            if (bitText.Length == 0
                || !int.TryParse(bitText, NumberStyles.None, CultureInfo.InvariantCulture, out var bit)
                || bit < 0
                || bit > MaxBit)
            {
                error = "Bit index must be an integer from 0 to 15.";
                return false;
            }

            bitIndex = bit;
        }

        if (body.Length == 0)
        {
            error = "Device number is required.";
            return false;
        }
        int number = 0;
        string numberCanonical = "";
        switch (device)
        {
            case MelsecDeviceKind.D:
            case MelsecDeviceKind.M:
                if (!IsAllDecimalDigits(body)
                    || !int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                {
                    error = $"Device number for {device} must be a decimal integer.";
                    return false;
                }

                numberCanonical = number.ToString(CultureInfo.InvariantCulture);
                break;

            case MelsecDeviceKind.X:
            case MelsecDeviceKind.Y:
                if (!TryParseXyNumber(body, out number, out numberCanonical, out var xyError))
                {
                    error = xyError;
                    return false;
                }

                break;

            default:
                error = $"Unsupported device '{device}'.";
                return false;
        }

        if (!IsInRange(device, number, out var rangeError))
        {
            error = rangeError;
            return false;
        }

        var canonical = bitIndex is null
            ? $"{device}{numberCanonical}"
            : $"{device}{numberCanonical}:{bitIndex.Value}";

        address = new MelsecAddress(device, number, bitIndex, canonical);
        return true;
    }

    public static string Canonicalize(string input)
    {
        if (!TryParse(input, out var address, out var error))
        {
            throw new FormatException(error);
        }

        return address.Canonical;
    }

    private static bool TryMapDevice(char deviceChar, out MelsecDeviceKind device)
    {
        switch (deviceChar)
        {
            case 'D':
                device = MelsecDeviceKind.D;
                return true;
            case 'M':
                device = MelsecDeviceKind.M;
                return true;
            case 'X':
                device = MelsecDeviceKind.X;
                return true;
            case 'Y':
                device = MelsecDeviceKind.Y;
                return true;
            default:
                device = default;
                return false;
        }
    }

    private static bool IsAllDecimalDigits(string text)
    {
        foreach (var ch in text)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// X/Y AnN addresses are traditionally octal (digits 0-7). Design examples also use
    /// hex-looking forms such as Y0F (15). Accept pure octal, or hex when A-F appear;
    /// reject 8/9 which are invalid in octal and not used by the brief fixtures.
    /// </summary>
    private static bool TryParseXyNumber(
        string body,
        out int number,
        out string numberCanonical,
        out string error)
    {
        number = 0;
        numberCanonical = "";
        error = "";

        var hasHexLetter = false;
        foreach (var ch in body)
        {
            if (ch is >= '0' and <= '7')
            {
                continue;
            }

            if (ch is '8' or '9')
            {
                error = "X/Y device numbers use octal digits (0-7); 8 and 9 are invalid.";
                return false;
            }

            var upper = char.ToUpperInvariant(ch);
            if (upper is >= 'A' and <= 'F')
            {
                hasHexLetter = true;
                continue;
            }

            error = "X/Y device numbers must be octal/hex digits only.";
            return false;
        }

        if (hasHexLetter)
        {
            if (!int.TryParse(body, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number))
            {
                error = "X/Y device number is not a valid hexadecimal value.";
                return false;
            }

            numberCanonical = number.ToString("X", CultureInfo.InvariantCulture).PadLeft(3, '0');
            return true;
        }

        try
        {
            number = Convert.ToInt32(body, 8);
        }
        catch (Exception)
        {
            error = "X/Y device number is not a valid octal value.";
            return false;
        }

        numberCanonical = Convert.ToString(number, 8).ToUpperInvariant().PadLeft(3, '0');
        return true;
    }

    private static bool IsInRange(MelsecDeviceKind device, int number, out string error)
    {
        error = "";
        switch (device)
        {
            case MelsecDeviceKind.D:
                if (number is < 0 or > MaxD)
                {
                    error = $"D device number must be 0–{MaxD}.";
                    return false;
                }

                return true;
            case MelsecDeviceKind.M:
                if (number is < 0 or > MaxM)
                {
                    error = $"M device number must be 0–{MaxM}.";
                    return false;
                }

                return true;
            case MelsecDeviceKind.X:
            case MelsecDeviceKind.Y:
                if (number is < 0 or > MaxXy)
                {
                    error = $"{device} device number must be 0–0x{MaxXy:X}.";
                    return false;
                }

                return true;
            default:
                error = $"Unsupported device '{device}'.";
                return false;
        }
    }
}
