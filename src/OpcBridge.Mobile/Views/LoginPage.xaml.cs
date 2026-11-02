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
    private bool picking_;
    private bool busy_;
    private CancellationTokenSource? connectCts_;

    public LoginPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        bridgePicker.ItemsSource = coordinator_.Bridges;
        bridgePicker.ItemDisplayBinding = new Binding(nameof(BridgeConnection.DisplayName));
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
        picking_ = true;
        bridgePicker.SelectedItem = active;
        picking_ = false;

        statusLabel.Text = active is null
            ? "No bridge yet — add one above."
            : active.StatusText + " · " + active.Bridge.Url
                + (active.Session is { AuthEnabled: false }
                    ? "\nThis bridge runs without enforced sign-in; the viewer still asks for an account."
                    : string.Empty);

        // One action at a time: a connect in flight owns the bridge.
        connectButton.IsEnabled = !busy_;
        bridgePicker.IsEnabled = !busy_;
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
        statusLabel.Text = "searching for a bridge on " + host + " (up to 8 s)…";
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

            EndBusy();
            if (cancelled)
            {
                statusLabel.Text = "search cancelled";
            }
        }
    }

    private void OnCancelConnectClicked(object? sender, EventArgs e)
    {
        connectCts_?.Cancel();
        statusLabel.Text = "cancelling…";
    }

    private async void OnBridgePicked(object? sender, EventArgs e)
    {
        if (picking_ ||
            busy_ ||
            bridgePicker.SelectedItem is not BridgeConnection connection ||
            ReferenceEquals(connection, coordinator_.Active))
        {
            return;
        }

        BeginBusy();
        errorLabel.Text = string.Empty;
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
            EndBusy();
        }
    }

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
