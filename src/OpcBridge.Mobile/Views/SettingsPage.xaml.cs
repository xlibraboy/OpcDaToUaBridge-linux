using System.Globalization;
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
        signOutButton.IsEnabled = session?.Authenticated == true;

        syncLabel.Text = active?.SyncText ?? "not connected";
        syncLabel.TextColor = (Color)new StateColorConverter()
            .Convert(active?.SyncKey ?? "offline", typeof(Color), null, CultureInfo.InvariantCulture);
        latencyLabel.Text = "network latency: " + (active?.LatencyText ?? "latency —");
        latencyLabel.TextColor = (Color)new StateColorConverter()
            .Convert(active?.LatencyKey ?? "unknown", typeof(Color), null, CultureInfo.InvariantCulture);
        retrySyncButton.IsEnabled = active is not null;

        IReadOnlyList<OutboxItem> items = active?.OutboxItems ?? Array.Empty<OutboxItem>();
        BindableLayout.SetItemsSource(outboxList, items);
        outboxEmptyLabel.IsVisible = items.Count == 0;
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
        catch (Exception ex)
        {
            // An async event handler must never let a network failure reach the process: the
            // operator gets the address's answer here instead of the app disappearing.
            connectionLabel.Text = "✗ could not reach " + host + " · " + ex.Message;
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

        try
        {
            await coordinator_.ActivateAsync(connection, CancellationToken.None);
            Repaint();
        }
        catch (Exception ex)
        {
            connectionLabel.Text = "✗ could not use " + connection.DisplayName + " · " + ex.Message;
        }
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

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        BridgeConnection? active = coordinator_.Active;
        if (active is null)
        {
            return;
        }

        signOutButton.IsEnabled = false;
        try
        {
            await active.SignOutAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The session is cleared locally by SignOutAsync; a failure here must still leave
            // the operator on a screen that says what happened.
            connectionLabel.Text = "✗ could not sign out cleanly · " + ex.Message;
        }
        finally
        {
            signOutButton.IsEnabled = true;
            Repaint();
        }
    }

    private async void OnRetrySyncClicked(object? sender, EventArgs e)
    {
        BridgeConnection? active = coordinator_.Active;
        if (active is null)
        {
            return;
        }

        retrySyncButton.IsEnabled = false;
        try
        {
            if (active.IsConnected && !active.IsOffline)
            {
                await active.RefreshAsync(CancellationToken.None);
            }
            else
            {
                await active.ConnectAsync(CancellationToken.None);
            }

            await active.FlushOutboxAsync(CancellationToken.None);
        }
        finally
        {
            retrySyncButton.IsEnabled = true;
            Repaint();
        }
    }

    private async void OnRetryOutboxClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: OutboxItem item } ||
            coordinator_.Active is not { } active)
        {
            return;
        }

        await active.RetryOutboxItemAsync(item.Id, CancellationToken.None);
        Repaint();
    }

    private async void OnDiscardOutboxClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: OutboxItem item } ||
            coordinator_.Active is not { } active)
        {
            return;
        }

        bool confirmed = await DisplayAlert(
            "Discard queued item",
            $"Discard this {item.KindLabel.ToLowerInvariant()} for {item.TargetText}?",
            "Discard",
            "Cancel");
        if (confirmed)
        {
            active.DiscardOutboxItem(item.Id);
            Repaint();
        }
    }
}
