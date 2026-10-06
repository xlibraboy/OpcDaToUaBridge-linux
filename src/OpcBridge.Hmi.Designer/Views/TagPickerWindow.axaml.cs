using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Designer.ViewModels;

namespace OpcBridge.Hmi.Designer.Views;

/// <summary>
/// Tag picker for binding a widget: one source's mapped tags, filtered by typing, with live
/// values that refresh while the dialog is open. Returns the tag to bind, or null on cancel.
/// </summary>
public partial class TagPickerWindow : Window
{
    public TagPickerWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FilterBox.Focus();
    }

    private DesignerTagPickerViewModel? ViewModel => DataContext as DesignerTagPickerViewModel;

    public static async Task<TagBindingKey?> ShowForAsync(
        Window owner,
        DesignerTagPickerViewModel viewModel,
        DesignerViewModel? designer)
    {
        var window = new TagPickerWindow { DataContext = viewModel };

        void OnLiveValuesChanged() => Dispatcher.UIThread.Post(viewModel.RefreshLive);
        if (designer is not null)
        {
            designer.LiveValuesChanged += OnLiveValuesChanged;
        }

        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            if (designer is not null)
            {
                designer.LiveValuesChanged -= OnLiveValuesChanged;
            }
        }

        return viewModel.Result;
    }

    private void OnBindClick(object? sender, RoutedEventArgs e) => Confirm();

    private void OnTagDoubleTapped(object? sender, TappedEventArgs e) => Confirm();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
    }

    private void Confirm()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.Confirm();
        if (vm.Result is not null)
        {
            Close();
        }
    }
}
