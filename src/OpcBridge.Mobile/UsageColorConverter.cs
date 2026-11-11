using System.Globalization;

namespace OpcBridge.Mobile;

/// <summary>
/// Maps a checkpoint's usage heat (<c>hot</c>, <c>warm</c>, <c>cool</c>, <c>never</c>,
/// <c>none</c>) to the fill, stroke, thickness or text colour of the 4px usage dot and the
/// sheet's count. <c>none</c> is a check with no tag source: a hollow dot, never a heat colour;
/// <c>never</c> is a source-backed check with no activation and draws nothing.
/// </summary>
public sealed class UsageColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value as string ?? "none";
        string part = (parameter as string ?? "fill").Trim().ToLowerInvariant();

        if (key == "none")
        {
            return part switch
            {
                "stroke" => ThemePalette.Get("UsageNoneColor"),
                "thickness" => 1.0,
                "text" => ThemePalette.Get("MutedColor"),
                _ => ThemePalette.Get("SurfaceColor")
            };
        }

        if (key == "never")
        {
            return part switch
            {
                "thickness" => 0.0,
                "text" => ThemePalette.Get("MutedColor"),
                _ => Colors.Transparent
            };
        }

        string color = key switch
        {
            "hot" => "UsageHotColor",
            "warm" => "UsageWarmColor",
            _ => "UsageCoolColor"
        };

        return part == "thickness" ? 0.0 : ThemePalette.Get(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
