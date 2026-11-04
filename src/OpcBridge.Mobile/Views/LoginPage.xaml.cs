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
    private int busyOperations_;
    private CancellationTokenSource? connectCts_;
    private CancellationTokenSource? startupCts_;
    private bool startingUp_;
    private string? statusNote_;

    /// <summary>True while any operation owns the bridge (start-up search, add, activation).</summary>
    private bool Busy => busyOperations_ > 0;

    public LoginPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        // The add-bridge form lives in its own card, opened on request — picking a saved
        // bridge never lands beside the Add & connect button.
        bridgeSheet.NewBridgeRequested += OpenAddCard;
        Repaint();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        coordinator_.Changed -= OnChanged;
        coordinator_.Changed += OnChanged;
        ThemeManager.Changed -= OnThemeChanged;
        ThemeManager.Changed += OnThemeChanged;
        Repaint();

        // A saved bridge is remembered across launches; asking it for its session is what puts
        // its status (and whether it enforces sign-in) on this screen. The search is cancellable:
        // picking another bridge from the picker stops it instead of waiting for the probe.
        startupCts_?.Dispose();
        startupCts_ = new CancellationTokenSource();
        startingUp_ = true;
        BeginBusy();
        try
        {
            await coordinator_.ConnectActiveAsync(startupCts_.Token);
        }
        catch (OperationCanceledException)
        {
            // Another saved bridge was picked while the start-up search was still running.
        }
        finally
        {
            startingUp_ = false;
            EndBusy();
        }
    }

    protected override void OnDisappearing()
    {
        connectCts_?.Cancel();
        startupCts_?.Cancel();
        coordinator_.Changed -= OnChanged;
        ThemeManager.Changed -= OnThemeChanged;
        base.OnDisappearing();
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Repaint);

    private void OnThemeChanged() => MainThread.BeginInvokeOnMainThread(Repaint);

    private void BeginBusy()
    {
        busyOperations_++;
        UpdateBusyVisuals();
    }

    private void EndBusy()
    {
        busyOperations_ = Math.Max(0, busyOperations_ - 1);
        UpdateBusyVisuals();
    }

    private void UpdateBusyVisuals()
    {
        busyIndicator.IsVisible = Busy;
        busyIndicator.IsRunning = Busy;
        cancelConnectButton.IsVisible = connectCts_ is not null;
        Repaint();
    }

    private void Repaint()
    {
        BridgeConnection? active = coordinator_.Active;
        bridgePickerLabel.Text = active?.DisplayName ?? "Saved bridges";
        bridgePickerLabel.TextColor = active is null
            ? ThemePalette.Get("MutedColor")
            : ThemePalette.Get("TextColor");

        string stateKey = active is null
            ? "unknown"
            : active.IsOffline ? "offline" : active.IsConnected ? "synced" : "unknown";
        Color stateColor = DotColor(stateKey);
        bridgeStateDot.Color = stateColor;
        bridgeStateLabel.TextColor = stateColor;
        bridgeStateLabel.Text = active is null
            ? "no bridge"
            : active.IsOffline ? "offline" : active.IsConnected ? "connected · live" : active.StatusText;
        statusLabel.Text = statusNote_ ?? (active is null
            ? "No bridge yet — add one below."
            : active.IsOffline ? active.OfflineText : active.Bridge.Url);

        // One action at a time: a connect in flight owns the bridge.
        connectButton.IsEnabled = !Busy;
        signInButton.IsEnabled = !Busy && active is not null;
        viewOfflineButton.IsVisible = !Busy && active?.CanUseOffline == true;
    }

    /// <summary>
    /// Opens the synced dataset without a bridge account. Only offered while the bridge is
    /// unreachable; the access rule clears it as soon as the bridge answers again.
    /// </summary>
    private void OnViewOfflineClicked(object? sender, EventArgs e)
    {
        if (coordinator_.Active is not { CanUseOffline: true } active)
        {
            return;
        }

        statusNote_ = null;
        errorLabel.Text = string.Empty;
        active.EnterOfflineViewer();
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (Busy)
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
            // The bridge is on the phone now; the add form has done its job.
            addBridgeCard.IsVisible = false;
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

    private void OnNewBridgeTapped(object? sender, TappedEventArgs e) => OpenAddCard();

    private void OnHideAddTapped(object? sender, EventArgs e) => addBridgeCard.IsVisible = false;

    /// <summary>Opens the add-bridge card and puts the cursor in the host field.</summary>
    private void OpenAddCard()
    {
        addBridgeCard.IsVisible = true;
        hostEntry.Focus();
    }

    /// <summary>
    /// The bridge row is the saved-bridge picker. It opens the themed sheet instead of the
    /// platform action sheet, so the list keeps the app's palette; picking a row activates it.
    /// </summary>
    private async void OnBridgeRowTapped(object? sender, TappedEventArgs e)
    {
        // Switching stays available while the start-up search runs (the pick cancels it);
        // only an operation that cannot be superseded — adding a bridge — keeps the picker shut.
        if ((Busy && !startingUp_) || coordinator_.Bridges.Count == 0)
        {
            return;
        }

        BridgeConnection? picked = await bridgeSheet.ShowAsync(coordinator_.Bridges);
        if (picked is null || ReferenceEquals(picked, coordinator_.Active))
        {
            return;
        }

        // The picked bridge takes the link: stop the start-up search before it can answer for
        // the bridge being left behind.
        startupCts_?.Cancel();
        BeginBusy();
        errorLabel.Text = string.Empty;
        statusNote_ = "connecting to " + picked.DisplayName + "…";
        Repaint();
        try
        {
            await coordinator_.ActivateAsync(picked, CancellationToken.None);
        }
        catch (Exception ex)
        {
            errorLabel.Text = "✗ could not use " + picked.DisplayName + " · " + ex.Message;
        }
        finally
        {
            statusNote_ = null;
            EndBusy();
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (bridgeSheet.IsOpen)
        {
            bridgeSheet.Dismiss();
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private static Color DotColor(string key) => key switch
    {
        "synced" => ThemePalette.Get("GoodColor"),
        "error" or "offline" => ThemePalette.Get("BadColor"),
        _ => ThemePalette.Get("MutedColor")
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
