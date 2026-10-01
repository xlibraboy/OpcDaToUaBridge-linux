using System.Globalization;
using System.Text.Json;

namespace OpcBridge.Client.Workers;

/// <summary>
/// A value crossing the worker pipe: the item id, a type tag, the JSON-encoded value, the DA
/// timestamp and quality. The type tag preserves the exact CLR type (float vs double, signed
/// vs unsigned integers) so values round-trip without silent widening.
/// </summary>
public sealed record WireValue(
    string ItemId,
    string Type,
    JsonElement Value,
    DateTime TimestampUtc,
    int DaQuality,
    bool IsGood);

/// <summary>Type tags for <see cref="WireValue"/>.</summary>
public static class WireValueTypes
{
    public const string Null = "null";
    public const string Boolean = "bool";
    public const string Int8 = "i1";
    public const string UInt8 = "ui1";
    public const string Int16 = "i2";
    public const string UInt16 = "ui2";
    public const string Int32 = "i4";
    public const string UInt32 = "ui4";
    public const string Int64 = "i8";
    public const string UInt64 = "ui8";
    public const string Float32 = "r4";
    public const string Float64 = "r8";
    public const string String = "str";
    public const string DateTime = "date";
    public const string Bytes = "bytes";
}

/// <summary>Encodes and decodes the type-tagged scalar values used by <see cref="WireValue"/>.</summary>
public static class WireValueCodec
{
    public static (string Type, JsonElement Value) Encode(object? value)
    {
        switch (value)
        {
            case null:
                return (WireValueTypes.Null, JsonSerializer.SerializeToElement<object?>(null));
            case bool v:
                return (WireValueTypes.Boolean, JsonSerializer.SerializeToElement(v));
            case sbyte v:
                return (WireValueTypes.Int8, JsonSerializer.SerializeToElement(v));
            case byte v:
                return (WireValueTypes.UInt8, JsonSerializer.SerializeToElement(v));
            case short v:
                return (WireValueTypes.Int16, JsonSerializer.SerializeToElement(v));
            case ushort v:
                return (WireValueTypes.UInt16, JsonSerializer.SerializeToElement(v));
            case int v:
                return (WireValueTypes.Int32, JsonSerializer.SerializeToElement(v));
            case uint v:
                return (WireValueTypes.UInt32, JsonSerializer.SerializeToElement(v));
            case long v:
                return (WireValueTypes.Int64, JsonSerializer.SerializeToElement(v));
            case ulong v:
                return (WireValueTypes.UInt64, JsonSerializer.SerializeToElement(v));
            case float v:
                return (WireValueTypes.Float32, JsonSerializer.SerializeToElement(v));
            case double v:
                return (WireValueTypes.Float64, JsonSerializer.SerializeToElement(v));
            case string v:
                return (WireValueTypes.String, JsonSerializer.SerializeToElement(v));
            case DateTime v:
                return (
                    WireValueTypes.DateTime,
                    JsonSerializer.SerializeToElement(v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            case byte[] v:
                return (WireValueTypes.Bytes, JsonSerializer.SerializeToElement(Convert.ToBase64String(v)));
            default:
                throw new NotSupportedException(
                    $"Value type {value.GetType().Name} cannot cross the worker pipe; only scalar OPC DA types are supported.");
        }
    }

    public static object? Decode(string type, JsonElement value) => type switch
    {
        WireValueTypes.Null => null,
        WireValueTypes.Boolean => value.GetBoolean(),
        WireValueTypes.Int8 => value.GetSByte(),
        WireValueTypes.UInt8 => value.GetByte(),
        WireValueTypes.Int16 => value.GetInt16(),
        WireValueTypes.UInt16 => value.GetUInt16(),
        WireValueTypes.Int32 => value.GetInt32(),
        WireValueTypes.UInt32 => value.GetUInt32(),
        WireValueTypes.Int64 => value.GetInt64(),
        WireValueTypes.UInt64 => value.GetUInt64(),
        WireValueTypes.Float32 => value.GetSingle(),
        WireValueTypes.Float64 => value.GetDouble(),
        WireValueTypes.String => value.GetString(),
        WireValueTypes.DateTime => System.DateTime.Parse(
            value.GetString() ?? string.Empty,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind),
        WireValueTypes.Bytes => Convert.FromBase64String(value.GetString() ?? string.Empty),
        _ => throw new WorkerProtocolException($"Unknown wire value type '{type}'.")
    };
}
