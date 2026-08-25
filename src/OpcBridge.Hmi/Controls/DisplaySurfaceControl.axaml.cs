using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OpcBridge.Hmi.ViewModels;
using OpcBridge.Hmi.ViewModels.Widgets;

namespace OpcBridge.Hmi.Controls;

public partial class DisplaySurfaceControl : UserControl
{
    private WidgetViewModelBase? dragWidget_;
    private Point dragStartCanvas_;
    private double originX_;
    private double originY_;
    private bool dragging_;

    public DisplaySurfaceControl()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
    }

    private DisplaySurfaceViewModel? Surface => DataContext as DisplaySurfaceViewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        DisplaySurfaceViewModel? surface = Surface;
        if (surface is null || !surface.IsDesignMode)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        WidgetViewModelBase? hit = null;
        if (e.Source is Control control)
        {
            hit = FindWidgetViewModel(control);
        }

        surface.SelectWidget(hit);
        if (hit is null)
        {
            return;
        }

        // Use control-relative movement deltas so ScrollViewer offset does not matter.
        dragWidget_ = hit;
        dragStartCanvas_ = e.GetPosition(this);
        originX_ = hit.X;
        originY_ = hit.Y;
        dragging_ = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!dragging_ || dragWidget_ is null)
        {
            return;
        }

        DisplaySurfaceViewModel? surface = Surface;
        if (surface is null || !surface.IsDesignMode)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        double dx = pos.X - dragStartCanvas_.X;
        double dy = pos.Y - dragStartCanvas_.Y;
        surface.MoveWidgetTo(dragWidget_, originX_ + dx, originY_ + dy);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!dragging_)
        {
            return;
        }

        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EndDrag()
    {
        dragging_ = false;
        dragWidget_ = null;
        dragStartCanvas_ = default;
        originX_ = 0;
        originY_ = 0;
    }

    private static WidgetViewModelBase? FindWidgetViewModel(Control? control)
    {
        Control? current = control;
        while (current is not null)
        {
            if (current.DataContext is WidgetViewModelBase widget)
            {
                return widget;
            }

            current = current.Parent as Control;
        }

        return null;
    }
}
