using System.Globalization;

namespace OpcBridge.Mobile;

/// <summary>
/// Maps an element/checkpoint state key to the tinted surface used by the V3 result rows:
/// <c>bg</c>, <c>border</c> or <c>text</c>. A checkpoint count also treats "no data" as the
/// amber warn tone, as the web Result rail does; an element's unknown state is warn too.
/// </summary>
public sealed class StateTintColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value as string ?? string.Empty;
        string part = (parameter as string ?? "bg").Trim().ToLowerInvariant();
        string tone = key switch
        {
            "ready" or "true" or "done" or "match" or "synced" or "good" => "good",
            "blocked" or "false" or "error" or "poor" => "bad",
            "current" or "mismatch" or "syncing" or "pending" or "fair" or "warn" or "unknown" or "" => "warn",
            _ => "neutral"
        };

        return tone switch
        {
            "good" => ThemePalette.Get(part switch
            {
                "border" => "GoodBorderColor",
                "text" => "GoodTextColor",
                _ => "GoodBgColor"
            }),
            "bad" => ThemePalette.Get(part switch
            {
                "border" => "BadBorderColor",
                "text" => "BadTextColor",
                _ => "BadBgColor"
            }),
            "warn" => ThemePalette.Get(part switch
            {
                "border" => "WarnBorderColor",
                "text" => "WarnTextColor",
                _ => "WarnBgColor"
            }),
            _ => ThemePalette.Get(part switch
            {
                "border" => "BorderColor",
                "text" => "MutedColor",
                _ => "SurfaceAltColor"
            })
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
