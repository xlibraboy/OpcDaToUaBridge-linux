using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class ElementsPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private readonly ElementsViewModel elements_ = new();

    public ElementsPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        elementsView.ItemsSource = elements_.VisibleGroups;
        kindAllButton.BindingContext = elements_.KindFilters[0];
        kindInterlockButton.BindingContext = elements_.KindFilters[1];
        kindPermissiveButton.BindingContext = elements_.KindFilters[2];
        kindSequenceButton.BindingContext = elements_.KindFilters[3];
        coordinator_.Changed += OnChanged;
        ThemeManager.Changed += OnThemeChanged;
        RefreshFilterUi();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Repaint();
        await coordinator_.ConnectActiveAsync(CancellationToken.None);
        Repaint();
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Repaint);

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

        elementsView.SelectedItem = null;
        await Shell.Current.GoToAsync($"block?id={row.BlockId}");
    }

    private async void OnSyncTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("//settings");
    }
}
