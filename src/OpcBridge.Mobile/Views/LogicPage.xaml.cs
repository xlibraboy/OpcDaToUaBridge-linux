using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class LogicPage : ContentPage
{
    private readonly AppState state_;

    public LogicPage(AppState state)
    {
        InitializeComponent();
        state_ = state;
        blocksView.ItemsSource = state_.Overview.Blocks;
        state_.Changed += OnStateChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Repaint();
        if (!state_.IsConnected)
        {
            await ConnectSilentlyAsync();
        }
    }

    private async Task ConnectSilentlyAsync()
    {
        string host = state_.BaseUrl;
        await state_.ConnectAsync(host, CancellationToken.None);
        Repaint();
    }

    private void OnStateChanged()
    {
        MainThread.BeginInvokeOnMainThread(Repaint);
    }

    private void Repaint()
    {
        summaryLabel.Text = state_.Overview.Summary;
        connectionLabel.Text = state_.ConnectionText + " · " + state_.BaseUrl;
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        refreshButton.IsEnabled = false;
        try
        {
            if (!state_.IsConnected)
            {
                await ConnectSilentlyAsync();
            }
            else
            {
                await state_.RefreshAsync(CancellationToken.None);
            }
        }
        finally
        {
            refreshButton.IsEnabled = true;
            refreshView.IsRefreshing = false;
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        try
        {
            await state_.RefreshAsync(CancellationToken.None);
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
