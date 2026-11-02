using System.Globalization;

namespace OpcBridge.Mobile;

/// <summary>
/// A filter chip's colours from its <c>IsSelected</c> flag: a selected chip is a filled
/// light chip with dark text, an unselected one a dark chip with muted text — so the
/// selection reads without relying on colour alone. ConverterParameter "text" picks the
/// text colour; anything else the background.
/// </summary>
public sealed class ChipColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool selected = value is true;
        return (parameter as string) switch
        {
            "text" => selected ? Color.FromArgb("#0C1210") : Color.FromArgb("#93A19B"),
            _ => selected ? Color.FromArgb("#F1F5F3") : Color.FromArgb("#1D2723")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
