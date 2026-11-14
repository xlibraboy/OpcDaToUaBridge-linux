using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

/// <summary>
/// Plant Wi-Fi setup: the one access point the phone may carry bridge traffic on, chosen from
/// a scan (each BSSID separately) with its passphrase. Provisioning is a control-user action:
/// the page is read-only for Operator/Viewer accounts and unlocks for the Engineer/Admin
/// account already signed in to the bridge — the same users that own the Logic web page.
/// </summary>
public partial class AccessPointPage : ContentPage
{
    private readonly AccessPointGate gate_;
    private readonly BridgeCoordinator coordinator_;
    private List<NearbyAccessPoint> accessPoints_ = new();
    private SavedAccessPoint? selected_;
    private CancellationTokenSource? scanCts_;
    private CancellationTokenSource? connectCts_;
    private string? renderedBinding_;
    private bool saving_;
    private bool scanning_;

    public AccessPointPage(AccessPointGate gate, BridgeCoordinator coordinator)
    {
        InitializeComponent();
        gate_ = gate;
        coordinator_ = coordinator;
        Repaint();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        gate_.Changed -= OnGateChanged;
        gate_.Changed += OnGateChanged;
        coordinator_.Changed -= OnCoordinatorChanged;
        coordinator_.Changed += OnCoordinatorChanged;
        ThemeManager.Changed -= OnThemeChanged;
        ThemeManager.Changed += OnThemeChanged;
        gate_.Refresh();
        Repaint();

        if (IsControlUser && accessPoints_.Count == 0 && !gate_.Status.Current.PermissionMissing)
        {
            _ = ScanAsync();
        }
    }

    protected override void OnDisappearing()
    {
        scanCts_?.Cancel();
        connectCts_?.Cancel();
        selected_ = null;
        gate_.Changed -= OnGateChanged;
        coordinator_.Changed -= OnCoordinatorChanged;
        ThemeManager.Changed -= OnThemeChanged;
        base.OnDisappearing();
    }

    private void OnGateChanged() => MainThread.BeginInvokeOnMainThread(Repaint);

    private void OnCoordinatorChanged() => MainThread.BeginInvokeOnMainThread(Repaint);

    private MobileSession? Session => coordinator_.Active?.Session;

    /// <summary>Engineer/Admin only: the same roles that control the Logic web page.</summary>
    private bool IsControlUser => Session is { Authenticated: true } session && session.IsControlUser;

    private void OnThemeChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        RebuildRows();
        Repaint();
    });

    private void Repaint()
    {
        AccessPointGateStatus status = gate_.Status;
        string key = status.State switch
        {
            AccessPointGateState.ConnectedToBound => "Good",
            AccessPointGateState.ConnectedToOther => "Bad",
            AccessPointGateState.PermissionRequired or AccessPointGateState.Unverifiable => "Warn",
            AccessPointGateState.Disconnected => status.Current.WifiEnabled ? "Bad" : "Warn",
            _ => "Muted"
        };
        Color color = ThemePalette.Get(key + "Color");
        statusDot.Color = color;
        statusLabel.TextColor = color;
        statusLabel.Text = status.State switch
        {
            AccessPointGateState.ConnectedToBound => "connected · " + status.Bound?.DisplayName,
            AccessPointGateState.ConnectedToOther =>
                "on " + (string.IsNullOrWhiteSpace(status.Current.Ssid) ? "another network" : status.Current.Ssid),
            _ => status.StateText
        };

        SavedAccessPoint? bound = status.Bound;
        string? bindingBssid = bound?.BssidText;
        if (!string.Equals(renderedBinding_, bindingBssid, StringComparison.Ordinal))
        {
            renderedBinding_ = bindingBssid;
            RebuildRows();
        }

        bool locked = !IsControlUser;
        MobileSession? session = Session;
        bool signedIn = session is { Authenticated: true };
        bool canConnect = bound is not null && gate_.HasStoredPassword(bound);

        boundSsidLabel.Text = bound?.DisplayName ?? "Not provisioned";
        boundBssidLabel.Text = bound is null
            ? "Engineer/Admin setup required"
            : bound.BssidText + " · " + bound.SecurityText;
        boundBadgeLabel.Text = bound is null ? "NOT BOUND" : "BOUND";
        boundBadgeLabel.TextColor = bound is null
            ? ThemePalette.Get("MutedColor")
            : ThemePalette.Get(status.OnlineAllowed ? "GoodTextColor" : "WarnTextColor");
        boundBadge.BackgroundColor = bound is null
            ? ThemePalette.Get("SurfaceAltColor")
            : ThemePalette.Get(status.OnlineAllowed ? "GoodBgColor" : "WarnBgColor");
        boundBadge.StrokeThickness = 0;
        boundCheck.IsVisible = status.OnlineAllowed;
        connectBoundButton.IsVisible = bound is not null && !status.OnlineAllowed;
        connectBoundButton.IsEnabled = !saving_ && canConnect;
        connectBoundButton.Text = bound is null ? "Connect" : "Connect to " + bound.DisplayName;
        forgetButton.IsVisible = !locked && bound is not null;
        forgetButton.IsEnabled = !saving_;
        boundNoteLabel.Text = locked
            ? bound is null
                ? "No access point is provisioned on this phone. An Engineer or Admin bridge account must assign it."
                : "This access point is provisioned for this phone. Only an Engineer or Admin can change it."
            : bound is null
                ? "Bind the access point this phone may use for live data."
                : "The bridge link may only run on this BSSID. Any other Wi-Fi keeps the app offline.";

        provisioningCard.IsVisible = locked;
        string account = signedIn
            ? (string.IsNullOrWhiteSpace(session!.DisplayName) ? session.Username : session.DisplayName)
                + " · " + session.Role
            : string.Empty;
        provisioningNote.Text = bound is null
            ? signedIn
                ? account + " is signed in. Only an Engineer or Admin account can assign the plant AP and its password."
                : "No access point is provisioned. Sign in with an Engineer or Admin bridge account while the phone is on the plant network."
            : canConnect
                ? signedIn
                    ? account + " is signed in. Only an Engineer or Admin can replace this access point or change its password."
                    : "This access point is provisioned and read-only. Sign in with an Engineer or Admin account to change it."
                : "The access point is assigned but its password is missing. An Engineer or Admin must provision it again.";
        provisionAccountButton.Text = signedIn ? "Open Settings ▸ Account" : "Back to sign in";

        selectCard.IsVisible = !locked;
        scanButton.IsVisible = !locked;
        scanButton.IsEnabled = !locked && !saving_ && !scanning_;
        saveButton.IsEnabled = !saving_;
        saveButton.Text = saving_ ? "Connecting…" : "Save & connect";
        saveSpinner.IsVisible = saving_;
        saveSpinner.IsRunning = saving_;
        scanSpinner.IsVisible = scanning_;
        scanSpinner.IsRunning = scanning_;
        credentialsCard.IsVisible = !locked && selected_ is not null;
        foundLabel.Text = scanning_
            ? "scanning…"
            : accessPoints_.Count > 0 ? accessPoints_.Count + " found" : string.Empty;
        emptyNetworksLabel.IsVisible = !scanning_ && accessPoints_.Count == 0;
    }

    private async void OnProvisioningAccountClicked(object? sender, EventArgs e)
    {
        if (Session is { Authenticated: true })
        {
            // The operator is signed in with the wrong role: Settings ▸ Account signs out.
            await Shell.Current.GoToAsync("//settings");
            return;
        }

        await Shell.Current.GoToAsync("//login");
    }

    private void SetTopMessage(string text, bool isError)
    {
        messageLabel.Text = text;
        messageLabel.TextColor = ThemePalette.Get(isError ? "BadColor" : "GoodColor");
        messageLabel.IsVisible = true;
    }

    private void RebuildRows()
    {
        networkRows.Children.Clear();
        foreach (NearbyAccessPoint accessPoint in accessPoints_)
        {
            networkRows.Children.Add(BuildNetworkRow(accessPoint));
        }
    }

    private View BuildNetworkRow(NearbyAccessPoint accessPoint)
    {
        bool isSelected = selected_ is not null
            && AccessPointCodec.SameBssid(selected_.Bssid, accessPoint.Bssid);
        bool isBound = gate_.Binding is { } bound
            && AccessPointCodec.SameBssid(bound.Bssid, accessPoint.Bssid);

        Border row = new()
        {
            BackgroundColor = ThemePalette.Get(isSelected ? "SurfaceColor" : "SurfaceAltColor"),
            Stroke = new SolidColorBrush(ThemePalette.Get(isSelected ? "GoodColor" : "SurfaceAltColor")),
            StrokeThickness = isSelected ? 1 : 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            HeightRequest = 50,
            Padding = new Thickness(10, 0)
        };
        row.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => Select(accessPoint))
        });
        SemanticProperties.SetDescription(
            row,
            "Use " + accessPoint.Ssid + ", BSSID " + accessPoint.BssidText);

        Grid content = new()
        {
            ColumnDefinitions = new ColumnDefinitionCollection(
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)),
            ColumnSpacing = 10
        };

        Border radio = new()
        {
            WidthRequest = 16,
            HeightRequest = 16,
            BackgroundColor = ThemePalette.Get(isSelected ? "GoodColor" : "SurfaceColor"),
            Stroke = new SolidColorBrush(ThemePalette.Get(isSelected ? "GoodColor" : "BorderColor")),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(8) },
            VerticalOptions = LayoutOptions.Center
        };
        if (isSelected)
        {
            radio.Content = new BoxView
            {
                WidthRequest = 7,
                HeightRequest = 7,
                CornerRadius = new CornerRadius(3.5),
                Color = ThemePalette.Get("OnPrimaryColor"),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            };
        }

        content.Add(radio);

        VerticalStackLayout text = new()
        {
            Spacing = 1,
            VerticalOptions = LayoutOptions.Center
        };
        text.Add(new Label
        {
            Text = accessPoint.Ssid,
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = ThemePalette.Get("TextColor"),
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation
        });
        text.Add(new Label
        {
            Text = accessPoint.BssidText,
            FontFamily = "monospace",
            FontSize = 9,
            TextColor = ThemePalette.Get("MutedColor")
        });
        content.Add(text, 1, 0);

        if (accessPoint.Security != AccessPointSecurity.Open)
        {
            content.Add(new Image
            {
                Source = ImageSource.FromFile(IconName("icon_lock")),
                WidthRequest = 12,
                HeightRequest = 12,
                VerticalOptions = LayoutOptions.Center
            }, 2, 0);
        }

        if (isBound)
        {
            Label boundText = new()
            {
                Text = "BOUND",
                FontFamily = "monospace",
                FontSize = 7.5,
                FontAttributes = FontAttributes.Bold,
                TextColor = ThemePalette.Get("GoodTextColor"),
                VerticalOptions = LayoutOptions.Center
            };
            content.Add(new Border
            {
                BackgroundColor = ThemePalette.Get("GoodBgColor"),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(5) },
                HeightRequest = 18,
                Padding = new Thickness(6, 0),
                VerticalOptions = LayoutOptions.Center,
                Content = boundText
            }, 3, 0);
        }

        row.Content = content;
        return row;
    }

    private void Select(NearbyAccessPoint accessPoint)
    {
        selected_ = new SavedAccessPoint(accessPoint.Ssid, accessPoint.Bssid, accessPoint.Security);
        selectedSsidLabel.Text = accessPoint.Ssid;
        selectedBssidLabel.Text = accessPoint.BssidText + " · " + accessPoint.SecurityText;
        securityLabel.Text = accessPoint.SecurityText;
        passwordField.IsVisible = accessPoint.Security != AccessPointSecurity.Open;
        passwordEntry.Text = string.Empty;
        passwordEntry.IsPassword = true;
        revealIcon.Source = ImageSource.FromFile(IconName("icon_eye"));
        bool keepsStored = gate_.Binding is { } bound
            && AccessPointCodec.SameBssid(bound.Bssid, accessPoint.Bssid)
            && accessPoint.Security != AccessPointSecurity.Open;
        credentialNote.Text = accessPoint.Security == AccessPointSecurity.Open
            ? "Open network — no password required."
            : keepsStored
                ? "Password saved on this phone — leave blank to keep it."
                : "The password stays in the phone's secure storage and is never synced to a bridge.";
        SetActionMessage(string.Empty, isError: false);
        RebuildRows();
        Repaint();
    }

    private async Task ScanAsync()
    {
        if (saving_)
        {
            return;
        }

        scanCts_?.Cancel();
        scanCts_?.Dispose();
        scanCts_ = new CancellationTokenSource();
        CancellationToken token = scanCts_.Token;
        scanning_ = true;
        messageLabel.IsVisible = false;
        emptyNetworksLabel.IsVisible = false;
        Repaint();
        try
        {
            MobileResult<IReadOnlyList<NearbyAccessPoint>> result = await gate_.ScanAsync(token);
            if (!result.Ok)
            {
                SetTopMessage("✗ " + (result.Error ?? "scan failed"), isError: true);
                return;
            }

            accessPoints_ = result.Value?.ToList() ?? new List<NearbyAccessPoint>();
            emptyNetworksLabel.Text = accessPoints_.Count == 0
                ? "No networks found — allow nearby Wi-Fi and location, then scan again."
                : string.Empty;
            RebuildRows();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetTopMessage("✗ scan failed · " + exception.Message, isError: true);
        }
        finally
        {
            scanning_ = false;
            Repaint();
        }
    }

    private async void OnScanClicked(object? sender, TappedEventArgs e) => await ScanAsync();

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (saving_ || selected_ is not { } accessPoint)
        {
            return;
        }

        string password = passwordEntry.Text ?? string.Empty;
        bool keepsStored = gate_.Binding is { } bound
            && AccessPointCodec.SameBssid(bound.Bssid, accessPoint.Bssid)
            && accessPoint.Security != AccessPointSecurity.Open;
        if (accessPoint.Security != AccessPointSecurity.Open && password.Length == 0 && !keepsStored)
        {
            SetActionMessage("Enter the Wi-Fi password.", isError: true);
            return;
        }

        using CancellationTokenSource cts = new();
        connectCts_ = cts;
        saving_ = true;
        SetActionMessage(string.Empty, isError: false);
        Repaint();
        try
        {
            MobileResult<AccessPointGateStatus> result = await gate_.BindAsync(accessPoint, password, cts.Token);
            if (!result.Ok)
            {
                SetActionMessage("✗ " + (result.Error ?? "could not join the access point"), isError: true);
                return;
            }

            passwordEntry.Text = string.Empty;
            selected_ = null;
            SetTopMessage(
                "✓ Plant access point assigned. Operators can connect to it, but cannot change it.",
                isError: false);
        }
        catch (OperationCanceledException)
        {
            SetActionMessage("connection cancelled", isError: false);
        }
        catch (Exception exception)
        {
            SetActionMessage("✗ " + exception.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(connectCts_, cts))
            {
                connectCts_ = null;
            }

            saving_ = false;
            Repaint();
        }
    }

    private async void OnConnectBoundClicked(object? sender, EventArgs e)
    {
        if (saving_ || gate_.Binding is null)
        {
            return;
        }

        using CancellationTokenSource cts = new();
        connectCts_ = cts;
        saving_ = true;
        SetActionMessage(string.Empty, isError: false);
        Repaint();
        try
        {
            MobileResult<AccessPointGateStatus> result = await gate_.ConnectAsync(cts.Token);
            if (!result.Ok)
            {
                SetActionMessage("✗ " + (result.Error ?? "could not connect to the access point"), isError: true);
                return;
            }

            SetActionMessage("Connected to " + gate_.Binding!.DisplayName, isError: false);
        }
        catch (OperationCanceledException)
        {
            SetActionMessage("connection cancelled", isError: false);
        }
        catch (Exception exception)
        {
            SetActionMessage("✗ " + exception.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(connectCts_, cts))
            {
                connectCts_ = null;
            }

            saving_ = false;
            Repaint();
        }
    }

    private async void OnForgetClicked(object? sender, EventArgs e)
    {
        if (saving_ || gate_.Binding is null)
        {
            return;
        }

        bool confirmed = await DisplayAlert(
            "Forget access point",
            "Live data will stay off until another plant access point is bound.",
            "Forget",
            "Cancel");
        if (!confirmed)
        {
            return;
        }

        saving_ = true;
        Repaint();
        try
        {
            await gate_.ForgetAsync();
            selected_ = null;
            passwordEntry.Text = string.Empty;
            SetActionMessage("Binding forgotten — online access is off.", isError: false);
        }
        catch (Exception exception)
        {
            SetActionMessage("✗ " + exception.Message, isError: true);
        }
        finally
        {
            saving_ = false;
            Repaint();
        }
    }

    private void OnRevealPasswordClicked(object? sender, TappedEventArgs e)
    {
        passwordEntry.IsPassword = !passwordEntry.IsPassword;
    }

    private async void OnBackClicked(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("..");

    private void SetActionMessage(string text, bool isError)
    {
        actionMessageLabel.Text = text;
        actionMessageLabel.TextColor = ThemePalette.Get(isError ? "BadColor" : "MutedColor");
    }

    private static string IconName(string name) =>
        name + (ThemePalette.IsLight ? "_light" : string.Empty) + ".png";
}
