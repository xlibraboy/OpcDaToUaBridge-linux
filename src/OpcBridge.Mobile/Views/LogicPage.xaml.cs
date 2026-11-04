using System.Collections.Specialized;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class LogicPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private LogicOverviewViewModel? subscribed_;

    public LogicPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
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
    /// filtered lists rather than leaving their old colours in place.
    /// </summary>
    private void OnThemeChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        BindableLayout.SetItemsSource(stateChips, null);
        BindableLayout.SetItemsSource(tagChips, null);
        blocksView.ItemsSource = null;
        Repaint();
        syncBadge.RefreshTheme();
        latencyBadge.RefreshTheme();
    });

    private void Repaint()
    {
        BridgeConnection? active = coordinator_.Active;
        LogicOverviewViewModel? overview = active?.Overview;

        if (!ReferenceEquals(blocksView.ItemsSource, overview?.VisibleGroups))
        {
            blocksView.ItemsSource = overview?.VisibleGroups;
        }

        // The visible list is rebuilt on the UI thread when a filter or a state change moves
        // blocks in or out; the labels that report it follow the collection.
        if (!ReferenceEquals(subscribed_, overview))
        {
            if (subscribed_ is not null)
            {
                subscribed_.Visible.CollectionChanged -= OnVisibleChanged;
            }

            subscribed_ = overview;
            if (subscribed_ is not null)
            {
                subscribed_.Visible.CollectionChanged += OnVisibleChanged;
            }
        }

        BindableLayout.SetItemsSource(stateChips, overview?.StateFilters);
        BindableLayout.SetItemsSource(tagChips, overview?.TagFilters);

        readyCountLabel.Text = (overview?.ReadyCount ?? 0).ToString();
        blockedCountLabel.Text = (overview?.BlockedCount ?? 0).ToString();
        noDataCountLabel.Text = (overview?.NoDataCount ?? 0).ToString();
        connectionDot.Color = active is null
            ? ThemePalette.Get("MutedColor")
            : active.IsOffline ? ThemePalette.Get("BadColor")
            : active.IsConnected ? ThemePalette.Get("GoodColor")
            : ThemePalette.Get("WarnColor");
        connectionLabel.Text = active is null
            ? "no bridges yet — add one under Settings ▸ Bridges"
            : active.StatusText;
        bridgeChipLabel.Text = active?.DisplayName ?? "Bridge";
        offlineLabel.IsVisible = active?.IsOffline == true;
        offlineLabel.Text = active?.OfflineText ?? string.Empty;

        syncBadge.Text = active?.SyncStamp ?? "not connected";
        syncBadge.StateKey = active?.SyncKey ?? "offline";
        syncBadge.Description = active is null
            ? "No bridge selected."
            : "Sync state: " + active.SyncText + " · " + active.PendingCount + " pending, "
                + active.FailedCount + " failed. Open Settings for queued items.";
        latencyBadge.Text = active?.LatencyValue ?? "—";
        latencyBadge.StateKey = active?.LatencyKey ?? "unknown";
        latencyBadge.Description = active is null
            ? "No bridge selected."
            : "Network latency to the bridge: " + active.LatencyText + ".";

        string search = overview?.SearchText ?? string.Empty;
        if (!string.Equals(searchEntry.Text, search, StringComparison.Ordinal))
        {
            searchEntry.Text = search;
        }

        RefreshFilterUi();
    }

    private void OnVisibleChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshFilterUi);

    /// <summary>Keeps the filter labels, the search box and the empty state in step with the active overview.</summary>
    private void RefreshFilterUi()
    {
        BridgeConnection? active = coordinator_.Active;
        LogicOverviewViewModel? overview = active?.Overview;

        filterLabel.Text = overview?.FilterText ?? string.Empty;
        filterLabel.IsVisible = overview?.HasFilter == true;

        bool hasTags = (overview?.TagFilters.Count ?? 0) > 0;
        chipDivider.IsVisible = hasTags;
        tagChips.IsVisible = hasTags;

        emptyLabel.Text = LogicListText.EmptyList(
            hasBridge: active is not null,
            connected: active?.IsConnected == true,
            hasBlocks: (overview?.Blocks.Count ?? 0) > 0,
            signInRequired: active?.SignInRequired == true,
            bridgeStatus: active?.StatusText ?? string.Empty,
            filterMissText: overview?.EmptyMessage ?? string.Empty);
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (coordinator_.Active?.Overview is { } overview)
        {
            overview.SearchText = e.NewTextValue ?? string.Empty;
            RefreshFilterUi();
        }
    }

    private void OnFilterChipClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button ||
            button.BindingContext is not LogicFilterChipViewModel chip ||
            coordinator_.Active?.Overview is not { } overview)
        {
            return;
        }

        if (overview.StateFilters.Contains(chip))
        {
            overview.SelectStateFilter(chip);
        }
        else
        {
            overview.ToggleTagFilter(chip);
        }

        RefreshFilterUi();
    }

    /// <summary>The bridge chip is the picker: one live link at a time, chosen by name.</summary>
    private async void OnBridgeChipTapped(object? sender, TappedEventArgs e)
    {
        if (coordinator_.Bridges.Count == 0)
        {
            await Shell.Current.GoToAsync("//settings");
            return;
        }

        string[] names = coordinator_.Bridges.Select(connection => connection.DisplayName).ToArray();
        string? picked = await DisplayActionSheet("Bridge", "Cancel", null, names);
        int index = picked is null ? -1 : Array.IndexOf(names, picked);
        if (index < 0 || ReferenceEquals(coordinator_.Bridges[index], coordinator_.Active))
        {
            return;
        }

        await coordinator_.ActivateAsync(coordinator_.Bridges[index], CancellationToken.None);
        Repaint();
    }

    private async void OnRefreshClicked(object? sender, TappedEventArgs e)
    {
        refreshButton.IsEnabled = false;
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
            }
            else
            {
                await active.ConnectAsync(CancellationToken.None);
            }
        }
        finally
        {
            refreshButton.IsEnabled = true;
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

    private async void OnBlockSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not LogicBlockCardViewModel card)
        {
            return;
        }

        blocksView.SelectedItem = null;
        await Shell.Current.GoToAsync($"block?id={card.Id}");
    }

    private async void OnSettingsClicked(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("//settings");
    }

    private async void OnSyncTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("//settings");
    }
}
