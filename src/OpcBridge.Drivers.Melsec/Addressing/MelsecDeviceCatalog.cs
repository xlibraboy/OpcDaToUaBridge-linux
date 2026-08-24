namespace OpcBridge.Drivers.Melsec.Addressing;

/// <summary>How device numbers are written when addressing the PLC.</summary>
public enum MelsecNumberBase
{
    /// <summary>Plain decimal digits (D, M, timers, counters).</summary>
    Decimal,

    /// <summary>AnN-family X/Y: traditionally octal; hex-looking forms accepted (e.g. Y0F).</summary>
    OctalOrHex
}

/// <summary>
/// One row of the accepted-device table shown on the dashboard: what a device means,
/// how its numbers are written, and the inclusive numeric range this app accepts.
/// Limits here are the app's enforced caps (A3N brief), shared by the serial driver
/// and MX Component — <see cref="MelsecAddressParser"/> reads them from this catalog
/// so displayed ranges can never drift from enforced validation.
/// </summary>
public sealed record MelsecDeviceRange(
    string Device,
    string DisplayName,
    string SignalType,
    MelsecNumberBase NumberBase,
    int MinNumber,
    int MaxNumber,
    bool BitSuffixAllowed,
    int? MaxBitIndex,
    IReadOnlyList<string> Aliases,
    string Example);

public static class MelsecDeviceCatalog
{
    public static IReadOnlyList<MelsecDeviceRange> Devices { get; } = new[]
    {
        new MelsecDeviceRange(
            "D", "Data register", "Word", MelsecNumberBase.Decimal,
            0, 1023, BitSuffixAllowed: true, MaxBitIndex: 15,
            Aliases: Array.Empty<string>(), Example: "D100"),
        new MelsecDeviceRange(
            "M", "Internal relay", "Bit", MelsecNumberBase.Decimal,
            0, 2047, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: Array.Empty<string>(), Example: "M10"),
        new MelsecDeviceRange(
            "X", "Input relay", "Bit", MelsecNumberBase.OctalOrHex,
            0, 0x7FF, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: Array.Empty<string>(), Example: "X20"),
        new MelsecDeviceRange(
            "Y", "Output relay", "Bit", MelsecNumberBase.OctalOrHex,
            0, 0x7FF, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: Array.Empty<string>(), Example: "Y0F"),
        new MelsecDeviceRange(
            "TS", "Timer contact", "Bit", MelsecNumberBase.Decimal,
            0, 2047, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: Array.Empty<string>(), Example: "TS5"),
        new MelsecDeviceRange(
            "TC", "Timer coil", "Bit", MelsecNumberBase.Decimal,
            0, 2047, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: Array.Empty<string>(), Example: "TC5"),
        new MelsecDeviceRange(
            "TN", "Timer present value", "Word", MelsecNumberBase.Decimal,
            0, 2047, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: new[] { "T" }, Example: "T0"),
        new MelsecDeviceRange(
            "CS", "Counter contact", "Bit", MelsecNumberBase.Decimal,
            0, 2047, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: Array.Empty<string>(), Example: "CS7"),
        new MelsecDeviceRange(
            "CC", "Counter coil", "Bit", MelsecNumberBase.Decimal,
            0, 2047, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: Array.Empty<string>(), Example: "CC7"),
        new MelsecDeviceRange(
            "CN", "Counter present value", "Word", MelsecNumberBase.Decimal,
            0, 2047, BitSuffixAllowed: false, MaxBitIndex: null,
            Aliases: new[] { "C" }, Example: "C0"),
    };

    /// <summary>Finds a catalog row by canonical device prefix (case-insensitive).</summary>
    public static MelsecDeviceRange Find(string device)
    {
        foreach (MelsecDeviceRange range in Devices)
        {
            if (string.Equals(range.Device, device, StringComparison.OrdinalIgnoreCase))
            {
                return range;
            }
        }

        throw new KeyNotFoundException($"No MELSEC device '{device}' in the catalog.");
    }

    /// <summary>Inclusive upper limit enforced by the parser for a device kind.</summary>
    internal static int MaxNumberFor(MelsecDeviceKind kind) => kind switch
    {
        MelsecDeviceKind.D => Find("D").MaxNumber,
        MelsecDeviceKind.M => Find("M").MaxNumber,
        MelsecDeviceKind.X or MelsecDeviceKind.Y => Find("X").MaxNumber,
        MelsecDeviceKind.TS or MelsecDeviceKind.TC or MelsecDeviceKind.TN => Find("TN").MaxNumber,
        MelsecDeviceKind.CS or MelsecDeviceKind.CC or MelsecDeviceKind.CN => Find("CN").MaxNumber,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported device kind.")
    };

    /// <summary>Highest bit index allowed in a bit-in-word suffix (D devices only).</summary>
    internal static int MaxBitIndexForWordDevices => Find("D").MaxBitIndex!.Value;
}
