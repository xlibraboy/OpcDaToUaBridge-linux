namespace OpcBridge.Hmi.Core;

/// <summary>
/// Provides the natural min/max range for a given OPC/bridge data type.
/// Used by the trend charts to pin the Y axis for integer/boolean types.
/// Floating-point types (Float, Double, Decimal) return null — the chart
/// auto-scales from the actual data instead.
/// </summary>
public static class DataTypeRanges
{
    public static (double Min, double Max)? GetRange(string? dataType)
    {
        return (dataType ?? "Double").Trim().ToLowerInvariant() switch
        {
            "boolean" or "bool" => (0, 1),
            "byte" => (0, 255),
            "sbyte" => (-128, 127),
            "int16" or "short" => (-32768, 32767),
            "uint16" or "ushort" => (0, 65535),
            "int32" or "int" => (-2147483648, 2147483647),
            "uint32" or "uint" => (0, 4294967295),
            "int64" or "long" => (-9223372036854775808, 9223372036854775807),
            "uint64" or "ulong" => (0, 18446744073709551615),
            _ => null // Float, Double, Decimal, String, Auto — auto-scale
        };
    }
}
