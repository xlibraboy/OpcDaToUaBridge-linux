using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class ElementsPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private readonly ElementsViewModel elements_ = new();
    private bool picking_;

    public ElementsPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        elementsView.ItemsSource = elements_.VisibleGroups;
        BindableLayout.SetItemsSource(kindChips, elements_.KindFilters);
        bridgePicker.ItemsSource = coordinator_.Bridges;
        bridgePicker.ItemDisplayBinding = new Binding(nameof(BridgeConnection.DisplayName));
        coordinator_.Changed += OnChanged;
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

    private void Repaint()
    {
        BridgeConnection? active = coordinator_.Active;
        if (active is null)
        {
            elements_.Clear();
        }
        else if (active.IsOffline || !active.IsConnected)
        {
            BridgeLastKnown? known = active.LastKnown;
            if (known is not null)
            {
                elements_.ApplyDefinitions(known.Blocks, known.TagSources, coordinator_.Tags, active.CacheKey);
                elements_.ApplyState(
                    known.State,
                    BuildTagCache(active.CacheKey, known.Tags),
                    active.CacheKey,
                    lastKnown: true,
                    known.SavedUtc);
            }
            else
            {
                elements_.ApplyDefinitions(
                    active.Overview.Definitions,
                    active.Overview.AreaTags,
                    coordinator_.Tags,
                    active.CacheKey);
                elements_.ApplyState(null, null, active.CacheKey, lastKnown: false, active.LastSyncedUtc);
            }
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
                lastKnown: false,
                active.LastSyncedUtc);
        }

        summaryLabel.Text = elements_.Summary;
        sourceLabel.Text = elements_.SourceText;
        connectionLabel.Text = active is null
            ? "no bridges yet — add one under Settings ▸ Bridges"
            : active.StatusText + " · " + active.Bridge.Url;
        syncBadge.Text = active?.SyncText ?? "not connected";
        syncBadge.StateKey = active?.SyncKey ?? "offline";
        syncBadge.Description = active is null
            ? "No bridge selected."
            : "Sync state: " + active.SyncText + " · " + active.PendingCount + " pending, "
                + active.FailedCount + " failed. Open Settings for queued items.";
        latencyBadge.Text = active?.LatencyText ?? "latency —";
        latencyBadge.StateKey = active?.LatencyKey ?? "unknown";
        latencyBadge.Description = active is null
            ? "No bridge selected."
            : "Network latency to the bridge: " + active.LatencyText + ".";

        string search = elements_.SearchText;
        if (!string.Equals(searchBar.Text, search, StringComparison.Ordinal))
        {
            searchBar.Text = search;
        }

        picking_ = true;
        bridgePicker.SelectedItem = active;
        picking_ = false;

        RefreshFilterUi();
    }

    private void RefreshFilterUi()
    {
        filterLabel.Text = elements_.FilterText;
        filterLabel.IsVisible = elements_.HasFilter;
        emptyLabel.Text = elements_.EmptyMessage;
    }

    private static MultiBridgeTagCache? BuildTagCache(string bridgeKey, IReadOnlyList<HmiTagDto> tags)
    {
        if (tags.Count == 0)
        {
            return null;
        }

        MultiBridgeTagCache cache = new();
        cache.ReplaceBridge(bridgeKey, tags);
        return cache;
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

    private async void OnBridgePicked(object? sender, EventArgs e)
    {
        if (picking_ ||
            bridgePicker.SelectedItem is not BridgeConnection connection ||
            ReferenceEquals(connection, coordinator_.Active))
        {
            return;
        }

        await coordinator_.ActivateAsync(connection, CancellationToken.None);
        Repaint();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        await SyncAsync();
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
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
