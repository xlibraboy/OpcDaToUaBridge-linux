using System.Globalization;
using OpcBridge.Drivers.Melsec.Addressing;

namespace OpcBridge.Drivers.Melsec.Protocol;

/// <summary>
/// ACPU-common head device formatting for 1C Frame command bodies.
/// </summary>
public static class Melsec1CDeviceCodes
{
    /// <summary>
    /// Head device string as required by 1C body, e.g. "D0100", "M0010", "X0020".
    /// D/M: letter + 4 decimal digits. X/Y: letter + 4 zero-padded octal digits
    /// (fixed width covers full AnN range X0000–X07FF / 0–0x7FF).
    /// </summary>
    public static string FormatHead(MelsecAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.Device switch
        {
            MelsecDeviceKind.D => "D" + address.Number.ToString("D4", CultureInfo.InvariantCulture),
            MelsecDeviceKind.M => "M" + address.Number.ToString("D4", CultureInfo.InvariantCulture),
            MelsecDeviceKind.X => "X" + ToOctal4(address.Number),
            MelsecDeviceKind.Y => "Y" + ToOctal4(address.Number),
            _ => throw new ArgumentOutOfRangeException(nameof(address), address.Device, "Unsupported MELSEC device kind.")
        };
    }

    private const int MaxXyAnN = 0x7FF; // AnN X/Y max; value 2047 → octal 3777 (4 digits)

    private static string ToOctal4(int number)
    {
        if (number < 0 || number > MaxXyAnN)
        {
            throw new ArgumentOutOfRangeException(
                nameof(number),
                number,
                $"X/Y device number must be in 0..{MaxXyAnN} (0x7FF) for 4-digit octal 1C heads.");
        }

        // Fixed 4-digit zero-padded octal (AnN wire: X0020, X07FF) — avoids variable-length heads.
        return Convert.ToString(number, 8).PadLeft(4, '0');
    }
}
