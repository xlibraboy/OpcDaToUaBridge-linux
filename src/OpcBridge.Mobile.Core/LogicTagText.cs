using System.Globalization;
using System.Text.Json;
using OpcBridge.Core;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// Renders a cached tag value the way the plant reads it: the mapping's on/off text for a
/// digital tag, otherwise the number with its engineering unit. Bad quality is marked so an
/// unknown condition never reads like a healthy one.
/// </summary>
public static class LogicTagText
{
    public static string Format(MultiBridgeTagEntry? entry)
    {
        if (entry?.Value is null)
        {
            return "—";
        }

        string text = entry.Digital ? FormatDigital(entry) : FormatAnalog(entry);
        return entry.IsGood == false ? text + " · bad" : text;
    }

    /// <summary>
    /// The raw reading for the inspector's LIVE row: the bit as 1/0, or the Analog value with
    /// its unit. The operator's word is drawn separately in its own STATE chip.
    /// </summary>
    public static string Raw(MultiBridgeTagEntry? entry)
    {
        if (entry?.Value is null)
        {
            return "—";
        }

        if (!entry.Digital)
        {
            return FormatAnalog(entry);
        }

        return TagDigital.CoerceBool(entry.Value) ? "1" : "0";
    }

    /// <summary>
    /// The mapping's operator word for a digital tag (OnText/OffText, else On/Off). Numeric
    /// readings have no word: the comparison carries them, as in the web Result panel.
    /// </summary>
    public static string StateWord(MultiBridgeTagEntry? entry)
    {
        if (entry?.Value is null || !entry.Digital)
        {
            return string.Empty;
        }

        bool on = TagDigital.CoerceBool(entry.Value);
        string? text = on ? entry.OnText : entry.OffText;
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text!.Trim();
        }

        return on ? "On" : "Off";
    }

    private static string FormatDigital(MultiBridgeTagEntry entry)
    {
        bool on = TagDigital.CoerceBool(entry.Value);
        string? text = on ? entry.OnText : entry.OffText;
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text!;
        }

        return on ? "On" : "Off";
    }

    private static string FormatAnalog(MultiBridgeTagEntry entry)
    {
        string number = entry.Value switch
        {
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            string s => s,
            JsonElement json => FormatJson(json),
            _ => Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? "—"
        };

        return string.IsNullOrWhiteSpace(entry.Unit) ? number : number + " " + entry.Unit;
    }

    /// <summary>
    /// A cached reading deserializes to a <see cref="JsonElement"/>; format its number with the
    /// same decimals as a live one instead of falling back to the raw JSON text.
    /// </summary>
    private static string FormatJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetDouble(out double number)
            ? number.ToString("0.###", CultureInfo.InvariantCulture)
            : element.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.String => element.GetString() ?? "—",
        JsonValueKind.Null or JsonValueKind.Undefined => "—",
        _ => element.GetRawText()
    };
}
