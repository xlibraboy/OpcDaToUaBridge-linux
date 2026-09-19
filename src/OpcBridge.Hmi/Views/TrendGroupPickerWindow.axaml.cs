using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// Trend-group tag picker. Lists tag metadata only (no live values), so a multi-select is not
/// disturbed by the value stream; the confirmed selection is handed back through
/// <see cref="TagPickerViewModel.Result"/>.
/// </summary>
public partial class TrendGroupPickerWindow : Window
{
    public TrendGroupPickerWindow()
    {
        InitializeComponent();

        // Inner controls claim Escape before it bubbles, so the window watches the tunnel pass.
        AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Tunnel);
    }

    public TrendGroupPickerWindow(TagPickerViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        TagList.SelectionChanged += (_, _) => viewModel.SetSelectedCount(TagList.SelectedItems?.Count ?? 0);
    }

    /// <summary>
    /// Shows the picker and runs <paramref name="onConfirmed"/> when the operator confirms a
    /// selection (cancel/close runs nothing).
    /// </summary>
    public static void ShowFor(Window? owner, TagPickerViewModel viewModel, Action<TagPickerResult> onConfirmed)
    {
        var window = new TrendGroupPickerWindow(viewModel);
        window.Closed += (_, _) =>
        {
            if (viewModel.Result is { } result)
            {
                onConfirmed(result);
            }
        };

        if (owner is null)
        {
            window.Show();
        }
        else
        {
            window.ShowDialog(owner);
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TagPickerViewModel viewModel && viewModel.CanConfirm)
        {
            var keys = (TagList.SelectedItems ?? Array.Empty<object>())
                .OfType<TagListRow>()
                .Where(row => row.Enabled)
                .Select(row => row.Key)
                .ToList();
            if (keys.Count > 0)
            {
                viewModel.Result = new TagPickerResult(
                    keys,
                    viewModel.AskForName ? viewModel.GroupName.Trim() : null);
            }
        }

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Escape cancels, the same way the Cancel button does.</summary>
    private void OnEscapeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }
}
