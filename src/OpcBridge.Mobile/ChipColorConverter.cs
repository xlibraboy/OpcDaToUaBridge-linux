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
            "text" => selected ? Color.FromArgb("#0F1513") : Color.FromArgb("#9AA6A0"),
            _ => selected ? Color.FromArgb("#E9EFEC") : Color.FromArgb("#19211E")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
