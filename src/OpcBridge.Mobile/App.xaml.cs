using Microsoft.Maui.ApplicationModel;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile;

public partial class App : Application
{
    private readonly BridgeCoordinator coordinator_;
    private readonly AppShell shell_;
    private bool signedIn_;

    public App(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator_ = coordinator;
        coordinator.Load();
        shell_ = new AppShell();
        MainPage = shell_;
        // The shell starts on the sign-in page, so the first rule evaluation only records
        // where the app already is.
        signedIn_ = MobileAccess.IsGranted(coordinator_.Active);
        coordinator_.Changed += OnCoordinatorChanged;
    }

    private void OnCoordinatorChanged() => MainThread.BeginInvokeOnMainThread(ApplyAccessRule);

    /// <summary>
    /// The one place access is decided: a signed-in account opens the tabs, anything else —
    /// a sign-out, a switched-to bridge with no session, a refused session — returns the app
    /// to the sign-in page.
    /// </summary>
    private void ApplyAccessRule()
    {
        bool signedIn = MobileAccess.IsGranted(coordinator_.Active);
        if (signedIn == signedIn_)
        {
            return;
        }

        signedIn_ = signedIn;
        _ = NavigateAsync(signedIn ? "//logic" : "//login");
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
