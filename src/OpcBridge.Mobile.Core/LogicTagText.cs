using System.Globalization;
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
            _ => Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? "—"
        };

        return string.IsNullOrWhiteSpace(entry.Unit) ? number : number + " " + entry.Unit;
    }
}
