using Microsoft.Maui.ApplicationModel;

namespace OpcBridge.Mobile;

/// <summary>
/// The viewer's two surfaces. The dark palette is the plant-floor ground the console was drawn
/// on; the light palette keeps the same roles for daylight and glare, with the state colours
/// darkened so every state word still carries its contrast against a white card.
/// </summary>
public static class ThemePalette
{
    public static bool IsLight => Application.Current?.RequestedTheme == AppTheme.Light;

    public static Color Get(string key) => IsLight ? Light(key) : Dark(key);

    public static Color Light(string key) => key switch
    {
        "PageBg" => Color.FromArgb("#F2F6F3"),
        "SurfaceColor" => Color.FromArgb("#FFFFFF"),
        "SurfaceAltColor" => Color.FromArgb("#E8EFEA"),
        "BorderColor" => Color.FromArgb("#D2DDD6"),
        "TextColor" => Color.FromArgb("#101915"),
        "MutedColor" => Color.FromArgb("#5B6A63"),
        "GoodColor" => Color.FromArgb("#157A4B"),
        "BadColor" => Color.FromArgb("#C0322F"),
        "WarnColor" => Color.FromArgb("#966200"),
        "OnPrimaryColor" => Color.FromArgb("#FFFFFF"),
        "TabBarColor" => Color.FromArgb("#FFFFFF"),
        "ScrimColor" => Color.FromArgb("#66000000"),
        _ => Color.FromArgb("#FF00FF")
    };

    public static Color Dark(string key) => key switch
    {
        "PageBg" => Color.FromArgb("#0C1210"),
        "SurfaceColor" => Color.FromArgb("#151D1A"),
        "SurfaceAltColor" => Color.FromArgb("#1D2723"),
        "BorderColor" => Color.FromArgb("#2A3631"),
        "TextColor" => Color.FromArgb("#F1F5F3"),
        "MutedColor" => Color.FromArgb("#93A19B"),
        "GoodColor" => Color.FromArgb("#4CC38A"),
        "BadColor" => Color.FromArgb("#E5484D"),
        "WarnColor" => Color.FromArgb("#E2A336"),
        "OnPrimaryColor" => Color.FromArgb("#07130D"),
        "TabBarColor" => Color.FromArgb("#101714"),
        "ScrimColor" => Color.FromArgb("#CC0C1210"),
        _ => Color.FromArgb("#FF00FF")
    };
}
