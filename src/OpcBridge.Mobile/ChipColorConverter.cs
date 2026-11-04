using System.Globalization;

namespace OpcBridge.Mobile;

/// <summary>
/// A filter chip's colours from its <c>IsSelected</c> flag: a selected chip is the surface's ink
/// with reverse text, an unselected one the raised surface with muted text — so the selection
/// reads without relying on colour alone. ConverterParameter "text" picks the text colour;
/// anything else the background.
/// </summary>
public sealed class ChipColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool selected = value is true;
        return (parameter as string) switch
        {
            "text" => ThemePalette.Get(selected ? "PageBg" : "MutedColor"),
            _ => ThemePalette.Get(selected ? "TextColor" : "SurfaceAltColor")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
