using Avalonia.Controls;
using Avalonia.Input;
using OpcBridge.Hmi.ViewModels.Widgets;

namespace OpcBridge.Hmi.Controls.Widgets;

public partial class NumericWidgetView : UserControl
{
    public NumericWidgetView() => InitializeComponent();

    /// <summary>Enter and Space open the faceplate, the keyboard equivalent of the click.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            OpenFaceplate();
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => OpenFaceplate();

    private void OpenFaceplate()
    {
        if (DataContext is NumericWidgetViewModel vm && vm.OpenFaceplateCommand.CanExecute(null))
        {
            vm.OpenFaceplateCommand.Execute(null);
        }
    }
}
