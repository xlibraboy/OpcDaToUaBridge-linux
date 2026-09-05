using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Converts a "#RRGGBB" hex string to an <see cref="IBrush"/> for the pen-table color
/// swatches; parse failures fall back to the app's accent blue.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return new SolidColorBrush(Color.Parse(hex.Trim()));
            }
            catch (FormatException)
            {
            }
        }

        return new SolidColorBrush(Color.Parse("#4FC3F7"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is SolidColorBrush brush ? brush.Color.ToString() : AvaloniaProperty.UnsetValue;
    }
}
