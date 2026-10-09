using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile;

public partial class App : Application
{
    public App(BridgeCoordinator coordinator)
    {
        InitializeComponent();
        coordinator.Load();
        MainPage = new AppShell();
    }
}
