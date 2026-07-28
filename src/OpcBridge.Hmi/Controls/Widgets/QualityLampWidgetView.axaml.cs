using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using OpcBridge.Hmi.ViewModels.Widgets;

namespace OpcBridge.Hmi.Controls.Widgets;

public partial class QualityLampWidgetView : UserControl
{
    public QualityLampWidgetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateLamp();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateLamp();
    }

    private void UpdateLamp()
    {
        if (Content is not Border border)
        {
            return;
        }

        if (border.Child is not StackPanel sp || sp.Children.Count == 0 || sp.Children[0] is not Ellipse ellipse)
        {
            return;
        }

        if (DataContext is QualityLampWidgetViewModel vm)
        {
            ellipse.Fill = vm.IsGood switch
            {
                true => Brushes.LimeGreen,
                false => Brushes.OrangeRed,
                _ => Brushes.Gray
            };
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is QualityLampWidgetViewModel vm && vm.OpenFaceplateCommand.CanExecute(null))
        {
            vm.OpenFaceplateCommand.Execute(null);
        }
    }
}
