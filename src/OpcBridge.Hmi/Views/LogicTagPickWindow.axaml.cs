using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels.Logic;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// The Tags tab's "From bridge…" picker. It lists the bridge's mapped tags — source, tag and the
/// reading each had when the window opened — so several signals can be added to the area's tags in
/// one step; the ones the list already holds are disabled. The confirmed rows are handed back
/// through <see cref="LogicTagPickViewModel.Result"/>.
/// </summary>
public partial class LogicTagPickWindow : Window
{
    public LogicTagPickWindow()
    {
        InitializeComponent();

        // Open with the filter focused: narrowing the list is the first step of picking tags.
        Opened += (_, _) => FilterBox.Focus();

        // Inner controls claim Escape before it bubbles, so the window watches the tunnel pass.
        AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Tunnel);
    }

    public LogicTagPickWindow(LogicTagPickViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        TagList.SelectionChanged += (_, _) => viewModel.SetSelectedCount(TagList.SelectedItems?.Count ?? 0);
    }

    /// <summary>
    /// Shows the picker and runs <paramref name="onConfirmed"/> when the operator confirms a
    /// selection (cancel/close runs nothing).
    /// </summary>
    public static void ShowFor(
        Window? owner,
        LogicTagPickViewModel viewModel,
        Action<IReadOnlyList<LogicTagPickRow>> onConfirmed)
    {
        var window = new LogicTagPickWindow(viewModel);
        window.Closed += (_, _) =>
        {
            if (viewModel.Result is { } tags)
            {
                onConfirmed(tags);
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
        if (DataContext is LogicTagPickViewModel viewModel && viewModel.CanConfirm)
        {
            var tags = (TagList.SelectedItems ?? Array.Empty<object>())
                .OfType<LogicTagPickRow>()
                .Where(row => row.Enabled)
                .ToList();
            if (tags.Count > 0)
            {
                viewModel.Result = tags;
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
