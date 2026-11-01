using System.Globalization;
using OpcBridge.Client;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

[QueryProperty(nameof(BlockId), "id")]
public partial class LogicBlockPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private readonly Dictionary<Guid, Border> elementViews_ = new();
    private LogicBlockDetailViewModel? viewModel_;
    private Guid blockId_;
    private Border? flashedRow_;
    private CancellationTokenSource? flashToken_;

    public LogicBlockPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        coordinator_.Changed += OnLiveChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // The ctor subscribes for the earliest repaint; re-subscribing here is idempotent and
        // keeps a page that was navigated away from and back alive to live updates.
        coordinator_.Changed -= OnLiveChanged;
        coordinator_.Changed += OnLiveChanged;
    }

    protected override void OnDisappearing()
    {
        // The page is transient (a new instance per navigation): without this the singleton
        // coordinator would keep every visited block page alive and repainting.
        coordinator_.Changed -= OnLiveChanged;
        flashToken_?.Cancel();
        flashToken_ = null;
        if (flashedRow_ is not null)
        {
            RestoreRow(flashedRow_);
            flashedRow_ = null;
        }

        base.OnDisappearing();
    }

    /// <summary>
    /// Keeps the row's view, so a checkpoint dot can scroll to the parent it names. The network
    /// is a non-virtualized BindableLayout, so every row is realized on load.
    /// </summary>
    private void OnElementRowLoaded(object? sender, EventArgs e)
    {
        if (sender is Border border && border.BindingContext is LogicElementRowViewModel row)
        {
            elementViews_[row.Id] = border;
        }
    }

    private void OnElementRowUnloaded(object? sender, EventArgs e)
    {
        if (sender is Border border &&
            border.BindingContext is LogicElementRowViewModel row &&
            elementViews_.TryGetValue(row.Id, out Border? known) &&
            ReferenceEquals(known, border))
        {
            elementViews_.Remove(row.Id);
        }
    }

    /// <summary>
    /// A checkpoint dot is a jump: its evaluated row is scrolled to the middle of the screen and
    /// ringed for a moment, so "which group is open" and "where is it" are one tap.
    /// </summary>
    private async void OnCheckpointTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not LogicCheckpointViewModel checkpoint ||
            !elementViews_.TryGetValue(checkpoint.Id, out Border? row))
        {
            return;
        }

        _ = FlashRowAsync(row);
        await pageScroll.ScrollToAsync(row, ScrollToPosition.Center, true);
    }

    private async Task FlashRowAsync(Border row)
    {
        if (flashedRow_ is not null && !ReferenceEquals(flashedRow_, row))
        {
            RestoreRow(flashedRow_);
        }

        flashToken_?.Cancel();
        CancellationTokenSource token = new();
        flashToken_ = token;
        flashedRow_ = row;
        row.Stroke = new SolidColorBrush(ResourceColor("WarnColor", "#E2A336"));
        row.StrokeThickness = 2;

        try
        {
            await Task.Delay(1400, token.Token);
            if (ReferenceEquals(flashedRow_, row))
            {
                RestoreRow(row);
                flashedRow_ = null;
            }
        }
        catch (TaskCanceledException)
        {
            // A newer tap (or leaving the page) owns the ring now.
        }
        finally
        {
            if (ReferenceEquals(flashToken_, token))
            {
                flashToken_ = null;
            }

            token.Dispose();
        }
    }

    private static void RestoreRow(Border row)
    {
        row.Stroke = new SolidColorBrush(ResourceColor("BorderColor", "#2C3633"));
        row.StrokeThickness = 1;
    }

    private static Color ResourceColor(string key, string fallback)
    {
        if (Application.Current is { } app &&
            app.Resources.TryGetValue(key, out object? value) &&
            value is Color color)
        {
            return color;
        }

        return Color.FromArgb(fallback);
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
        if (block is null && Active.IsConnected)
        {
            // The id is not in the last list; one refresh can recover a list that is stale.
            await Active.RefreshAsync(CancellationToken.None);
            block = Active.Overview.Definitions.FirstOrDefault(candidate => candidate.Id == blockId_);
        }

        if (block is null)
        {
            titleLabel.Text = "Block not found";
            messageLabel.Text = Active.IsConnected
                ? "The block is not in the bridge's list — it may have been deleted."
                : "This block is not in the last synced list — connect once to refresh.";
            return;
        }

        // Everything on screen comes from memory first — the definitions, state and values the
        // cache holds, and the notes last served — so the block opens instantly and reads fully
        // while the link is down, instead of waiting on a network call.
        viewModel_ = new LogicBlockDetailViewModel(
            block,
            Active.CacheKey,
            Active.Overview.State?.Blocks.FirstOrDefault(candidate => candidate.Id == blockId_),
            coordinator_.Tags,
            Active.CachedNotes(blockId_),
            Active.Overview.AreaTagsFor(block),
            Active.Overview.Definitions,
            Active.Overview.AreaTags);
        viewModel_.ActionRequested += OnActionRequested;
        viewModel_.ApplyUsageStates(Active.Overview.State);

        BindingContext = viewModel_;
        Title = block.Name;
        titleLabel.Text = block.Name;
        descriptionLabel.Text = block.Description;
        Repaint();

        // Notes are the one part the cache can be behind on: read them in the background and
        // keep the cached ones when the bridge cannot answer.
        await RefreshNotesAsync();
    }

    /// <summary>Re-reads the block's notes through the connection, which keeps them cached.</summary>
    private async Task RefreshNotesAsync()
    {
        if (Active is not { } active)
        {
            return;
        }

        MobileResult<IReadOnlyList<LogicNoteDto>> notes = await active.GetNotesAsync(blockId_, CancellationToken.None);
        if (notes.Ok && notes.Value is not null)
        {
            viewModel_?.SetNotes(notes.Value, PendingNotes());
        }
    }

    /// <summary>Notes this block has in the outbox but the bridge has not stored yet.</summary>
    private IEnumerable<OutboxItem> PendingNotes() =>
        Active?.OutboxItems.Where(item => item.Kind == OutboxKind.Note && item.BlockId == blockId_)
            ?? Enumerable.Empty<OutboxItem>();

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
        viewModel_.ApplyUsageStates(Active.Overview.State);
        offlineLabel.IsVisible = Active.IsOffline;
        offlineLabel.Text = Active.OfflineText;
        stateLabel.Text = viewModel_.StateLabel;
        stateLabel.TextColor = (Color)new StateColorConverter()
            .Convert(viewModel_.StateKey, typeof(Color), null, CultureInfo.InvariantCulture);
        blockedLabel.Text = viewModel_.BlockedByText;
        // A queued note appears as pending and disappears when the bridge's copy arrives.
        viewModel_.SetNotes(Active.CachedNotes(blockId_), PendingNotes());
    }

    /// <summary>A row's tag line opens where the tag comes from and which blocks use it.</summary>
    private void OnTagTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is LogicElementRowViewModel row)
        {
            viewModel_?.OpenTagPanel(row);
        }
    }

    private void OnTagPanelCloseClicked(object? sender, EventArgs e) => viewModel_?.CloseTagPanel();

    private void OnTagPanelScrimTapped(object? sender, TappedEventArgs e) => viewModel_?.CloseTagPanel();

    /// <summary>A block card in the inspector opens that block's Result, as the web panel does.</summary>
    private async void OnTagUseTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not LogicTagUseViewModel use)
        {
            return;
        }

        viewModel_?.CloseTagPanel();
        await Shell.Current.GoToAsync($"block?id={use.BlockId}");
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

        // A named area tag resolves through the block's area's tags; while it is unassigned
        // there is nothing to write, and the message says which entry is waiting.
        if (viewModel_ is null
            || !viewModel_.TryResolveTag(
                action.Action.Tag,
                action.Action.SourceId,
                action.Action.ItemId,
                out string sourceId,
                out string itemId))
        {
            messageLabel.Text = "✗ " + (viewModel_?.UnresolvedTagText(action.Action.Tag) ?? "no tag");
            return;
        }

        OutboxSubmitResult result = await Active.SubmitActionAsync(
            viewModel_.Id,
            viewModel_.Title,
            action.Label,
            action.TagLabel,
            sourceId,
            itemId,
            action.Action.Value,
            CancellationToken.None);

        messageLabel.Text = result.Kind switch
        {
            OutboxSubmitKind.Sent => "✓ " + action.Label + " written",
            OutboxSubmitKind.Queued => "⏳ " + action.Label + " queued — will send when the bridge is back",
            OutboxSubmitKind.Uncertain => "✗ " + (result.Error ?? "write not confirmed — review it in Settings ▸ Sync"),
            OutboxSubmitKind.SignInRequired => "✗ sign in to send " + action.Label,
            _ => "✗ " + (result.Error ?? "write failed")
        };
    }

    private async void OnAddNoteClicked(object? sender, EventArgs e)
    {
        string text = noteEntry.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || viewModel_ is null || Active is null)
        {
            return;
        }

        OutboxSubmitResult result = await Active.SubmitNoteAsync(
            blockId_,
            text,
            CancellationToken.None);
        if (result.Kind is OutboxSubmitKind.Rejected)
        {
            messageLabel.Text = "✗ " + (result.Error ?? "note failed");
            return;
        }

        noteEntry.Text = string.Empty;
        viewModel_.SetNotes(Active.CachedNotes(blockId_), PendingNotes());
        if (result.Kind == OutboxSubmitKind.Sent)
        {
            await RefreshNotesAsync();
        }

        messageLabel.Text = result.Kind switch
        {
            OutboxSubmitKind.Sent => "✓ note added",
            OutboxSubmitKind.SignInRequired => "⏳ note queued — sign in to sync it",
            _ => "⏳ note queued — will send when the bridge is back"
        };
    }
}
