using System.Globalization;
using System.Text;

namespace OpcBridge.Drivers.Melsec.Protocol;

/// <summary>
/// ACPU common 1C command body builders and data parsers (BR/BW/WR/WW).
/// Bodies exclude station, PC, and the 2-char command code itself.
/// </summary>
public static class Melsec1CCommands
{
    /// <summary>Bit batch read body: head device + bit count (4 hex digits).</summary>
    public static string BuildBitReadBody(string deviceHead, int bitCount)
    {
        ValidateHead(deviceHead);
        ValidateCount(bitCount, nameof(bitCount));
        return deviceHead + FormatCount4(bitCount);
    }

    /// <summary>Bit batch write body: head + bit count (4 hex) + '0'/'1' chars.</summary>
    public static string BuildBitWriteBody(string deviceHead, string bitData01)
    {
        ValidateHead(deviceHead);
        ArgumentNullException.ThrowIfNull(bitData01);
        if (bitData01.Length == 0)
        {
            throw new ArgumentException("Bit data must contain at least one 0/1 character.", nameof(bitData01));
        }

        for (int i = 0; i < bitData01.Length; i++)
        {
            char c = bitData01[i];
            if (c is not ('0' or '1'))
            {
                throw new ArgumentException(
                    $"Bit data must be only '0' and '1' characters (invalid '{c}' at index {i}).",
                    nameof(bitData01));
            }
        }

        return deviceHead + FormatCount4(bitData01.Length) + bitData01;
    }

    /// <summary>Word batch read body: head device + word count (4 hex digits).</summary>
    public static string BuildWordReadBody(string deviceHead, int wordCount)
    {
        ValidateHead(deviceHead);
        ValidateCount(wordCount, nameof(wordCount));
        return deviceHead + FormatCount4(wordCount);
    }

    /// <summary>Word batch write body: head + word count (4 hex) + each word as 4 hex digits.</summary>
    public static string BuildWordWriteBody(string deviceHead, IReadOnlyList<ushort> words)
    {
        ValidateHead(deviceHead);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            throw new ArgumentException("Word list must contain at least one word.", nameof(words));
        }

        var sb = new StringBuilder(deviceHead.Length + 4 + (words.Count * 4));
        sb.Append(deviceHead);
        sb.Append(FormatCount4(words.Count));
        for (int i = 0; i < words.Count; i++)
        {
            sb.Append(words[i].ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Parse bit read data chars ('0'/'1') into booleans.</summary>
    public static bool[] ParseBitReadData(string dataChars, int bitCount)
    {
        ArgumentNullException.ThrowIfNull(dataChars);
        ValidateCount(bitCount, nameof(bitCount));
        if (dataChars.Length < bitCount)
        {
            throw new MelsecProtocolException(
                $"Bit read data length {dataChars.Length} is less than requested bit count {bitCount}.");
        }

        var bits = new bool[bitCount];
        for (int i = 0; i < bitCount; i++)
        {
            char c = dataChars[i];
            bits[i] = c switch
            {
                '1' => true,
                '0' => false,
                _ => throw new MelsecProtocolException(
                    $"Invalid bit data character '{c}' at index {i}; expected '0' or '1'.")
            };
        }

        return bits;
    }

    /// <summary>Parse word read data as consecutive 4-digit hex words.</summary>
    public static ushort[] ParseWordReadData(string dataChars, int wordCount)
    {
        ArgumentNullException.ThrowIfNull(dataChars);
        ValidateCount(wordCount, nameof(wordCount));
        int needed = wordCount * 4;
        if (dataChars.Length < needed)
        {
            throw new MelsecProtocolException(
                $"Word read data length {dataChars.Length} is less than {needed} chars for {wordCount} word(s).");
        }

        var words = new ushort[wordCount];
        for (int i = 0; i < wordCount; i++)
        {
            string hex = dataChars.Substring(i * 4, 4);
            if (!ushort.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort value))
            {
                throw new MelsecProtocolException($"Invalid word hex data '{hex}' at word index {i}.");
            }

            words[i] = value;
        }

        return words;
    }

    private static string FormatCount4(int count) =>
        count.ToString("X4", CultureInfo.InvariantCulture);

    private static void ValidateHead(string deviceHead)
    {
        if (string.IsNullOrEmpty(deviceHead))
        {
            throw new ArgumentException("Device head is required.", nameof(deviceHead));
        }
    }

    private static void ValidateCount(int count, string paramName)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, count, "Count must be positive.");
        }

        if (count > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(paramName, count, "Count must fit in 4 hex digits.");
        }
    }
}
