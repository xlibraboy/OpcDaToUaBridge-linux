using OpcBridge.Mobile.Views;

namespace OpcBridge.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("block", typeof(LogicBlockPage));
    }
}
