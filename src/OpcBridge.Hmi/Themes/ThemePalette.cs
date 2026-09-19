using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpcBridge.Hmi.Themes;

/// <summary>
/// Resolves the shared palette for the views that draw themselves in code rather than in
/// XAML (the trend chart, the display surface, the quality lamp).
///
/// Themes/SharedResources.axaml stays the single source of truth: every colour is declared
/// there once, and this class hands the same brush to code. The fallbacks exist only so a
/// control renders something sane if it is ever drawn outside an application host.
/// </summary>
internal static class ThemePalette
{
    private static readonly ConcurrentDictionary<string, IBrush> Cache = new();

    /// <summary>The colour behind a resource key, or <paramref name="fallback"/> if absent.</summary>
    public static Color Color(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key, out object? value) == true)
        {
            switch (value)
            {
                case Color color:
                    return color;
                case ISolidColorBrush brush:
                    return brush.Color;
            }
        }

        return Avalonia.Media.Color.Parse(fallback);
    }

    /// <summary>The brush behind a resource key, resolved once and reused.</summary>
    public static IBrush Brush(string key, string fallback)
    {
        if (Cache.TryGetValue(key, out IBrush? cached))
        {
            return cached;
        }

        IBrush resolved = new SolidColorBrush(Color(key, fallback));
        Cache[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// The brush behind a resource key at a given opacity, for the overlay fills the chart
    /// layers over its own drawing (readout chips, zoom band, alarm band).
    /// </summary>
    public static IBrush Brush(string key, double opacity, string fallback)
    {
        string cacheKey = $"{key}@{opacity}";
        if (Cache.TryGetValue(cacheKey, out IBrush? cached))
        {
            return cached;
        }

        IBrush resolved = new SolidColorBrush(Color(key, fallback), opacity);
        Cache[cacheKey] = resolved;
        return resolved;
    }
}
