namespace OpcBridge.Mobile;

/// <summary>The operator's appearance choice; System follows the phone's day/night setting.</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// Owns the appearance preference: it is applied before the first page is built, survives app
/// restarts, and {theme:ThemeColor} bindings follow it on their own. Pages that set colours from
/// code subscribe to <see cref="Changed"/> and repaint, because their values do not re-evaluate
/// with the theme.
/// </summary>
public static class ThemeManager
{
    private const string PreferenceKey = "appearance.theme";

    public static event Action? Changed;

    public static ThemeMode Mode
    {
        get => Enum.TryParse(
            Preferences.Default.Get(PreferenceKey, nameof(ThemeMode.System)),
            out ThemeMode mode)
            ? mode
            : ThemeMode.System;
    }

    public static void Apply()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.UserAppTheme = Mode switch
        {
            ThemeMode.Light => AppTheme.Light,
            ThemeMode.Dark => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    public static void SetMode(ThemeMode mode)
    {
        Preferences.Default.Set(PreferenceKey, mode.ToString());
        Apply();
        NotifyChanged();
    }

    public static void NotifyChanged() => Changed?.Invoke();
}
