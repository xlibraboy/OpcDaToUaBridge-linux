using Microsoft.Maui.ApplicationModel;

namespace OpcBridge.Mobile;

/// <summary>
/// The viewer's two surfaces. The dark palette is the plant-floor ground the console was drawn
/// on; the light palette keeps the same roles for daylight and glare, with the state colours
/// darkened so every state word still carries its contrast against a white card. Colours are
/// cached: the converters and theme bindings ask for them on every row, so a scroll must not
/// allocate a new Color each time.
/// </summary>
public static class ThemePalette
{
    private static readonly Color UnknownColor = Color.FromArgb("#FF00FF");

    private static readonly Dictionary<string, Color> LightColors = new()
    {
        ["PageBg"] = Color.FromArgb("#F2F6F3"),
        ["SurfaceColor"] = Color.FromArgb("#FFFFFF"),
        ["SurfaceAltColor"] = Color.FromArgb("#E8EFEA"),
        ["BorderColor"] = Color.FromArgb("#D2DDD6"),
        ["TextColor"] = Color.FromArgb("#101915"),
        ["MutedColor"] = Color.FromArgb("#5B6A63"),
        ["GoodColor"] = Color.FromArgb("#157A4B"),
        ["BadColor"] = Color.FromArgb("#C0322F"),
        ["WarnColor"] = Color.FromArgb("#966200"),
        ["GoodBgColor"] = Color.FromArgb("#EAF2EC"),
        ["GoodBorderColor"] = Color.FromArgb("#BCD3C4"),
        ["GoodTextColor"] = Color.FromArgb("#0B4D2C"),
        ["BadBgColor"] = Color.FromArgb("#F7E9E8"),
        ["BadBorderColor"] = Color.FromArgb("#E0B6B2"),
        ["BadTextColor"] = Color.FromArgb("#8A1C14"),
        ["WarnBgColor"] = Color.FromArgb("#F8F1E2"),
        ["WarnBorderColor"] = Color.FromArgb("#DDC9A0"),
        ["WarnTextColor"] = Color.FromArgb("#6B4500"),
        ["UsageHotColor"] = Color.FromArgb("#1F5FAF"),
        ["UsageWarmColor"] = Color.FromArgb("#6B9BD8"),
        ["UsageCoolColor"] = Color.FromArgb("#B7CDE8"),
        ["UsageNoneColor"] = Color.FromArgb("#AEB7B2"),
        ["OnPrimaryColor"] = Color.FromArgb("#FFFFFF"),
        ["TabBarColor"] = Color.FromArgb("#FFFFFF"),
        ["ScrimColor"] = Color.FromArgb("#66000000")
    };

    private static readonly Dictionary<string, Color> DarkColors = new()
    {
        ["PageBg"] = Color.FromArgb("#0C1210"),
        ["SurfaceColor"] = Color.FromArgb("#151D1A"),
        ["SurfaceAltColor"] = Color.FromArgb("#1D2723"),
        ["BorderColor"] = Color.FromArgb("#2A3631"),
        ["TextColor"] = Color.FromArgb("#F1F5F3"),
        ["MutedColor"] = Color.FromArgb("#93A19B"),
        ["GoodColor"] = Color.FromArgb("#4CC38A"),
        ["BadColor"] = Color.FromArgb("#E5484D"),
        ["WarnColor"] = Color.FromArgb("#E2A336"),
        ["GoodBgColor"] = Color.FromArgb("#12251A"),
        ["GoodBorderColor"] = Color.FromArgb("#204632"),
        ["GoodTextColor"] = Color.FromArgb("#4CC38A"),
        ["BadBgColor"] = Color.FromArgb("#291516"),
        ["BadBorderColor"] = Color.FromArgb("#52292A"),
        ["BadTextColor"] = Color.FromArgb("#E5484D"),
        ["WarnBgColor"] = Color.FromArgb("#292112"),
        ["WarnBorderColor"] = Color.FromArgb("#504020"),
        ["WarnTextColor"] = Color.FromArgb("#E2A336"),
        ["UsageHotColor"] = Color.FromArgb("#7AA2FF"),
        ["UsageWarmColor"] = Color.FromArgb("#5F8ED8"),
        ["UsageCoolColor"] = Color.FromArgb("#3D5F8A"),
        ["UsageNoneColor"] = Color.FromArgb("#56645D"),
        ["OnPrimaryColor"] = Color.FromArgb("#07130D"),
        ["TabBarColor"] = Color.FromArgb("#101714"),
        ["ScrimColor"] = Color.FromArgb("#CC0C1210")
    };

    public static bool IsLight => Application.Current?.RequestedTheme == AppTheme.Light;

    public static Color Get(string key) => IsLight ? Light(key) : Dark(key);

    public static Color Light(string key) => LightColors.TryGetValue(key, out Color? color) ? color : UnknownColor;

    public static Color Dark(string key) => DarkColors.TryGetValue(key, out Color? color) ? color : UnknownColor;
}
