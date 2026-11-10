using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class ElementsPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private readonly ElementsViewModel elements_ = new();
    private volatile bool scrolling_;
    private volatile bool pendingRepaint_;
    private bool visible_;
    private bool repaintQueued_;
    private IDispatcherTimer? scrollSettle_;
    private DateTime appearedAt_;

    public ElementsPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        elementsView.ItemsSource = elements_.VisibleGroups;
        elementsView.Scrolled += OnElementsScrolled;
        kindAllButton.BindingContext = elements_.KindFilters[0];
        kindInterlockButton.BindingContext = elements_.KindFilters[1];
        kindPermissiveButton.BindingContext = elements_.KindFilters[2];
        kindSequenceButton.BindingContext = elements_.KindFilters[3];
        RefreshFilterUi();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        visible_ = true;
        scrolling_ = false;
        pendingRepaint_ = false;
        appearedAt_ = DateTime.UtcNow;
        // A tab page stays alive while another tab is shown. Subscribing here means the hidden
        // page does no repaint work at all, so switching screens never waits behind one.
        coordinator_.Changed -= OnChanged;
        coordinator_.Changed += OnChanged;
        coordinator_.TagValuesChanged -= OnTagValuesChanged;
        coordinator_.TagValuesChanged += OnTagValuesChanged;
        ThemeManager.Changed -= OnThemeChanged;
        ThemeManager.Changed += OnThemeChanged;
        Repaint();
        await coordinator_.ConnectActiveAsync(CancellationToken.None);
        Repaint();
    }

    protected override void OnDisappearing()
    {
        visible_ = false;
        scrolling_ = false;
        pendingRepaint_ = false;
        repaintQueued_ = false;
        scrollSettle_?.Stop();
        coordinator_.Changed -= OnChanged;
        coordinator_.TagValuesChanged -= OnTagValuesChanged;
        ThemeManager.Changed -= OnThemeChanged;
        base.OnDisappearing();
    }

    /// <summary>
    /// Live pushes can repaint dozens of rows; doing that mid-fling is what made the list hitch.
    /// Keep the values arriving, but hold the repaint until the finger stops.
    /// </summary>
    private void OnChanged()
    {
        if (!visible_)
        {
            return;
        }

        if (scrolling_)
        {
            pendingRepaint_ = true;
            return;
        }

        QueueRepaint();
    }

    /// <summary>
    /// Coalesces the changes a connect or a burst of pushes raises into one repaint, so the UI
    /// thread renders the latest state instead of every intermediate one.
    /// </summary>
    private void QueueRepaint()
    {
        if (repaintQueued_)
        {
            return;
        }

        repaintQueued_ = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () =>
        {
            repaintQueued_ = false;
            if (!visible_)
            {
                return;
            }

            if (scrolling_)
            {
                pendingRepaint_ = true;
                return;
            }

            Repaint();
        });
    }

    /// <summary>
    /// A values batch only moves the rows behind the tags it names. Applying it here keeps a
    /// plant's whole element list out of the ten-a-second push path.
    /// </summary>
    private void OnTagValuesChanged(IReadOnlyList<HmiValueDelta> deltas)
    {
        if (!visible_)
        {
            return;
        }

        if (scrolling_)
        {
            pendingRepaint_ = true;
            return;
        }

        BridgeConnection? active = coordinator_.Active;
        if (active is null)
        {
            return;
        }

        elements_.ApplyTagDeltas(deltas, coordinator_.Tags, active.CacheKey);
        elements_.MarkSynced(active.LastSyncedUtc);
        PaintChrome();
    }

    private void OnElementsScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        scrolling_ = true;
        IDispatcherTimer settle = ScrollSettle;
        // Restarting one timer is cheap; a task and cancellation source per scroll event was
        // what kept the thread pool busy for the whole fling.
        settle.Stop();
        settle.Start();
    }

    private IDispatcherTimer ScrollSettle
    {
        get
        {
            if (scrollSettle_ is not null)
            {
                return scrollSettle_;
            }

            scrollSettle_ = Dispatcher.CreateTimer();
            scrollSettle_.Interval = TimeSpan.FromMilliseconds(160);
            scrollSettle_.IsRepeating = false;
            scrollSettle_.Tick += (_, _) =>
            {
                if (!visible_)
                {
                    return;
                }

                scrolling_ = false;
                if (pendingRepaint_)
                {
                    pendingRepaint_ = false;
                    Repaint();
                }
            };
            return scrollSettle_;
        }
    }

    /// <summary>
    /// Converters resolve a state colour when a row is built, so a theme switch rebuilds the
    /// grouped list rather than leaving the old surface's colours in place.
    /// </summary>
    private void OnThemeChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        elementsView.ItemsSource = null;
        elementsView.ItemsSource = elements_.VisibleGroups;
        Repaint();
        latencyBadge.RefreshTheme();
    });

    private void Repaint()
    {
        BridgeConnection? active = coordinator_.Active;
        if (active is null)
        {
            elements_.Clear();
        }
        else if (active.IsOffline || !active.IsConnected)
        {
            // The stored structure only — rows, tags and step order. No reading or evaluated
            // state is rendered from the local dataset.
            elements_.ApplyDefinitions(
                active.Overview.Definitions,
                active.Overview.AreaTags,
                coordinator_.Tags,
                active.CacheKey);
            elements_.ApplyState(null, null, active.CacheKey, offline: true, active.LastSyncedUtc);
        }
        else
        {
            elements_.ApplyDefinitions(
                active.Overview.Definitions,
                active.Overview.AreaTags,
                coordinator_.Tags,
                active.CacheKey);
            elements_.ApplyState(
                active.Overview.State,
                coordinator_.Tags,
                active.CacheKey,
                offline: false,
                active.LastSyncedUtc);
        }

        PaintChrome();
    }

    /// <summary>The strips and labels around the list; cheap enough to follow every live push.</summary>
    private void PaintChrome()
    {
        BridgeConnection? active = coordinator_.Active;
        summaryLabel.Text = elements_.Summary;
        sourceLabel.Text = elements_.SourceText;
        Color sourceColor = elements_.IsOffline
            ? ThemePalette.Get("WarnColor")
            : ThemePalette.Get("GoodColor");
        sourceDot.Color = sourceColor;
        sourceLabel.TextColor = sourceColor;
        connectionDot.Color = active is null
            ? ThemePalette.Get("MutedColor")
            : active.IsOffline ? ThemePalette.Get("BadColor")
            : active.IsConnected ? ThemePalette.Get("GoodColor")
            : ThemePalette.Get("WarnColor");
        connectionLabel.Text = active is null
            ? "no bridges yet — add one under Settings ▸ Bridges"
            : active.StatusText;
        latencyBadge.Text = active?.LatencyValue ?? "—";
        latencyBadge.StateKey = active?.LatencyKey ?? "unknown";
        latencyBadge.Description = active is null
            ? "No bridge selected."
            : "Network latency to the bridge: " + active.LatencyText + ".";

        string search = elements_.SearchText;
        if (!string.Equals(searchEntry.Text, search, StringComparison.Ordinal))
        {
            searchEntry.Text = search;
        }

        RefreshFilterUi();
    }

    private void RefreshFilterUi()
    {
        filterLabel.Text = elements_.FilterText;
        filterLabel.IsVisible = elements_.HasFilter;
        emptyLabel.Text = elements_.EmptyMessage;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        elements_.SearchText = e.NewTextValue ?? string.Empty;
        RefreshFilterUi();
    }

    private void OnFilterChipClicked(object? sender, EventArgs e)
    {
        if (sender is Button button && button.BindingContext is LogicFilterChipViewModel chip)
        {
            elements_.SelectKindFilter(chip);
            RefreshFilterUi();
        }
    }

    private async void OnRefreshClicked(object? sender, TappedEventArgs e)
    {
        await SyncAsync();
    }

    private async void OnSyncNowClicked(object? sender, TappedEventArgs e)
    {
        await SyncAsync();
    }

    private async Task SyncAsync()
    {
        refreshButton.IsEnabled = false;
        syncButton.IsEnabled = false;
        try
        {
            BridgeConnection? active = coordinator_.Active;
            if (active is null)
            {
                connectionLabel.Text = "add a bridge under Settings ▸ Bridges";
            }
            else if (active.IsConnected && !active.IsOffline)
            {
                await active.RefreshAsync(CancellationToken.None);
                await active.FlushOutboxAsync(CancellationToken.None);
            }
            else
            {
                await active.ConnectAsync(CancellationToken.None);
                await active.FlushOutboxAsync(CancellationToken.None);
            }
        }
        finally
        {
            refreshButton.IsEnabled = true;
            syncButton.IsEnabled = true;
            refreshView.IsRefreshing = false;
            Repaint();
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        try
        {
            BridgeConnection? active = coordinator_.Active;
            if (active is not null)
            {
                await active.RefreshAsync(CancellationToken.None);
            }
        }
        finally
        {
            refreshView.IsRefreshing = false;
        }
    }

    private async void OnElementSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not LogicElementRowViewModel row)
        {
            return;
        }

        // A tap that switches to this tab can also land on the row beneath it. Ignore that
        // ghost selection for a moment so the tab opens its list instead of a block page.
        if (scrolling_ || (DateTime.UtcNow - appearedAt_).TotalMilliseconds < 350)
        {
            elementsView.SelectedItem = null;
            return;
        }

        elementsView.SelectedItem = null;
        await Shell.Current.GoToAsync($"block?id={row.BlockId}");
    }

    private async void OnSyncTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("//settings");
    }
}
