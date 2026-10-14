using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcBridge.Hmi.ViewModels.Logic;

namespace OpcBridge.Hmi.Views;

/// <summary>
/// The New-block dialog: the block's name, its interlock group and — first, because it is the
/// tag-sharing scope — its AREA. The editor opens with the area already set, so every tag picked
/// afterwards joins the area's tags and is shared by the area's other blocks.
/// </summary>
public partial class LogicNewBlockWindow : Window
{
    private LogicNewBlockDraft? result_;

    public LogicNewBlockWindow()
    {
        InitializeComponent();
        NameBox.TextChanged += (_, _) => UpdateCreateEnabled();
        AreaBox.TextChanged += (_, _) => UpdateCreateEnabled();
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>Asks for a new block; null when the operator cancelled.</summary>
    public static async Task<LogicNewBlockDraft?> ShowFor(Window? owner, LogicNewBlockRequest request)
    {
        var window = new LogicNewBlockWindow();
        window.GroupBox.ItemsSource = request.Groups;
        window.AreaBox.ItemsSource = request.Areas;
        window.GroupBox.Text = request.Group;
        window.AreaBox.Text = request.Area;
        window.UpdateCreateEnabled();

        if (owner is not null)
        {
            return await window.ShowDialog<LogicNewBlockDraft?>(owner);
        }

        var closed = new TaskCompletionSource<LogicNewBlockDraft?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult(window.result_);
        window.Show();
        return await closed.Task;
    }

    private bool CanCreate() =>
        !string.IsNullOrWhiteSpace(NameBox.Text) && !string.IsNullOrWhiteSpace(AreaBox.Text);

    private void UpdateCreateEnabled() => CreateButton.IsEnabled = CanCreate();

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (!CanCreate())
        {
            return;
        }

        result_ = new LogicNewBlockDraft(
            NameBox.Text!.Trim(),
            GroupBox.Text?.Trim() ?? string.Empty,
            AreaBox.Text!.Trim());
        Close(result_);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
