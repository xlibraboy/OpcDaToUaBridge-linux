using Microsoft.Maui.Controls.Xaml;

namespace OpcBridge.Mobile;

/// <summary>
/// A palette colour that follows the selected theme: <c>{theme:ThemeColor MutedColor}</c> is
/// the muted colour of the current surface. It builds the same theme binding as the built-in
/// <c>{AppThemeBinding}</c>, so a live theme change repaints every use without rebuilding the page.
/// The binding is the declared return type so compiled XAML applies it as a binding, not as a
/// plain value.
/// </summary>
[ContentProperty(nameof(Key))]
public sealed class ThemeColorExtension : IMarkupExtension<BindingBase>
{
    public string Key { get; set; } = string.Empty;

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        (BindingBase)new AppThemeBindingExtension
        {
            Light = ThemePalette.Light(Key),
            Dark = ThemePalette.Dark(Key)
        }.ProvideValue(serviceProvider);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) =>
        ProvideValue(serviceProvider);
}
