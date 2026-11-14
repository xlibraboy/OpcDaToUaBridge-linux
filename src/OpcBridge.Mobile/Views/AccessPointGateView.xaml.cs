using Microsoft.Maui.ApplicationModel;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile.Views;

/// <summary>
/// The full-screen online lock shown over Sign in while the phone is not on the bound plant
/// access point. It is a view, not a page, so it never disturbs the shell's navigation: the
/// Sign in screen stays behind it and opens normally once the gate clears.
/// </summary>
public partial class AccessPointGateView : ContentView
{
    private AccessPointGateStatus? status_;
    private bool busy_;

    public AccessPointGateView()
    {
        InitializeComponent();
    }

    public event Action? PrimaryRequested;

    public event Action? ConfigureRequested;

    public event Action? ContinueOfflineRequested;

    public event Action? ControlSignInRequested;

    public bool IsOpen => overlayRoot.IsVisible;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        ThemeManager.Changed -= OnThemeChanged;
        ThemeManager.Changed += OnThemeChanged;
    }

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        base.OnHandlerChanging(args);
        if (args.NewHandler is null)
        {
            ThemeManager.Changed -= OnThemeChanged;
        }
    }

    public void Show(AccessPointGateStatus status)
    {
        status_ = status;
        errorLabel.IsVisible = false;
        overlayRoot.IsVisible = true;
        Render();
    }

    public void Hide()
    {
        overlayRoot.IsVisible = false;
        errorLabel.IsVisible = false;
        SetBusy(false);
    }

    public void SetBusy(bool busy)
    {
        busy_ = busy;
        primaryButton.IsEnabled = !busy;
        busyIndicator.IsVisible = busy;
        busyIndicator.IsRunning = busy;
    }

    public void SetError(string? error)
    {
        errorLabel.Text = error ?? string.Empty;
        errorLabel.IsVisible = !string.IsNullOrWhiteSpace(error);
    }

    private void Render()
    {
        if (status_ is not { } status)
        {
            return;
        }

        string tint = status.State switch
        {
            AccessPointGateState.ConnectedToBound => "Good",
            AccessPointGateState.NotBound => "Muted",
            AccessPointGateState.PermissionRequired or AccessPointGateState.Unverifiable => "Warn",
            _ => "Bad"
        };

        bool neutral = status.State == AccessPointGateState.NotBound;
        Color color = ThemePalette.Get(tint + "Color");
        Color background = ThemePalette.Get(neutral ? "SurfaceAltColor" : tint + "BgColor");
        Color border = ThemePalette.Get(neutral ? "BorderColor" : tint + "BorderColor");
        Color text = ThemePalette.Get(neutral ? "MutedColor" : tint + "TextColor");
        iconTile.BackgroundColor = background;
        iconTile.Stroke = new SolidColorBrush(border);
        eyebrowLabel.TextColor = color;
        statePill.BackgroundColor = background;
        statePill.Stroke = new SolidColorBrush(border);
        statePillLabel.TextColor = text;
        statePillLabel.Text = StateBadge(status.State);

        (string title, string body) = status.State switch
        {
            AccessPointGateState.NotBound => (
                "Plant Wi-Fi not provisioned",
                "Sign in with an Engineer or Admin bridge account to assign the plant access point. Operators use their own accounts for live data."),
            AccessPointGateState.PermissionRequired => (
                "Wi-Fi access needed",
                status.DetailText),
            AccessPointGateState.Unverifiable => (
                "Wi-Fi identity hidden",
                status.DetailText),
            AccessPointGateState.ConnectedToOther => (
                "Wrong access point",
                status.DetailText),
            AccessPointGateState.Disconnected => (
                "Plant Wi-Fi required",
                status.DetailText),
            _ => (
                "Plant access point connected",
                status.DetailText)
        };
        titleLabel.Text = title;
        bodyLabel.Text = body;

        boundSsidLabel.Text = status.Bound?.DisplayName ?? "Not provisioned";
        boundBssidLabel.Text = status.Bound is null
            ? "Engineer/Admin setup required"
            : status.Bound.BssidText + " · " + status.Bound.SecurityText;

        bool identityVisible = status.Current.IdentityVisible && status.Current.Connected;
        currentNetworkLabel.Text = !status.Current.WifiEnabled
            ? "Wi-Fi off"
            : status.State == AccessPointGateState.PermissionRequired
                ? "permission needed"
                : identityVisible ? status.Current.Ssid : "not connected";
        currentNetworkLabel.TextColor = status.State switch
        {
            AccessPointGateState.ConnectedToBound => ThemePalette.Get("GoodColor"),
            AccessPointGateState.PermissionRequired or AccessPointGateState.Unverifiable
                => ThemePalette.Get("WarnColor"),
            AccessPointGateState.Disconnected when !status.Current.WifiEnabled
                => ThemePalette.Get("BadColor"),
            AccessPointGateState.ConnectedToOther => ThemePalette.Get("BadColor"),
            _ => ThemePalette.Get("MutedColor")
        };

        primaryButton.Text = status.State switch
        {
            AccessPointGateState.NotBound => "Sign in to provision",
            AccessPointGateState.PermissionRequired when status.Bound is null => "Access point details",
            AccessPointGateState.PermissionRequired => "Allow Wi-Fi access",
            _ when status.Bound is not null => "Connect to " + status.Bound.DisplayName,
            _ => "Connect to the plant AP"
        };
        primaryButton.IsEnabled = !busy_;
        primaryButton.TextColor = ThemePalette.Get("OnPrimaryColor");
        controlSignInButton.IsVisible = status.Bound is not null;
    }

    private static string StateBadge(AccessPointGateState state) => state switch
    {
        AccessPointGateState.ConnectedToBound => "CONNECTED",
        AccessPointGateState.ConnectedToOther => "OTHER NETWORK",
        AccessPointGateState.PermissionRequired => "PERMISSION",
        AccessPointGateState.Unverifiable => "UNVERIFIED",
        AccessPointGateState.NotBound => "NOT PROVISIONED",
        _ => "NOT CONNECTED"
    };

    private void OnThemeChanged() => MainThread.BeginInvokeOnMainThread(Render);

    private void OnPrimaryClicked(object? sender, EventArgs e) => PrimaryRequested?.Invoke();

    private void OnConfigureClicked(object? sender, EventArgs e) => ConfigureRequested?.Invoke();

    private void OnContinueClicked(object? sender, EventArgs e) => ContinueOfflineRequested?.Invoke();

    private void OnControlSignInClicked(object? sender, EventArgs e) => ControlSignInRequested?.Invoke();
}
