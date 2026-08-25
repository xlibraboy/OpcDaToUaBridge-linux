using Avalonia.Controls;
using Avalonia.Input;
using OpcBridge.Hmi.ViewModels.Widgets;

namespace OpcBridge.Hmi.Controls.Widgets;

public partial class BoolIndicatorWidgetView : UserControl
{
    public BoolIndicatorWidgetView() => InitializeComponent();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is BoolIndicatorWidgetViewModel vm && vm.OpenFaceplateCommand.CanExecute(null))
        {
            vm.OpenFaceplateCommand.Execute(null);
        }
    }
}
