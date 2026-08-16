using System.Globalization;

namespace OpcBridge.Drivers.S7.Addressing;

public static class S7AddressParser
{
    public static bool TryParse(string? input, out S7Address address, out string error)
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

        var upper = raw.ToUpperInvariant();
        var areaChar = upper[0];
        if (!TryMapArea(areaChar, out var area, out var areaLetter))
        {
            error = $"Unsupported area '{raw[0]}'. Expected I, Q, M, or V.";
            return false;
        }

        var rest = upper[1..];

        // Bit form: I0.0 / Q1.7 / M10.2 (no size letter; requires '.' and bit)
        var dot = rest.IndexOf('.');
        if (dot >= 0)
        {
            if (area == S7Area.V)
            {
                error = "Bit addressing is not supported on V area; use VB/VW/VD.";
                return false;
            }

            // Sized forms never use bit suffix (IB0.0 is invalid here).
            if (rest.Length > 0 && IsSizeLetter(rest[0]))
            {
                error = "Sized forms (B/W/D) cannot include a bit index.";
                return false;
            }

            var byteText = rest[..dot];
            var bitText = rest[(dot + 1)..];

            if (byteText.Length == 0
                || !IsAllDecimalDigits(byteText)
                || !int.TryParse(byteText, NumberStyles.None, CultureInfo.InvariantCulture, out var byteOffset)
                || byteOffset < 0)
            {
                error = "Byte offset must be a non-negative decimal integer.";
                return false;
            }

            if (bitText.Length == 0
                || !IsAllDecimalDigits(bitText)
                || !int.TryParse(bitText, NumberStyles.None, CultureInfo.InvariantCulture, out var bit)
                || bit < 0
                || bit > 7)
            {
                error = "Bit index must be an integer from 0 to 7.";
                return false;
            }

            var canonical = $"{areaLetter}{byteOffset}.{bit}";
            address = new S7Address(area, byteOffset, SizeBytes: 1, BitIndex: bit, Canonical: canonical);
            return true;
        }

        // Sized form: IB0 / VW100 / QD0
        if (rest.Length < 2 || !IsSizeLetter(rest[0]))
        {
            error = "Expected bit form (I0.0) or sized form (IB0, VW100, …).";
            return false;
        }

        var sizeBytes = rest[0] switch
        {
            'B' => 1,
            'W' => 2,
            'D' => 4,
            _ => 0
        };

        var offsetText = rest[1..];
        if (offsetText.Length == 0
            || !IsAllDecimalDigits(offsetText)
            || !int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset < 0)
        {
            error = "Byte offset must be a non-negative decimal integer.";
            return false;
        }

        var sizedCanonical = $"{areaLetter}{rest[0]}{offset}";
        address = new S7Address(area, offset, sizeBytes, BitIndex: null, Canonical: sizedCanonical);
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

    private static bool TryMapArea(char areaChar, out S7Area area, out char areaLetter)
    {
        switch (areaChar)
        {
            case 'I':
                area = S7Area.Inputs;
                areaLetter = 'I';
                return true;
            case 'Q':
                area = S7Area.Outputs;
                areaLetter = 'Q';
                return true;
            case 'M':
                area = S7Area.Flags;
                areaLetter = 'M';
                return true;
            case 'V':
                area = S7Area.V;
                areaLetter = 'V';
                return true;
            default:
                area = default;
                areaLetter = default;
                return false;
        }
    }

    private static bool IsSizeLetter(char c) => c is 'B' or 'W' or 'D';

    private static bool IsAllDecimalDigits(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
