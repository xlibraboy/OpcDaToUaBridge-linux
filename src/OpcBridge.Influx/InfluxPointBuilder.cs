using OpcBridge.Core;

namespace OpcBridge.Influx;

public sealed record InfluxPointModel(
    string Measurement,
    IReadOnlyDictionary<string, string> Tags,
    object? ValueField,
    string ValueFieldKind,
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
            ["da_item_id"] = value.DaItemId
        };

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            tags["display_name"] = displayName.Trim();
        }

        (object? field, string kind) = NormalizeValue(value.Value);
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
            kind,
            value.DaQuality,
            value.IsGood,
            timestampUtc);
    }

    private static (object? Field, string Kind) NormalizeValue(object? raw)
    {
        if (raw is null)
        {
            return (null, "null");
        }

        return raw switch
        {
            bool b => (b, "bool"),
            byte b => ((long)b, "long"),
            sbyte sb => ((long)sb, "long"),
            short s => ((long)s, "long"),
            ushort us => ((long)us, "long"),
            int i => ((long)i, "long"),
            uint ui => ((long)ui, "long"),
            long l => (l, "long"),
            ulong ul => (ul <= long.MaxValue ? (long)ul : (double)ul, ul <= long.MaxValue ? "long" : "double"),
            float f => ((double)f, "double"),
            double d => (d, "double"),
            decimal m => ((double)m, "double"),
            string s => (s, "string"),
            char c => (c.ToString(), "string"),
            DateTime dt => (dt.ToUniversalTime().ToString("o"), "string"),
            _ => (Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? raw.ToString() ?? string.Empty, "string")
        };
    }
}
