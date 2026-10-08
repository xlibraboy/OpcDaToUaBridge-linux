using System.Globalization;

namespace OpcBridge.Mobile;

/// <summary>
/// Maps a state key (block: ready/blocked/unknown/disabled; condition: true/false/unknown;
/// step: done/current/pending/unknown) to the display colour. The state word is always shown
/// next to it, so the screen still reads without colour.
/// </summary>
public sealed class StateColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "ready" or "true" or "done" => Color.FromArgb("#4CC38A"),
            "blocked" or "false" => Color.FromArgb("#E5484D"),
            "current" => Color.FromArgb("#E2A336"),
            _ => Color.FromArgb("#9AA6A0")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
