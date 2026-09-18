using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// Saved trend groups: the group list, the tags of the selected group, and the buttons that
/// create, rename, fill, empty and open a group.
/// </summary>
public partial class TrendGroupsPage : UserControl
{
    public TrendGroupsPage()
    {
        InitializeComponent();
    }

    /// <summary>The name box persists when it loses focus, so typing does not rewrite the file per keystroke.</summary>
    private void OnGroupNameLostFocus(object? sender, RoutedEventArgs e) => SaveTrendGroups();

    private void OnGroupNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveTrendGroups();
        }
    }

    private void SaveTrendGroups() => (DataContext as MainViewModel)?.SaveTrendGroups();
}
