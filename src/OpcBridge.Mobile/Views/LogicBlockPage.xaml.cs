using System.Globalization;
using OpcBridge.Client;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

[QueryProperty(nameof(BlockId), "id")]
public partial class LogicBlockPage : ContentPage
{
    private readonly AppState state_;
    private LogicBlockDetailViewModel? viewModel_;
    private Guid blockId_;

    public LogicBlockPage(AppState state)
    {
        InitializeComponent();
        state_ = state;
        state_.Changed += OnLiveChanged;
    }

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
        LogicBlockDto? block = state_.Overview.Definitions.FirstOrDefault(candidate => candidate.Id == blockId_);
        if (block is null)
        {
            await state_.RefreshAsync(CancellationToken.None);
            block = state_.Overview.Definitions.FirstOrDefault(candidate => candidate.Id == blockId_);
        }

        if (block is null)
        {
            titleLabel.Text = "Block not found";
            messageLabel.Text = "Refresh the list on the Logic tab.";
            return;
        }

        MobileResult<IReadOnlyList<LogicNoteDto>> notes = await state_.Api.GetNotesAsync(blockId_, 50, CancellationToken.None);
        viewModel_ = new LogicBlockDetailViewModel(
            block,
            state_.Overview.State?.Blocks.FirstOrDefault(candidate => candidate.Id == blockId_),
            state_.Tags,
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
        if (viewModel_ is null)
        {
            return;
        }

        LogicBlockStateDto? blockState = state_.Overview.State?.Blocks
            .FirstOrDefault(candidate => candidate.Id == blockId_);
        viewModel_.ApplyState(blockState);
        viewModel_.ApplyValues();
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
        HmiWriteResponse result = await state_.Api.WriteAsync(
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
        if (text.Length == 0 || viewModel_ is null)
        {
            return;
        }

        MobileResult<LogicNoteDto> result = await state_.Api.AddNoteAsync(
            new LogicNoteAddRequest { BlockId = blockId_, Text = text },
            CancellationToken.None);
        if (!result.Ok)
        {
            messageLabel.Text = "✗ " + (result.Error ?? "note failed");
            return;
        }

        noteEntry.Text = string.Empty;
        MobileResult<IReadOnlyList<LogicNoteDto>> notes = await state_.Api.GetNotesAsync(blockId_, 50, CancellationToken.None);
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
