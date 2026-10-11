using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels.Logic;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// The block management window: every block under its interlock group heading with the live
/// state, the place to create and delete blocks (the page's rail picks the block to edit).
/// </summary>
public partial class LogicBlocksWindow : Window
{
    private static LogicBlocksWindow? current_;

    public LogicBlocksWindow()
    {
        InitializeComponent();
    }

    public static void ShowFor(Window? owner, LogicPanelViewModel panel)
    {
        if (current_ is { } open)
        {
            open.Activate();
            return;
        }

        var window = new LogicBlocksWindow { DataContext = panel };
        current_ = window;
        window.Closed += (_, _) => current_ = null;
        if (owner is null)
        {
            window.Show();
        }
        else
        {
            window.Show(owner);
        }
    }

    private async void OnBlockRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LogicBlockRowViewModel row } && DataContext is LogicPanelViewModel panel
            && await panel.TrySelectBlockAsync(row))
        {
            Close();
        }
    }

    private void OnNewBlockClick(object? sender, RoutedEventArgs e) => Close();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
