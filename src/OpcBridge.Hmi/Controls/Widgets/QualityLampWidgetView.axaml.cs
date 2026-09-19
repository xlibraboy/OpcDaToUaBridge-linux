using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using OpcBridge.Hmi.Themes;
using OpcBridge.Hmi.ViewModels.Widgets;

namespace OpcBridge.Hmi.Controls.Widgets;

public partial class QualityLampWidgetView : UserControl
{
    private QualityLampWidgetViewModel? subscribedVm_;

    public QualityLampWidgetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachToViewModel();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachToViewModel();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachFromViewModel();
    }

    /// <summary>
    /// The lamp reflects a live value, so it has to repaint whenever the tag's quality
    /// changes, not only when it is first bound.
    /// </summary>
    private void AttachToViewModel()
    {
        DetachFromViewModel();

        if (DataContext is QualityLampWidgetViewModel vm)
        {
            subscribedVm_ = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateLamp();
    }

    private void DetachFromViewModel()
    {
        if (subscribedVm_ is not null)
        {
            subscribedVm_.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedVm_ = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(QualityLampWidgetViewModel.IsGood))
        {
            UpdateLamp();
        }
    }

    private void UpdateLamp()
    {
        if (Content is not Border { Child: StackPanel sp } || sp.Children.Count == 0 || sp.Children[0] is not Ellipse ellipse)
        {
            return;
        }

        if (DataContext is QualityLampWidgetViewModel vm)
        {
            ellipse.Fill = vm.IsGood switch
            {
                true => ThemePalette.Brush("QualityGoodBrush", "#61CC69"),
                false => ThemePalette.Brush("QualityBadBrush", "#F05656"),
                _ => ThemePalette.Brush("QualityUncertainBrush", "#EBAA2D")
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
