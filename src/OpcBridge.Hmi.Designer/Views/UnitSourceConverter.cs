using System.Globalization;
using Avalonia.Data.Converters;

namespace OpcBridge.Hmi.Designer.Views;

/// <summary>
/// Converts between "server"/"manual" string and ComboBox selected index (0=From Dashboard, 1=Manual).
/// </summary>
public sealed class UnitSourceConverter : IValueConverter
{
    public static readonly UnitSourceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.Equals(value as string, "server", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is int index && index == 0 ? "server" : "manual";
    }
}
