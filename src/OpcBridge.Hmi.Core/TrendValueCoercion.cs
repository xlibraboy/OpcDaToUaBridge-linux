using System.Globalization;
using System.Text.Json;

namespace OpcBridge.Hmi.Core;

/// <summary>
/// Converts a history sample's raw wire value into the numeric sample the trends plot.
/// Values arrive as CLR primitives from in-process callers and as <see cref="JsonElement"/>
/// after wire deserialization; booleans map to 1/0 so a Boolean tag's history draws on its
/// 0..1 digital strip instead of being dropped as non-numeric.
/// </summary>
public static class TrendValueCoercion
{
    /// <summary>
    /// Converts <paramref name="value"/> to a chart sample value; returns false for null or a
    /// value that carries no number, in which case the sample is skipped.
    /// </summary>
    public static bool TryToDouble(object? value, out double y)
    {
        switch (value)
        {
            case null:
                y = 0;
                return false;
            case bool b:
                y = b ? 1 : 0;
                return true;
            case byte b:
                y = b;
                return true;
            case sbyte b:
                y = b;
                return true;
            case short s:
                y = s;
                return true;
            case ushort s:
                y = s;
                return true;
            case int i:
                y = i;
                return true;
            case uint i:
                y = i;
                return true;
            case long l:
                y = l;
                return true;
            case ulong ul:
                y = ul;
                return true;
            case float f:
                y = f;
                return true;
            case double d:
                y = d;
                return true;
            case decimal m:
                y = (double)m;
                return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed):
                y = parsed;
                return true;
            case JsonElement element:
                return TryFromJson(element, out y);
            default:
                y = 0;
                return false;
        }
    }

    private static bool TryFromJson(JsonElement element, out double y)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out double number):
                y = number;
                return true;
            case JsonValueKind.True:
                y = 1;
                return true;
            case JsonValueKind.False:
                y = 0;
                return true;
            case JsonValueKind.String
                when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double text):
                y = text;
                return true;
            default:
                y = 0;
                return false;
        }
    }
}
