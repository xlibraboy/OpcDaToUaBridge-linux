using System.Text.Json.Serialization;
namespace OpcBridge.Core;

public sealed class TagMapping
{
    /// <summary>
    /// When set, this tag is "fed" by another tag: the provider tag's value is forwarded
    /// as a write into this tag's DA item. Direction/permission is governed by the
    /// provider's AccessRights (must allow Read) and this tag's AccessRights (must allow Write).
    /// Optional — a tag with no provider is a normal standalone mapping.
    /// </summary>
    public string? ProviderSourceId { get; set; }
    [JsonPropertyName("providerItemId")]
    public string? ProviderItemId { get; set; }

    public string SourceId { get; set; } = "default";
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;
    public string UaNodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DataType { get; set; } = "Double";
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = TagMode.Source;
    public string? ManualValue { get; set; }
    public int PollRateMs { get; set; }

    /// <summary>
    /// Digits after the decimal point for floating-point values (Float/Double/Decimal).
    /// null (default) = no rounding, value passes through untouched.
    /// 0 = hide all decimals, 1 = one digit, 2 = two digits, ...
    /// Applied where the value enters the bridge, so UA/MQTT/Influx/dashboard/HMI all
    /// see the same rounded number.
    /// </summary>
    public int? Decimals { get; set; }

    public string? DaGroup { get; set; }
    public float DeadbandPct { get; set; }
    public bool Writeable { get; set; }
    public string AccessRights { get; set; } = TagAccessRights.Read;
    public bool MqttEnabled { get; set; }
    public string? MqttTopic { get; set; }
    public bool InfluxEnabled { get; set; }

    /// <summary>
    /// OPC UA sources only: name of the source-defined named subscription this tag rides on.
    /// Empty string = the source's default bucket (source UpdateRateMs semantics, unchanged).
    /// Matched case-insensitively against the source's definitions; unknown names group into
    /// the default bucket at runtime (spec §4).
    /// </summary>
    [JsonPropertyName("subscription")]
    public string Subscription { get; set; } = string.Empty;

    /// <summary>
    /// PLC sources (MxComponent today) only: name of the source-defined PLC group this
    /// tag rides on. Empty string = source default bucket (default-rate semantics).
    /// Unknown names fall back to the default bucket at runtime (spec §4).
    /// </summary>
    [JsonPropertyName("plcGroup")]
    public string PlcGroup { get; set; } = string.Empty;
}
public static class TagMode
{
    public const string Source = "Source";
    public const string Manual = "Manual";
}

public static class TagAccessRights
{
    public const string Read = "Read";
    public const string ReadWrite = "Read-Write";
    public const string Write = "Write";
}

public static class TagDecimals
{
    private const int MaxDigits = 15;

    /// <summary>
    /// Applies the tag's Decimals setting to a value. Only floating-point values are
    /// rounded; other types (and a null or negative/out-of-range Decimals = "off") pass
    /// through as-is. Uses AwayFromZero so 2.5 rounds to 3 (operator expectation, not
    /// banker's rounding).
    /// </summary>
    public static BridgeValue Apply(BridgeValue value, TagMapping? mapping)
    {
        int? digits = mapping?.Decimals;
        if (digits is not (>= 0 and <= MaxDigits) || value.Value is not (float or double or decimal))
        {
            return value;
        }

        object rounded = value.Value switch
        {
            float f => (float)Math.Round(f, digits.Value, MidpointRounding.AwayFromZero),
            double d => Math.Round(d, digits.Value, MidpointRounding.AwayFromZero),
            decimal m => Math.Round(m, digits.Value, MidpointRounding.AwayFromZero),
            _ => value.Value!
        };
        return value with { Value = rounded };
    }
}
