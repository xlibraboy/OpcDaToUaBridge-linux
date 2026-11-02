using Microsoft.Maui.ApplicationModel;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

/// <summary>
/// The viewer's entry screen: it exists before the tabs, and it is where the app returns when
/// the session ends. The operator picks a saved bridge (or adds one), signs in against that
/// bridge's own accounts, and the app swaps to the Logic tab only when the session is accepted.
/// </summary>
public partial class LoginPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private bool busy_;
    private CancellationTokenSource? connectCts_;
    private string? statusNote_;

    public LoginPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        Repaint();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        coordinator_.Changed -= OnChanged;
        coordinator_.Changed += OnChanged;
        Repaint();

        // A saved bridge is remembered across launches; asking it for its session is what puts
        // its status (and whether it enforces sign-in) on this screen.
        BeginBusy();
        try
        {
            await coordinator_.ConnectActiveAsync(CancellationToken.None);
        }
        finally
        {
            EndBusy();
        }
    }

    protected override void OnDisappearing()
    {
        connectCts_?.Cancel();
        coordinator_.Changed -= OnChanged;
        base.OnDisappearing();
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Repaint);

    private void BeginBusy()
    {
        busy_ = true;
        busyIndicator.IsVisible = true;
        busyIndicator.IsRunning = true;
        cancelConnectButton.IsVisible = connectCts_ is not null;
        Repaint();
    }

    private void EndBusy()
    {
        busy_ = false;
        busyIndicator.IsRunning = false;
        busyIndicator.IsVisible = false;
        cancelConnectButton.IsVisible = false;
        Repaint();
    }

    private void Repaint()
    {
        BridgeConnection? active = coordinator_.Active;
        bridgePickerLabel.Text = active?.DisplayName ?? "Saved bridges";
        bridgePickerLabel.TextColor = active is null
            ? Color.FromArgb("#93A19B")
            : Color.FromArgb("#F1F5F3");

        string stateKey = active is null
            ? "unknown"
            : active.IsOffline ? "offline" : active.IsConnected ? "synced" : "unknown";
        Color stateColor = DotColor(stateKey);
        bridgeStateDot.Color = stateColor;
        bridgeStateLabel.TextColor = stateColor;
        bridgeStateLabel.Text = active is null
            ? "no bridge"
            : active.IsOffline ? "offline" : active.IsConnected ? "connected · live" : active.StatusText;
        statusLabel.Text = statusNote_ ?? (active is null ? "No bridge yet — add one above." : active.Bridge.Url);

        // One action at a time: a connect in flight owns the bridge.
        connectButton.IsEnabled = !busy_;
        signInButton.IsEnabled = !busy_ && active is not null;
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (busy_)
        {
            return;
        }

        string host = hostEntry.Text?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            errorLabel.Text = "Enter the bridge host or IP first.";
            return;
        }

        using CancellationTokenSource cts = new();
        connectCts_ = cts;
        bool cancelled = false;
        BeginBusy();
        errorLabel.Text = string.Empty;
        statusNote_ = "searching for a bridge on " + host + " (up to 8 s)…";
        Repaint();
        try
        {
            MobileResult<BridgeConnection> result = await coordinator_.AddOrConnectAsync(
                host,
                nameEntry.Text,
                cts.Token);
            if (!result.Ok)
            {
                // Leave the typed address in place: it is the thing to correct.
                errorLabel.Text = "✗ " + (result.Error ?? "could not reach that bridge");
                return;
            }

            hostEntry.Text = string.Empty;
            nameEntry.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            // An async event handler must never let a network failure reach the process.
            errorLabel.Text = "✗ could not reach " + host + " · " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(connectCts_, cts))
            {
                connectCts_ = null;
            }

            statusNote_ = cancelled ? "search cancelled" : null;
            EndBusy();
        }
    }

    private void OnCancelConnectClicked(object? sender, EventArgs e)
    {
        connectCts_?.Cancel();
        statusNote_ = "cancelling…";
        Repaint();
    }

    /// <summary>
    /// The bridge row is the saved-bridge picker. It opens the same list a spinner would, without
    /// the platform Picker being disabled or re-selected underneath its own open dialog.
    /// </summary>
    private async void OnBridgeRowTapped(object? sender, TappedEventArgs e)
    {
        if (busy_ || coordinator_.Bridges.Count == 0)
        {
            return;
        }

        string[] names = coordinator_.Bridges.Select(connection => connection.DisplayName).ToArray();
        string? picked = await DisplayActionSheet("Bridge", "Cancel", null, names);
        int index = picked is null ? -1 : Array.IndexOf(names, picked);
        if (index < 0 || ReferenceEquals(coordinator_.Bridges[index], coordinator_.Active))
        {
            return;
        }

        BridgeConnection connection = coordinator_.Bridges[index];
        BeginBusy();
        errorLabel.Text = string.Empty;
        statusNote_ = "connecting to " + connection.DisplayName + "…";
        Repaint();
        try
        {
            await coordinator_.ActivateAsync(connection, CancellationToken.None);
        }
        catch (Exception ex)
        {
            errorLabel.Text = "✗ could not use " + connection.DisplayName + " · " + ex.Message;
        }
        finally
        {
            statusNote_ = null;
            EndBusy();
        }
    }

    private static Color DotColor(string key) => key switch
    {
        "synced" => Color.FromArgb("#4CC38A"),
        "error" or "offline" => Color.FromArgb("#E5484D"),
        _ => Color.FromArgb("#93A19B")
    };

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        BridgeConnection? active = coordinator_.Active;
        if (active is null)
        {
            errorLabel.Text = "Add a bridge first.";
            return;
        }

        string username = userEntry.Text?.Trim() ?? string.Empty;
        if (username.Length == 0)
        {
            errorLabel.Text = "Enter the username.";
            return;
        }

        BeginBusy();
        errorLabel.Text = string.Empty;
        try
        {
            MobileResult<MobileSession> result = await coordinator_.SignInAsync(
                active,
                username,
                passwordEntry.Text ?? string.Empty,
                CancellationToken.None);
            if (!result.Ok)
            {
                errorLabel.Text = result.Error ?? "the bridge rejected the sign-in";
                return;
            }

            passwordEntry.Text = string.Empty;
            // The session now opens the gate; the app shows the tabs.
        }
        catch (Exception ex)
        {
            errorLabel.Text = "✗ could not sign in · " + ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }
}
