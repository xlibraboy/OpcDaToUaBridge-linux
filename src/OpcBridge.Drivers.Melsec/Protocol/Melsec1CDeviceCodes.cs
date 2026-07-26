using System.Globalization;
using OpcBridge.Drivers.Melsec.Addressing;

namespace OpcBridge.Drivers.Melsec.Protocol;

/// <summary>
/// ACPU-common head device formatting for 1C Frame command bodies.
/// </summary>
public static class Melsec1CDeviceCodes
{
    /// <summary>
    /// Head device string as required by 1C body, e.g. "D0100", "M0010", "X020".
    /// D/M: letter + 4 decimal digits. X/Y: letter + 3 zero-padded octal digits.
    /// </summary>
    public static string FormatHead(MelsecAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.Device switch
        {
            MelsecDeviceKind.D => "D" + address.Number.ToString("D4", CultureInfo.InvariantCulture),
            MelsecDeviceKind.M => "M" + address.Number.ToString("D4", CultureInfo.InvariantCulture),
            MelsecDeviceKind.X => "X" + ToOctal3(address.Number),
            MelsecDeviceKind.Y => "Y" + ToOctal3(address.Number),
            _ => throw new ArgumentOutOfRangeException(nameof(address), address.Device, "Unsupported MELSEC device kind.")
        };
    }

    private static string ToOctal3(int number)
    {
        if (number < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "Device number must be non-negative.");
        }

        // 3-digit zero-padded octal (ACPU common style: X020).
        return Convert.ToString(number, 8).PadLeft(3, '0');
    }
}
