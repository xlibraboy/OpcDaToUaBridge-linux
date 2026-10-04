using OpcBridge.Core;

namespace OpcBridge.Influx;

public sealed record InfluxPointModel(
    string Measurement,
    IReadOnlyDictionary<string, string> Tags,
    object? ValueField,
    string ValueFieldName,
    int Quality,
    bool IsGood,
    DateTime TimestampUtc);

public static class InfluxPointBuilder
{
    public static InfluxPointModel Build(InfluxOptions options, BridgeValue value, string? displayName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(value);

        string measurement = string.IsNullOrWhiteSpace(options.Measurement) ? "opc_tags" : options.Measurement.Trim();
        Dictionary<string, string> tags = new(StringComparer.Ordinal)
        {
            ["source_id"] = value.SourceId,
            ["da_item_id"] = value.ItemId
        };

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            tags["display_name"] = displayName.Trim();
        }

        (object? field, string fieldName) = NormalizeValue(value.Value);
        DateTime timestampUtc = value.TimestampUtc.Kind switch
        {
            DateTimeKind.Utc => value.TimestampUtc,
            DateTimeKind.Local => value.TimestampUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.TimestampUtc, DateTimeKind.Utc)
        };

        return new InfluxPointModel(
            measurement,
            tags,
            field,
            fieldName,
            value.DaQuality,
            value.IsGood,
            timestampUtc);
    }

    /// <summary>
    /// Maps a raw value onto its storage field. InfluxDB locks a field name to one type per
    /// measurement, so every CLR kind family gets its own stable field: the integer family in
    /// <c>value_int</c>, floats in <c>value</c>, booleans in <c>value_bool</c> and everything
    /// textual (String, DateTime, ByteString) in <c>value_str</c>. Any mix of tag value types can
    /// then share one measurement without field-type conflicts. An empty name means "no value".
    /// </summary>
    private static (object? Field, string FieldName) NormalizeValue(object? raw)
    {
        if (raw is null)
        {
            return (null, string.Empty);
        }

        return raw switch
        {
            bool b => (b, "value_bool"),
            byte b => ((long)b, "value_int"),
            sbyte sb => ((long)sb, "value_int"),
            short s => ((long)s, "value_int"),
            ushort us => ((long)us, "value_int"),
            int i => ((long)i, "value_int"),
            uint ui => ((long)ui, "value_int"),
            long l => (l, "value_int"),
            ulong ul => ul <= long.MaxValue ? ((long)ul, "value_int") : ((double)ul, "value"),
            float f => ((double)f, "value"),
            double d => (d, "value"),
            decimal m => ((double)m, "value"),
            string s => (s, "value_str"),
            char c => (c.ToString(), "value_str"),
            DateTime dt => (dt.ToUniversalTime().ToString("o"), "value_str"),
            byte[] bytes => (Convert.ToBase64String(bytes), "value_str"),
            _ => (Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? raw.ToString() ?? string.Empty, "value_str")
        };
    }
}
