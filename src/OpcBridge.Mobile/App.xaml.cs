using Microsoft.Maui.ApplicationModel;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile;

public partial class App : Application
{
    private readonly BridgeCoordinator coordinator_;
    private readonly AccessPointGate accessPointGate_;
    private readonly AppShell shell_;
    private bool signedIn_;

    public App(BridgeCoordinator coordinator, AccessPointGate accessPointGate)
    {
        InitializeComponent();
        ThemeManager.Apply();
        RequestedThemeChanged += OnRequestedThemeChanged;
        coordinator_ = coordinator;
        accessPointGate_ = accessPointGate;
        accessPointGate_.Load();
        coordinator.Load();
        shell_ = new AppShell();
        MainPage = shell_;
        // The shell starts on the sign-in page, so the first rule evaluation only records
        // where the app already is.
        signedIn_ = AccessGranted();
        coordinator_.Changed += OnCoordinatorChanged;
    }

    private void OnCoordinatorChanged() => MainThread.BeginInvokeOnMainThread(ApplyAccessRule);

    /// <summary>
    /// One notification path for both the operator's choice and the phone's own day/night
    /// change: pages that set colours from code repaint, while ThemeColor bindings follow by
    /// themselves.
    /// </summary>
    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(ThemeManager.NotifyChanged);

    /// <summary>
    /// The one place access is decided: a signed-in account opens the tabs, anything else —
    /// a sign-out, a switched-to bridge with no session, a refused session — returns the app
    /// to the sign-in page.
    /// </summary>
    private void ApplyAccessRule()
    {
        bool signedIn = AccessGranted();
        if (signedIn == signedIn_)
        {
            return;
        }

        signedIn_ = signedIn;
        _ = NavigateAsync(signedIn ? "//logic" : "//login");
    }

    /// <summary>
    /// A bridge account opens the tabs only once the phone has a plant AP provisioned (or the
    /// operator is in the local read-only offline view). An unprovisioned account stays on Sign
    /// in so an Engineer/Admin can assign the AP; the bridge data itself is gated separately.
    /// </summary>
    private bool AccessGranted()
    {
        BridgeConnection? active = coordinator_.Active;
        return MobileAccess.IsGranted(active)
            && (accessPointGate_.Binding is not null || active?.IsOfflineViewer == true);
    }

    private async Task NavigateAsync(string route)
    {
        try
        {
            await shell_.GoToAsync(route);
        }
        catch (Exception ex)
        {
            // A failed navigation must not take the viewer down; the next session change tries again.
            System.Diagnostics.Debug.WriteLine("access navigation failed: " + ex.Message);
        }
    }
}
