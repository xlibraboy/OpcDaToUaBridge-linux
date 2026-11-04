using Microsoft.Maui.Controls.Xaml;

namespace OpcBridge.Mobile;

/// <summary>
/// The icon drawn for the current surface: <c>{theme:ThemeIcon icon_refresh}</c> picks the
/// dark-surface file on the dark theme and its <c>_light</c> ink variant on the light one.
/// The binding is the declared return type so compiled XAML applies it as a binding, not as a
/// plain value.
/// </summary>
[ContentProperty(nameof(Name))]
public sealed class ThemeIconExtension : IMarkupExtension<BindingBase>
{
    public string Name { get; set; } = string.Empty;

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        (BindingBase)new AppThemeBindingExtension
        {
            Light = ImageSource.FromFile(Name + "_light.png"),
            Dark = ImageSource.FromFile(Name + ".png")
        }.ProvideValue(serviceProvider);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) =>
        ProvideValue(serviceProvider);
}
