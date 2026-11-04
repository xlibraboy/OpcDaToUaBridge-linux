using Microsoft.Maui.ApplicationModel;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

public partial class SettingsPage : ContentPage
{
    private readonly BridgeCoordinator coordinator_;
    private CancellationTokenSource? connectCts_;

    public SettingsPage(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        BindableLayout.SetItemsSource(bridgesList, coordinator_.Bridges);
        coordinator_.Changed += OnChanged;
        ThemeManager.Changed += OnThemeChanged;
        aboutVersionLabel.Text = AppInfo.Current.VersionString;
        aboutTextLabel.Text = "OpcBridge Logic · plant logic viewer for the bridge HMI.";
        aboutFooterLabel.Text = "Writes that may already have reached the bridge are never replayed automatically.";
        PaintThemeSelection();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Repaint();
        PaintThemeSelection();
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Repaint);

    private void OnThemeChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        // Converters resolve a colour when the view is built; rebuilding the two lists is what
        // repaints their state colours for the new surface.
        BindableLayout.SetItemsSource(bridgesList, null);
        BindableLayout.SetItemsSource(bridgesList, coordinator_.Bridges);
        BindableLayout.SetItemsSource(outboxList, null);
        Repaint();
        PaintThemeSelection();
    });

    private void PaintThemeSelection()
    {
        ThemeMode mode = ThemeManager.Mode;
        PaintThemeButton(themeSystemButton, mode == ThemeMode.System);
        PaintThemeButton(themeLightButton, mode == ThemeMode.Light);
        PaintThemeButton(themeDarkButton, mode == ThemeMode.Dark);
    }

    private static void PaintThemeButton(Button button, bool selected)
    {
        button.BackgroundColor = selected ? ThemePalette.Get("SurfaceAltColor") : Colors.Transparent;
        button.TextColor = ThemePalette.Get(selected ? "TextColor" : "MutedColor");
        button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
    }

    private void OnThemeClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string name } &&
            Enum.TryParse(name, out ThemeMode mode))
        {
            ThemeManager.SetMode(mode);
            PaintThemeSelection();
        }
    }

    private void Repaint()
    {
        BridgeConnection? active = coordinator_.Active;
        bridgesEmptyLabel.IsVisible = coordinator_.Bridges.Count == 0;
        bridgesCountLabel.Text = coordinator_.Bridges.Count + " saved";
        connectionDot.Color = active is null
            ? ThemePalette.Get("MutedColor")
            : active.IsOffline ? ThemePalette.Get("BadColor")
            : active.IsConnected ? ThemePalette.Get("GoodColor")
            : ThemePalette.Get("WarnColor");
        connectionLabel.Text = active is null
            ? "no bridge selected"
            : active.DisplayName + " · "
                + (active.IsOfflineViewer ? "offline view · structure only"
                    : active.IsOffline ? "offline"
                    : active.IsConnected ? "connected"
                    : active.StatusText);

        MobileSession? session = active?.Session;
        if (session is { Authenticated: true })
        {
            string name = string.IsNullOrWhiteSpace(session.DisplayName) ? session.Username : session.DisplayName;
            sessionAvatar.Text = Initials(name);
            sessionName.Text = name;
            sessionLabel.Text = string.IsNullOrWhiteSpace(session.Role)
                ? "signed in"
                : session.Role + " · signed in";
        }
        else
        {
            sessionAvatar.Text = "—";
            sessionName.Text = "Not signed in";
            sessionLabel.Text = active is null
                ? "select a bridge"
                : session is { AuthEnabled: false }
                    ? "the bridge runs without enforced sign-in"
                    : "the bridge requires sign-in";
        }

        signOutButton.IsEnabled = session?.Authenticated == true;

        Color syncColor = KeyColor(active?.SyncKey ?? "offline");
        syncBadgeLabel.Text = active is null
            ? "NO BRIDGE"
            : active.FailedCount > 0
                ? active.FailedCount + " FAILED"
                    + (active.PendingCount > 0 ? " · " + active.PendingCount + " PENDING" : string.Empty)
                : active.PendingCount > 0
                    ? active.PendingCount + " PENDING"
                    : active.IsOffline ? "OFFLINE" : "SYNCED";
        syncBadgeLabel.TextColor = syncColor;
        syncBadgeBorder.Stroke = new SolidColorBrush(syncColor);
        syncStateValue.Text = active?.SyncText ?? "not connected";
        syncStateValue.TextColor = syncColor;
        syncStateDot.Color = syncColor;

        Color latencyColor = KeyColor(active?.LatencyKey ?? "unknown");
        latencyValue.Text = active?.LatencyText ?? "latency —";
        latencyValue.TextColor = latencyColor;
        latencyDot.Color = latencyColor;
        retrySyncButton.IsEnabled = active is not null;

        IReadOnlyList<OutboxItem> items = active?.OutboxItems ?? Array.Empty<OutboxItem>();
        BindableLayout.SetItemsSource(outboxList, items);
        outboxEmptyLabel.IsVisible = items.Count == 0;
    }

    private static Color KeyColor(string key) => key switch
    {
        "synced" or "good" or "true" or "done" => ThemePalette.Get("GoodColor"),
        "error" or "poor" or "blocked" or "false" => ThemePalette.Get("BadColor"),
        "syncing" or "pending" or "fair" or "current" => ThemePalette.Get("WarnColor"),
        _ => ThemePalette.Get("MutedColor")
    };

    private static string Initials(string name)
    {
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "—",
            1 => parts[0].Length > 1 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant(),
            _ => (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[1][0]))
        };
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        string host = hostEntry.Text?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            connectionMessageLabel.Text = "enter the bridge host or IP first";
            return;
        }

        using CancellationTokenSource cts = new();
        connectCts_ = cts;
        bool cancelled = false;
        connectButton.IsEnabled = false;
        cancelConnectButton.IsVisible = true;
        busyIndicator.IsVisible = true;
        busyIndicator.IsRunning = true;
        // The probe can walk the whole 8080–8180 range; say what is happening right away.
        connectionMessageLabel.Text = "searching for a bridge on " + host + " (up to 8 s)…";
        try
        {
            MobileResult<BridgeConnection> result = await coordinator_.AddOrConnectAsync(
                host,
                nameEntry.Text,
                cts.Token);
            if (!result.Ok)
            {
                // Do not Repaint over this: the operator must see why the address was refused.
                connectionMessageLabel.Text = "✗ " + (result.Error ?? "could not reach that bridge");
                return;
            }

            hostEntry.Text = string.Empty;
            nameEntry.Text = string.Empty;
            Repaint();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            // An async event handler must never let a network failure reach the process: the
            // operator gets the address's answer here instead of the app disappearing.
            connectionMessageLabel.Text = "✗ could not reach " + host + " · " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(connectCts_, cts))
            {
                connectCts_ = null;
            }

            connectButton.IsEnabled = true;
            cancelConnectButton.IsVisible = false;
            busyIndicator.IsRunning = false;
            busyIndicator.IsVisible = false;
            if (cancelled)
            {
                connectionMessageLabel.Text = "search cancelled";
            }
        }
    }

    private void OnCancelConnectClicked(object? sender, EventArgs e)
    {
        connectCts_?.Cancel();
        connectionMessageLabel.Text = "cancelling…";
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
            connectionMessageLabel.Text = "✗ could not use " + connection.DisplayName + " · " + ex.Message;
        }
    }

    private async void OnRemoveBridgeClicked(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border || border.BindingContext is not BridgeConnection connection)
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
            connectionMessageLabel.Text = "✗ could not sign out cleanly · " + ex.Message;
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
