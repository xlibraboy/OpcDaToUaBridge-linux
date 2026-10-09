using System.Globalization;
using OpcBridge.Client;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

[QueryProperty(nameof(BlockId), "id")]
public partial class LogicBlockPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private LogicBlockDetailViewModel? viewModel_;
    private Guid blockId_;

    public LogicBlockPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        coordinator_.Changed += OnLiveChanged;
    }

    /// <summary>The bridge whose block this page shows (the active one).</summary>
    private BridgeConnection? Active => coordinator_.Active;

    /// <summary>Route parameter from <c>block?id=…</c>.</summary>
    public string BlockId
    {
        set
        {
            if (Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out Guid id))
            {
                blockId_ = id;
                _ = LoadAsync();
            }
        }
    }

    private async Task LoadAsync()
    {
        if (Active is null)
        {
            titleLabel.Text = "No bridge selected";
            messageLabel.Text = "Pick a bridge on the Logic tab.";
            return;
        }

        LogicBlockDto? block = Active.Overview.Definitions.FirstOrDefault(candidate => candidate.Id == blockId_);
        if (block is null)
        {
            await Active.RefreshAsync(CancellationToken.None);
            block = Active.Overview.Definitions.FirstOrDefault(candidate => candidate.Id == blockId_);
        }

        if (block is null)
        {
            titleLabel.Text = "Block not found";
            messageLabel.Text = "Refresh the list on the Logic tab.";
            return;
        }

        MobileResult<IReadOnlyList<LogicNoteDto>> notes = await Active.Api.GetNotesAsync(blockId_, 50, CancellationToken.None);
        viewModel_ = new LogicBlockDetailViewModel(
            block,
            Active.CacheKey,
            Active.Overview.State?.Blocks.FirstOrDefault(candidate => candidate.Id == blockId_),
            coordinator_.Tags,
            notes.Ok ? notes.Value : null);
        viewModel_.ActionRequested += OnActionRequested;

        BindingContext = viewModel_;
        Title = block.Name;
        titleLabel.Text = block.Name;
        descriptionLabel.Text = block.Description;
        Repaint();
    }

    private void OnLiveChanged()
    {
        MainThread.BeginInvokeOnMainThread(Repaint);
    }

    private void Repaint()
    {
        if (viewModel_ is null || Active is null)
        {
            return;
        }

        LogicBlockStateDto? blockState = Active.Overview.State?.Blocks
            .FirstOrDefault(candidate => candidate.Id == blockId_);
        viewModel_.ApplyState(blockState);
        viewModel_.ApplyValues();
        offlineLabel.IsVisible = Active.IsOffline;
        offlineLabel.Text = Active.OfflineText;
        stateLabel.Text = viewModel_.StateLabel;
        stateLabel.TextColor = (Color)new StateColorConverter()
            .Convert(viewModel_.StateKey, typeof(Color), null, CultureInfo.InvariantCulture);
        blockedLabel.Text = viewModel_.BlockedByText;
    }

    private async void OnActionClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not LogicActionButtonViewModel action || viewModel_ is null)
        {
            return;
        }

        if (action.Confirm)
        {
            bool confirmed = await DisplayAlert(
                action.Label,
                $"Write \"{action.Action.Value}\" to {action.TagLabel}?",
                "Write",
                "Cancel");
            if (!confirmed)
            {
                return;
            }
        }

        viewModel_.RunActionCommand.Execute(action);
    }

    private async void OnActionRequested(LogicActionButtonViewModel action)
    {
        if (Active is null)
        {
            return;
        }

        HmiWriteResponse result = await Active.Api.WriteAsync(
            new HmiWriteRequest
            {
                SourceId = action.Action.SourceId,
                ItemId = action.Action.ItemId,
                Value = ParseValue(action.Action.Value)
            },
            CancellationToken.None);

        messageLabel.Text = result.Ok
            ? "✓ " + action.Label + " written"
            : "✗ " + (result.Error ?? "write failed");
    }

    private async void OnAddNoteClicked(object? sender, EventArgs e)
    {
        string text = noteEntry.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || viewModel_ is null || Active is null)
        {
            return;
        }

        MobileResult<LogicNoteDto> result = await Active.Api.AddNoteAsync(
            new LogicNoteAddRequest { BlockId = blockId_, Text = text },
            CancellationToken.None);
        if (!result.Ok)
        {
            messageLabel.Text = "✗ " + (result.Error ?? "note failed");
            return;
        }

        noteEntry.Text = string.Empty;
        MobileResult<IReadOnlyList<LogicNoteDto>> notes = await Active.Api.GetNotesAsync(blockId_, 50, CancellationToken.None);
        if (notes.Ok)
        {
            viewModel_.SetNotes(notes.Value!);
        }

        messageLabel.Text = "✓ note added";
    }

    private static object ParseValue(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (bool.TryParse(text, out bool flag))
        {
            return flag;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return number;
        }

        return text;
    }
}
