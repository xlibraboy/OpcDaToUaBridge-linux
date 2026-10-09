using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class LogicPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private bool picking_;

    public LogicPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        bridgePicker.ItemsSource = coordinator_.Bridges;
        bridgePicker.ItemDisplayBinding = new Binding(nameof(BridgeConnection.DisplayName));
        coordinator_.Changed += OnChanged;
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
        if (!ReferenceEquals(blocksView.ItemsSource, active?.Overview.Blocks))
        {
            blocksView.ItemsSource = active?.Overview.Blocks;
        }

        summaryLabel.Text = active?.Overview.Summary ?? string.Empty;
        connectionLabel.Text = active is null
            ? "no bridges yet — add one under Settings ▸ Bridges"
            : active.StatusText + " · " + active.Bridge.Url;

        picking_ = true;
        bridgePicker.SelectedItem = active;
        picking_ = false;
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
