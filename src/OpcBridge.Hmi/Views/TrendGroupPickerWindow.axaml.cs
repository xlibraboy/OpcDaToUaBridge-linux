using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels;

namespace OpcBridge.Hmi.Views;

public partial class TrendGroupPickerWindow : Window
{
    public TrendGroupPickerWindow()
    {
        InitializeComponent();
    }

    private void OnOpenTrendClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            List<TagItemViewModel> tags = (TagList.SelectedItems ?? Array.Empty<object>())
                .OfType<TagItemViewModel>()
                .ToList();
            if (tags.Count > 0)
            {
                vm.OpenTrendGroup(tags);
            }
        }

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}