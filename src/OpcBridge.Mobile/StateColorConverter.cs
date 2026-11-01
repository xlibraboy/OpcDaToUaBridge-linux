using System.Globalization;

namespace OpcBridge.Mobile;

/// <summary>
/// Maps a state key (block: ready/blocked/unknown/disabled; condition: true/false/unknown;
/// step: done/current/pending/unknown) to the display colour. The state word is always shown
/// next to it, so the screen still reads without colour. ConverterParameter "checkpoint" gives
/// a checkpoint's "no data" the amber of its rail dot, where the block list's unknown is muted.
/// </summary>
public sealed class StateColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value as string ?? string.Empty;
        if ((parameter as string) == "checkpoint" && (key.Length == 0 || key == "unknown"))
        {
            return Color.FromArgb("#E2A336");
        }

        return key switch
        {
            "ready" or "true" or "done" or "match" => Color.FromArgb("#4CC38A"),
            "blocked" or "false" => Color.FromArgb("#E5484D"),
            "current" or "mismatch" => Color.FromArgb("#E2A336"),
            _ => Color.FromArgb("#9AA6A0")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
