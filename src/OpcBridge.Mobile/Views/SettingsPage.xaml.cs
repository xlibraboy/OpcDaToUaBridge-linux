using Microsoft.Maui.ApplicationModel;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class SettingsPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;

    public SettingsPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        BindableLayout.SetItemsSource(bridgesList, coordinator_.Bridges);
        coordinator_.Changed += OnChanged;
        aboutLabel.Text = "OpcBridge Logic " + AppInfo.Current.VersionString;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Repaint();
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Repaint);

    private void Repaint()
    {
        BridgeConnection? active = coordinator_.Active;
        bridgesEmptyLabel.IsVisible = coordinator_.Bridges.Count == 0;
        connectionLabel.Text = active is null
            ? "no bridge selected"
            : active.StatusText + " · " + active.Bridge.Url;

        MobileSession? session = active?.Session;
        sessionLabel.Text = session is null
            ? "not signed in"
            : (session.Authenticated
                ? $"signed in as {session.DisplayName} ({session.Role})"
                : (session.AuthEnabled ? "the bridge requires sign-in" : "the bridge runs without sign-in"));
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        string host = hostEntry.Text?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            connectionLabel.Text = "enter the bridge host or IP first";
            return;
        }

        connectButton.IsEnabled = false;
        // The probe can walk the whole 8080–8180 range; say what is happening right away.
        connectionLabel.Text = "searching for a bridge on " + host + "…";
        try
        {
            MobileResult<BridgeConnection> result = await coordinator_.AddOrConnectAsync(
                host,
                nameEntry.Text,
                CancellationToken.None);
            if (!result.Ok)
            {
                // Do not Repaint over this: the operator must see why the address was refused.
                connectionLabel.Text = "✗ " + (result.Error ?? "could not reach that bridge");
                return;
            }

            hostEntry.Text = string.Empty;
            nameEntry.Text = string.Empty;
            Repaint();
        }
        finally
        {
            connectButton.IsEnabled = true;
        }
    }

    private async void OnBridgeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border || border.BindingContext is not BridgeConnection connection || connection.IsActive)
        {
            return;
        }

        await coordinator_.ActivateAsync(connection, CancellationToken.None);
        Repaint();
    }

    private async void OnRemoveBridgeClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not BridgeConnection connection)
        {
            return;
        }

        bool confirmed = await DisplayAlert(
            "Remove bridge",
            $"Forget {connection.DisplayName} ({connection.Bridge.Url})?",
            "Remove",
            "Cancel");
        if (confirmed)
        {
            await coordinator_.RemoveAsync(connection);
            Repaint();
        }
    }

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        BridgeConnection? active = coordinator_.Active;
        if (active is null)
        {
            await DisplayAlert("Sign in", "Add a bridge first.", "OK");
            return;
        }

        signInButton.IsEnabled = false;
        try
        {
            MobileResult<MobileSession> result = await coordinator_.SignInAsync(
                active,
                userEntry.Text ?? string.Empty,
                passwordEntry.Text ?? string.Empty,
                CancellationToken.None);
            if (!result.Ok)
            {
                await DisplayAlert("Sign-in failed", result.Error ?? "the bridge rejected the sign-in", "OK");
            }
            else
            {
                passwordEntry.Text = string.Empty;
            }
        }
        finally
        {
            signInButton.IsEnabled = true;
            Repaint();
        }
    }
}
