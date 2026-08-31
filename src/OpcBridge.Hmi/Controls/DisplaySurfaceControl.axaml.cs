using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using OpcBridge.Hmi.ViewModels;
using OpcBridge.Hmi.ViewModels.Widgets;

namespace OpcBridge.Hmi.Controls;

public partial class DisplaySurfaceControl : UserControl
{
    private const double GridStep = 32;
    private const double MinSize = 24;

    private WidgetViewModelBase? dragWidget_;
    private Point dragStart_;
    private double originX_;
    private double originY_;
    private double originW_;
    private double originH_;
    private bool dragging_;
    private bool resizing_;
    private bool editNotified_;

    private DisplaySurfaceViewModel? subscribedSurface_;

    public DisplaySurfaceControl()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        DataContextChanged += (_, _) => HookSurface();
    }

    private DisplaySurfaceViewModel? Surface => DataContext as DisplaySurfaceViewModel;

    private void HookSurface()
    {
        if (subscribedSurface_ is not null)
        {
            subscribedSurface_.PropertyChanged -= OnSurfacePropertyChanged;
        }

        subscribedSurface_ = Surface;
        if (subscribedSurface_ is not null)
        {
            subscribedSurface_.PropertyChanged += OnSurfacePropertyChanged;
        }

        RedrawGrid();
    }

    private void OnSurfacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DisplaySurfaceViewModel.ShowGrid)
            or nameof(DisplaySurfaceViewModel.CanvasWidth)
            or nameof(DisplaySurfaceViewModel.CanvasHeight)
            or nameof(DisplaySurfaceViewModel.IsDesignMode))
        {
            RedrawGrid();
        }
    }

    private void RedrawGrid()
    {
        DisplaySurfaceViewModel? surface = Surface;
        Canvas host = GridHost;
        host.Children.Clear();
        if (surface is null || !surface.ShowGrid || !surface.IsDesignMode)
        {
            return;
        }

        var stroke = new SolidColorBrush(Color.Parse("#2E2E36"));
        for (double x = GridStep; x < surface.CanvasWidth; x += GridStep)
        {
            host.Children.Add(new Line
            {
                StartPoint = new Point(x, 0),
                EndPoint = new Point(x, surface.CanvasHeight),
                Stroke = stroke,
                StrokeThickness = 1
            });
        }

        for (double y = GridStep; y < surface.CanvasHeight; y += GridStep)
        {
            host.Children.Add(new Line
            {
                StartPoint = new Point(0, y),
                EndPoint = new Point(surface.CanvasWidth, y),
                Stroke = stroke,
                StrokeThickness = 1
            });
        }
    }

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
        bool onResizeHandle = false;
        if (e.Source is Control control)
        {
            hit = FindWidgetViewModel(control);
            onResizeHandle = InResizeHandle(control);
        }

        surface.SelectWidget(hit);
        if (hit is null)
        {
            return;
        }

        // Use control-relative movement deltas so ScrollViewer offset does not matter.
        dragWidget_ = hit;
        dragStart_ = e.GetPosition(this);
        originX_ = hit.X;
        originY_ = hit.Y;
        originW_ = hit.Width;
        originH_ = hit.Height;
        dragging_ = true;
        resizing_ = onResizeHandle;
        editNotified_ = false;
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
        double dx = pos.X - dragStart_.X;
        double dy = pos.Y - dragStart_.Y;

        // Snapshot undo state once, on first actual movement.
        if (!editNotified_ && (dx != 0 || dy != 0))
        {
            surface.RaiseEditStarted();
            editNotified_ = true;
        }

        if (resizing_)
        {
            surface.ResizeWidgetTo(dragWidget_, originW_ + dx, originH_ + dy);
        }
        else
        {
            surface.MoveWidgetTo(dragWidget_, originX_ + dx, originY_ + dy);
        }

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
        resizing_ = false;
        editNotified_ = false;
        dragWidget_ = null;
        dragStart_ = default;
        originX_ = 0;
        originY_ = 0;
        originW_ = 0;
        originH_ = 0;
    }

    private static bool InResizeHandle(Control? control)
    {
        Control? current = control;
        while (current is not null)
        {
            if (current is Border && current.Classes.Contains("resizeHandle"))
            {
                return true;
            }

            current = current.Parent as Control;
        }

        return false;
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
