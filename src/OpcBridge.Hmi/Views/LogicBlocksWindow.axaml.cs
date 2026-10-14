using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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

        // Open with the block list focused, so the list is reachable without a pointer.
        Opened += (_, _) =>
        {
            Control? first = this.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => control.Focusable && control.IsEffectivelyVisible);
            first?.Focus();
        };
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
        if (sender is not Button { Tag: LogicBlockRowViewModel row })
        {
            return;
        }

        if (DataContext is LogicPanelViewModel panel && await panel.TrySelectBlockAsync(row))
        {
            Close();
        }
    }

    private async void OnNewBlockClick(object? sender, RoutedEventArgs e)
    {
        // The New-block dialog (name, group and the AREA first) closes this window on success.
        if (DataContext is LogicPanelViewModel panel && await panel.StartNewBlockAsync(this))
        {
            Close();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
