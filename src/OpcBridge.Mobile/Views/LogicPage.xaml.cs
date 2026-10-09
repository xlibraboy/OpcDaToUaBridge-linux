using System.Collections.Specialized;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class LogicPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private LogicOverviewViewModel? subscribed_;
    private bool picking_;

    public LogicPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
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

        summaryLabel.Text = overview?.Summary ?? string.Empty;
        connectionLabel.Text = active is null
            ? "no bridges yet — add one under Settings ▸ Bridges"
            : active.StatusText + " · " + active.Bridge.Url;
        offlineLabel.IsVisible = active?.IsOffline == true;
        offlineLabel.Text = active?.OfflineText ?? string.Empty;

        string search = overview?.SearchText ?? string.Empty;
        if (!string.Equals(searchBar.Text, search, StringComparison.Ordinal))
        {
            searchBar.Text = search;
        }

        picking_ = true;
        bridgePicker.SelectedItem = active;
        picking_ = false;

        RefreshFilterUi();
    }

    private void OnVisibleChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshFilterUi);

    /// <summary>Keeps the filter labels, the search box and the empty state in step with the active overview.</summary>
    private void RefreshFilterUi()
    {
        LogicOverviewViewModel? overview = coordinator_.Active?.Overview;

        filterLabel.Text = overview?.FilterText ?? string.Empty;
        filterLabel.IsVisible = overview?.HasFilter == true;

        bool hasTags = (overview?.TagFilters.Count ?? 0) > 0;
        chipDivider.IsVisible = hasTags;
        tagChips.IsVisible = hasTags;

        emptyLabel.Text = overview is null
            ? "No bridge selected — add one under Settings ▸ Bridges."
            : overview.EmptyMessage;
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

    private async void OnBridgePicked(object? sender, EventArgs e)
    {
        if (picking_ || bridgePicker.SelectedItem is not BridgeConnection connection || ReferenceEquals(connection, coordinator_.Active))
        {
            return;
        }

        await coordinator_.ActivateAsync(connection, CancellationToken.None);
        Repaint();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        refreshButton.IsEnabled = false;
        try
        {
            BridgeConnection? active = coordinator_.Active;
            if (active is null)
            {
                connectionLabel.Text = "add a bridge under Settings ▸ Bridges";
            }
            else if (active.IsConnected)
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

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//settings");
    }
}
