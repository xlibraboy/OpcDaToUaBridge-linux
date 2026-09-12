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
    /// Engineering unit label for this tag (e.g. "°C", "bar", "RPM").
    /// Set per-tag in the dashboard; flows to the HMI for display on widgets and trends.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Renders this tag as a two-state (on/off) signal in the HMI faceplate, so status shows
    /// as <see cref="OnText"/>/<see cref="OffText"/> instead of the raw value.
    /// null = auto (Boolean/Bool tags are digital, everything else is analog),
    /// true = force digital (e.g. a Byte tag that only ever carries 0/1),
    /// false = force analog.
    /// </summary>
    public bool? Digital { get; set; }

    /// <summary>
    /// Faceplate label for the on (non-zero / true) state when the tag resolves digital
    /// (e.g. "Running"). null/blank = render the raw value instead.
    /// </summary>
    public string? OnText { get; set; }

    /// <summary>
    /// Faceplate label for the off (zero / false) state when the tag resolves digital
    /// (e.g. "Stopped"). null/blank = render the raw value instead.
    /// </summary>
    public string? OffText { get; set; }

    /// <summary>
    /// How this tag's history renders in the HMI trend charts: <see cref="TrendStyleTypes.Continuous"/>
    /// (line through the samples, default) or <see cref="TrendStyleTypes.Step"/> (sample-and-hold
    /// steps — the value is held constant until the next sample arrives). Set per-tag in the
    /// dashboard Maps faceplate; applies to both the faceplate 1h history and the tag's trend window.
    /// </summary>
    public string TrendStyle { get; set; } = TrendStyleTypes.Continuous;

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

public static class TrendStyleTypes
{
    public const string Continuous = "Continuous";
    public const string Step = "Step";

    /// <summary>
    /// Canonicalizes a configured trend style; anything that is not explicitly "Step"
    /// (empty, "Auto", misspelled, etc.) maps to the <see cref="Continuous"/> default.
    /// </summary>
    public static string Normalize(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value.Trim(), Step, StringComparison.OrdinalIgnoreCase)
            ? Step
            : Continuous;
    }
}

/// <summary>
/// Resolves a tag's on/off ("digital") semantics and coerces raw values to a boolean.
/// Booleans are digital automatically; byte and other numeric tags are analog unless
/// <see cref="TagMapping.Digital"/> is explicitly set, because a 0-255 value cannot be
/// told apart from a genuine two-state flag by type alone.
/// </summary>
public static class TagDigital
{
    public static bool Resolve(TagMapping? mapping)
    {
        if (mapping is null)
        {
            return false;
        }

        return mapping.Digital ?? IsBooleanType(mapping.DataType);
    }

    public static bool IsBooleanType(string? dataType) =>
        string.Equals(dataType, "Boolean", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataType, "Bool", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Any non-zero / true value is on. Handles CLR primitives plus <see cref="System.Text.Json.JsonElement"/>
    /// (live HMI values arrive as JSON elements after wire deserialization).
    /// </summary>
    public static bool CoerceBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        byte by => by != 0,
        sbyte sb => sb != 0,
        short s => s != 0,
        ushort us => us != 0,
        int i => i != 0,
        uint ui => ui != 0,
        long l => l != 0,
        ulong ul => ul != 0,
        float f => Math.Abs(f) > float.Epsilon,
        double d => Math.Abs(d) > double.Epsilon,
        decimal m => m != 0,
        string s when bool.TryParse(s, out bool b) => b,
        string s when s == "1" => true,
        string s when s == "0" => false,
        System.Text.Json.JsonElement je => CoerceJsonElement(je),
        _ => false
    };

    private static bool CoerceJsonElement(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Number => element.TryGetDouble(out double n) && Math.Abs(n) > double.Epsilon,
        System.Text.Json.JsonValueKind.String => CoerceBool(element.GetString()),
        _ => false
    };
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
