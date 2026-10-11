using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels.Logic;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// The runtime's plant-logic page: the block editor and the evaluated result, per bridge.
/// Opened from View ▸ Logic (Ctrl+4).
/// </summary>
public partial class LogicPage : UserControl
{
    public LogicPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Wire();
    }

    private LogicPanelViewModel? Panel => DataContext as LogicPanelViewModel;

    private void Wire()
    {
        if (Panel is { } panel)
        {
            // The page owns the dialogs; the view model only asks.
            panel.ConfirmAsync ??= (title, message, confirm) =>
                ConfirmWindow.ShowFor(TopLevel.GetTopLevel(this) as Window, title, message, confirm);
        }
    }

    private void OnBlocksClick(object? sender, RoutedEventArgs e)
    {
        if (Panel is { } panel)
        {
            LogicBlocksWindow.ShowFor(TopLevel.GetTopLevel(this) as Window, panel);
        }
    }

    private void OnGuideClick(object? sender, RoutedEventArgs e) =>
        LogicGuideWindow.ShowFor(TopLevel.GetTopLevel(this) as Window);
}
