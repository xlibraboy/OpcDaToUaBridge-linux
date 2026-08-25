using System.Text.Json;
using OpcBridge.Client;

namespace OpcBridge.Hmi.Core;

public static class DisplayWidgetTypes
{
    public const string Label = "label";
    public const string Numeric = "numeric";
    public const string QualityLamp = "qualityLamp";
    public const string BoolIndicator = "boolIndicator";
    public const string PushButton = "pushButton";

    public static bool IsKnown(string? type) =>
        string.Equals(type, Label, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, Numeric, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, QualityLamp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, BoolIndicator, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, PushButton, StringComparison.OrdinalIgnoreCase);
}

public static class DisplayPropReader
{
    public static string GetString(Dictionary<string, JsonElement>? props, string key, string fallback = "")
    {
        if (props is null || !props.TryGetValue(key, out JsonElement el))
        {
            return fallback;
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? fallback,
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback
        };
    }

    public static bool GetBool(Dictionary<string, JsonElement>? props, string key, bool fallback = false)
    {
        if (props is null || !props.TryGetValue(key, out JsonElement el))
        {
            return fallback;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out bool b) => b,
            JsonValueKind.Number when el.TryGetInt32(out int i) => i != 0,
            _ => fallback
        };
    }

    public static double GetDouble(Dictionary<string, JsonElement>? props, string key, double fallback = 0)
    {
        if (props is null || !props.TryGetValue(key, out JsonElement el))
        {
            return fallback;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
        {
            return d;
        }

        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double sd))
        {
            return sd;
        }

        return fallback;
    }

    public static object? GetWriteValue(Dictionary<string, JsonElement>? props)
    {
        if (props is null || !props.TryGetValue("writeValue", out JsonElement el))
        {
            return true;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number when el.TryGetInt64(out long l) => l,
            JsonValueKind.Number when el.TryGetDouble(out double d) => d,
            _ => el.ToString()
        };
    }
}

public static class DisplayDocumentValidator
{
    public static string? DescribeLoadIssue(DisplayDocumentDto? doc)
    {
        if (doc is null)
        {
            return "Display document missing";
        }

        if (doc.SchemaVersion != 1)
        {
            return $"Unsupported schemaVersion {doc.SchemaVersion}";
        }

        if (doc.Width <= 0 || doc.Height <= 0)
        {
            return "Display width/height invalid";
        }

        return null;
    }
}
