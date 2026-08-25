using Avalonia.Controls;
using OpcBridge.Hmi.Designer.ViewModels;

namespace OpcBridge.Hmi.Designer.Views;

public partial class DesignerWindow : Window
{
    public DesignerWindow()
    {
        InitializeComponent();
        var vm = new DesignerViewModel();
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
    }
}
