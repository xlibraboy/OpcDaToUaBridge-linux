using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.Controls;

/// <summary>Raised after a drag-zoom selection on the plot.</summary>
public sealed class TrendZoomRequestedEventArgs : EventArgs
{
    public TrendZoomRequestedEventArgs(DateTime fromUtc, DateTime toUtc)
    {
        FromUtc = fromUtc;
        ToUtc = toUtc;
    }

    public DateTime FromUtc { get; }

    public DateTime ToUtc { get; }
}

/// <summary>
/// VTScada-style historical trend with two pen layouts: "Stacked" (one strip per pen,
/// each with its own Y axis on the left; digital pens get a square-wave strip with
/// optional state text instead of numbers) and "Mixed" (all pens overlaid on one
/// plot, each analog pen mapped to the full plot height with a color-coded legend
/// listing every pen's min-max scale; digital pens share a bottom band). A crosshair
/// follows the mouse with a per-strip value chip plus a floating time chip; a pin
/// command drops a blue cursor line that stays put while the data keeps scrolling.
/// Layout (strips, axes, chips, pen table in the host window) follows the classic
/// SCADA data viewer; colors/fonts stay the app theme.
/// </summary>
public sealed class TrendChartControl : Control
{
    public static readonly StyledProperty<IEnumerable<TrendSample>?> SamplesProperty =
        AvaloniaProperty.Register<TrendChartControl, IEnumerable<TrendSample>?>(nameof(Samples));

    public static readonly StyledProperty<IEnumerable<TrendSeries>?> SeriesProperty =
        AvaloniaProperty.Register<TrendChartControl, IEnumerable<TrendSeries>?>(nameof(Series));

    public static readonly StyledProperty<DateTime> FromUtcProperty =
        AvaloniaProperty.Register<TrendChartControl, DateTime>(nameof(FromUtc));

    public static readonly StyledProperty<DateTime> ToUtcProperty =
        AvaloniaProperty.Register<TrendChartControl, DateTime>(nameof(ToUtc));

    /// <summary>Y-axis mapping for analog pens: "Shared" (one scale) or "Percent" (each strip 0..100%).</summary>
    public static readonly StyledProperty<string?> YAxisModeProperty =
        AvaloniaProperty.Register<TrendChartControl, string?>(nameof(YAxisMode), "Shared");

    /// <summary>Pen layout: "Stacked" (one strip per pen) or "Mixed" (all pens overlaid on one plot).</summary>
    public static readonly StyledProperty<string?> LayoutModeProperty =
        AvaloniaProperty.Register<TrendChartControl, string?>(nameof(LayoutMode), "Stacked");

    public static readonly StyledProperty<double?> AlarmHighProperty =
        AvaloniaProperty.Register<TrendChartControl, double?>(nameof(AlarmHigh));

    // Single-pen (faceplate) compat properties: axis + unit + line style for Samples.
    public static readonly StyledProperty<double> YMinProperty =
        AvaloniaProperty.Register<TrendChartControl, double>(nameof(YMin));

    public static readonly StyledProperty<double> YMaxProperty =
        AvaloniaProperty.Register<TrendChartControl, double>(nameof(YMax));

    public static readonly StyledProperty<double> YStepProperty =
        AvaloniaProperty.Register<TrendChartControl, double>(nameof(YStep));

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<TrendChartControl, string?>(nameof(Unit));

    public static readonly StyledProperty<string?> TrendStyleProperty =
        AvaloniaProperty.Register<TrendChartControl, string?>(nameof(TrendStyle), "Continuous");

    public static readonly StyledProperty<double?> AlarmLowProperty =
        AvaloniaProperty.Register<TrendChartControl, double?>(nameof(AlarmLow));

    /// <summary>UTC timestamps of the pinned blue time cursors; multiple lines allowed.</summary>
    public static readonly StyledProperty<System.Collections.Immutable.ImmutableList<DateTime>> PinsProperty =
        AvaloniaProperty.Register<TrendChartControl, System.Collections.Immutable.ImmutableList<DateTime>>(
            nameof(Pins), System.Collections.Immutable.ImmutableList<DateTime>.Empty);

    /// <summary>True while a drag-zoom selection is active (hides hover chips).</summary>
    public static readonly StyledProperty<bool> EnableRangeZoomProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(EnableRangeZoom), false);

    /// <summary>Prefixes each hover value chip with its pen name (used while the pen table is collapsed).</summary>
    public static readonly StyledProperty<bool> ShowNamesInHoverProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(ShowNamesInHover));

    /// <summary>Prefixes each hover value chip with its pen name (used while the pen table is collapsed).</summary>
    public bool ShowNamesInHover
    {
        get => GetValue(ShowNamesInHoverProperty);
        set => SetValue(ShowNamesInHoverProperty, value);
    }

    /// <summary>Single-series input (faceplate mini-trend).</summary>
    public IEnumerable<TrendSample>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>
    /// Multi-pen input: one entry per strip. When set and non-empty it takes precedence
    /// over <see cref="Samples"/>.
    /// </summary>
    public IEnumerable<TrendSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public DateTime FromUtc
    {
        get => GetValue(FromUtcProperty);
        set => SetValue(FromUtcProperty, value);
    }

    public DateTime ToUtc
    {
        get => GetValue(ToUtcProperty);
        set => SetValue(ToUtcProperty, value);
    }

    public string? YAxisMode
    {
        get => GetValue(YAxisModeProperty);
        set => SetValue(YAxisModeProperty, value);
    }

    /// <summary>"Stacked" (one strip per pen, each with its own axis) or "Mixed" (all pens on one plot).</summary>
    public string? LayoutMode
    {
        get => GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    /// <summary>Alarm limits for the single-pen (faceplate) mode; per-pen limits come on the series.</summary>
    public double? AlarmHigh
    {
        get => GetValue(AlarmHighProperty);
        set => SetValue(AlarmHighProperty, value);
    }

    public double? AlarmLow
    {
        get => GetValue(AlarmLowProperty);
        set => SetValue(AlarmLowProperty, value);
    }

    /// <summary>Bottom of the single-pen Y axis (faceplate mode).</summary>
    public double YMin
    {
        get => GetValue(YMinProperty);
        set => SetValue(YMinProperty, value);
    }

    /// <summary>Top of the single-pen Y axis (faceplate mode).</summary>
    public double YMax
    {
        get => GetValue(YMaxProperty);
        set => SetValue(YMaxProperty, value);
    }

    /// <summary>Gridline step of the single-pen Y axis (faceplate mode).</summary>
    public double YStep
    {
        get => GetValue(YStepProperty);
        set => SetValue(YStepProperty, value);
    }

    /// <summary>Tag's engineering unit appended to chips (faceplate mode).</summary>
    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>"Continuous" or "Step" trace rendering (faceplate mode).</summary>
    public string? TrendStyle
    {
        get => GetValue(TrendStyleProperty);
        set => SetValue(TrendStyleProperty, value);
    }

    /// <summary>UTC timestamps of the pinned blue time cursors; multiple lines allowed.</summary>
    public System.Collections.Immutable.ImmutableList<DateTime> Pins
    {
        get => GetValue(PinsProperty);
        set => SetValue(PinsProperty, value);
    }

    /// <summary>When true, drag on the plot selects a time range and raises <see cref="ZoomRequested"/>.</summary>
    public bool EnableRangeZoom
    {
        get => GetValue(EnableRangeZoomProperty);
        set => SetValue(EnableRangeZoomProperty, value);
    }

    /// <summary>Raised when the operator drag-selects a time range on the plot.</summary>
    public event EventHandler<TrendZoomRequestedEventArgs>? ZoomRequested;

    /// <summary>Raised when the operator double-clicks the plot (zoom back out).</summary>
    public event EventHandler? ZoomResetRequested;

    /// <summary>Raised when the pin set changes (add, remove or drag); after any pin mutation.</summary>
    public event EventHandler? PinsChanged;

    static TrendChartControl()
    {
        AffectsRender<TrendChartControl>(
            SamplesProperty,
            SeriesProperty,
            FromUtcProperty,
            ToUtcProperty,
            YAxisModeProperty,
            LayoutModeProperty,
            AlarmHighProperty,
            AlarmLowProperty,
            PinsProperty,
            YMinProperty,
            YMaxProperty,
            YStepProperty,
            UnitProperty,
            TrendStyleProperty,
            ShowNamesInHoverProperty);
    }

    public TrendChartControl()
    {
        // Opaque background makes the whole chart a hit region for crosshair/pin/zoom.
        Cursor = new Cursor(StandardCursorType.Cross);
        AddHandler(Gestures.DoubleTappedEvent, OnChartDoubleTapped);
    }

    private static bool IsStepStyle(string? trendStyle) =>
        !string.IsNullOrWhiteSpace(trendStyle)
        && string.Equals(trendStyle.Trim(), "Step", StringComparison.OrdinalIgnoreCase);

    private bool IsPercentAxisMode => string.Equals(YAxisMode, "Percent", StringComparison.OrdinalIgnoreCase);

    private bool IsMixedLayout => string.Equals(LayoutMode, "Mixed", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pens to draw: <see cref="Series"/> when set, else the single-series props.</summary>
    private IReadOnlyList<TrendSeries> GetSeries()
    {
        if (Series is not null)
        {
            TrendSeries[] list = Series as TrendSeries[] ?? Series.ToArray();
            if (list.Length > 0)
            {
                return list;
            }
        }

        IReadOnlyList<TrendSample> samples = Samples is null
            ? Array.Empty<TrendSample>()
            : Samples as IReadOnlyList<TrendSample> ?? Samples.ToArray();
        (double, double, double)? fixedAxis = YStep > 0 && YMax > YMin ? (YMin, YMax, YStep) : null;
        return new[]
        {
            new TrendSeries(
                string.Empty,
                Unit ?? string.Empty,
                TrendStyle ?? "Continuous",
                TrendSeriesPalette.ColorFor(0),
                samples,
                FixedAxis: fixedAxis)
        };
    }

    private Point? cursorPoint_;
    private bool zoomDragging_;
    private double zoomAnchorX_;
    private double zoomCurrentX_;
    private int draggedPin_ = -1;
    private bool pinDragMoved_;

    /// <summary>Horizontal tolerance (px) for grabbing a pin line with the pointer.</summary>
    private const double PinGrabTolerance = 6;

    // Geometry + window captured at the last successful render, used by hit mapping.
    private double layoutPlotLeft_;
    private double layoutPlotWidth_;
    private DateTime layoutFromUtc_;
    private DateTime layoutToUtc_;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (zoomDragging_)
        {
            zoomCurrentX_ = position.X;
            InvalidateVisual();
            return;
        }

        if (draggedPin_ >= 0)
        {
            MovePinTo(draggedPin_, position.X);
            pinDragMoved_ = true;
            InvalidateVisual();
            return;
        }

        // Show the grab cursor when hovering a pin line.
        bool overPin = FindPinNear(position.X) >= 0;
        Cursor = overPin
            ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(StandardCursorType.Cross);

        if (cursorPoint_ != position)
        {
            cursorPoint_ = position;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point position = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;

        // Ctrl+Click adds/removes a blue time cursor at the pointer; plain press near an
        // existing cursor grabs it for dragging.
        if (props.IsLeftButtonPressed && layoutPlotWidth_ > 0
            && position.X >= layoutPlotLeft_ && position.X <= layoutPlotLeft_ + layoutPlotWidth_)
        {
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            int hit = FindPinNear(position.X);
            if (ctrl)
            {
                double ticks = (layoutToUtc_ - layoutFromUtc_).Ticks;
                DateTime pinned = layoutFromUtc_ + TimeSpan.FromTicks((long)(ticks * (position.X - layoutPlotLeft_) / layoutPlotWidth_));
                var pins = Pins.ToList();
                if (hit >= 0)
                {
                    pins.RemoveAt(hit); // Ctrl+Click on a line removes it
                }
                else
                {
                    pins.Add(pinned);
                }

                Pins = System.Collections.Immutable.ImmutableList.ToImmutableList(pins);
                PinsChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (hit >= 0)
            {
                draggedPin_ = hit;
                pinDragMoved_ = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (!EnableRangeZoom || !props.IsLeftButtonPressed)
        {
            return;
        }

        if (layoutPlotWidth_ <= 0 || layoutToUtc_ <= layoutFromUtc_)
        {
            return;
        }

        zoomAnchorX_ = position.X;
        zoomCurrentX_ = position.X;
        zoomDragging_ = true;
        cursorPoint_ = null;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (draggedPin_ >= 0)
        {
            if (pinDragMoved_)
            {
                PinsChanged?.Invoke(this, EventArgs.Empty);
            }

            draggedPin_ = -1;
            pinDragMoved_ = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (zoomDragging_)
        {
            zoomDragging_ = false;
            e.Pointer.Capture(null);
            Point position = e.GetPosition(this);
            CommitZoomSelection(position.X);
            InvalidateVisual();
            e.Handled = true;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (zoomDragging_)
        {
            zoomDragging_ = false;
            InvalidateVisual();
        }

        draggedPin_ = -1;
        pinDragMoved_ = false;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (cursorPoint_.HasValue)
        {
            cursorPoint_ = null;
            InvalidateVisual();
        }
    }

    /// <summary>Index of the pin line under the pointer X, or -1.</summary>
    private int FindPinNear(double x)
    {
        if (layoutPlotWidth_ <= 0)
        {
            return -1;
        }

        long ticks = (layoutToUtc_ - layoutFromUtc_).Ticks;
        for (int i = 0; i < Pins.Count; i++)
        {
            double pinX = layoutPlotLeft_ + layoutPlotWidth_ * (Pins[i] - layoutFromUtc_).Ticks / (double)ticks;
            if (Math.Abs(pinX - x) <= PinGrabTolerance)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Moves the pin at <paramref name="index"/> to the pointer X (clamped to the plot).</summary>
    private void MovePinTo(int index, double x)
    {
        if (layoutPlotWidth_ <= 0)
        {
            return;
        }

        double frac = Math.Clamp((x - layoutPlotLeft_) / layoutPlotWidth_, 0, 1);
        DateTime pinned = layoutFromUtc_ + TimeSpan.FromTicks((long)((layoutToUtc_ - layoutFromUtc_).Ticks * frac));
        Pins = Pins.SetItem(index, pinned);
    }

    private void CommitZoomSelection(double endX)
    {
        double x0 = Math.Max(layoutPlotLeft_, Math.Min(zoomAnchorX_, endX));
        double x1 = Math.Min(layoutPlotLeft_ + layoutPlotWidth_, Math.Max(zoomAnchorX_, endX));
        if (x1 - x0 < 10)
        {
            return; // click without a drag is not a zoom
        }

        double ticks = (layoutToUtc_ - layoutFromUtc_).Ticks;
        if (ticks <= 0)
        {
            return;
        }

        DateTime fromUtc = layoutFromUtc_ + TimeSpan.FromTicks((long)(ticks * (x0 - layoutPlotLeft_) / layoutPlotWidth_));
        DateTime toUtc = layoutFromUtc_ + TimeSpan.FromTicks((long)(ticks * (x1 - layoutPlotLeft_) / layoutPlotWidth_));
        if (toUtc <= fromUtc)
        {
            return;
        }

        ZoomRequested?.Invoke(this, new TrendZoomRequestedEventArgs(fromUtc, toUtc));
    }

    private void OnChartDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!EnableRangeZoom || layoutPlotWidth_ <= 0)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (position.X < layoutPlotLeft_ || position.X > layoutPlotLeft_ + layoutPlotWidth_)
        {
            return;
        }

        // Double-click on the plot = "zoom back out"; the host decides whether a zoom
        // is actually active.
        ZoomResetRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // Palette matches Themes/SharedResources.axaml (dark theme).
    private static readonly SolidColorBrush PanelBrush = new(Color.Parse("#1B1B20"));
    private static readonly SolidColorBrush StripBrush = new(Color.Parse("#141419"));
    private static readonly SolidColorBrush AxisLabelBrush = new(Color.Parse("#B4B4BE"));
    private static readonly SolidColorBrush GridBrush = new(Color.Parse("#3A3A44"));
    private static readonly SolidColorBrush FrameBrush = new(Color.Parse("#555560"));
    private static readonly SolidColorBrush CrosshairBrush = new(Color.Parse("#7A7A86"));
    private static readonly SolidColorBrush PinBrush = new(Color.Parse("#4A90D9"));
    private static readonly SolidColorBrush DotFillBrush = new(Color.Parse("#FFFFFF"));
    private static readonly SolidColorBrush ReadoutBgBrush = new(Color.FromArgb(0xEC, 0x23, 0x23, 0x29));
    private static readonly SolidColorBrush LegendBackdropBrush = new(Color.FromArgb(0xC8, 0x17, 0x17, 0x1D));
    private static readonly SolidColorBrush LegendBorderBrush = new(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ZoomFillBrush = new(Color.FromArgb(0x40, 0x4F, 0xC3, 0xF7));
    private static readonly SolidColorBrush AlarmBandBrush = new(Color.FromArgb(0x1F, 0xEF, 0x53, 0x50));
    private static readonly Pen AlarmPen = new(new SolidColorBrush(Color.Parse("#EF5350")), 1) { DashStyle = DashStyle.Dash };

    /// <summary>Solid brush for a pen color (hex string from the palette or view model).</summary>
    private static SolidColorBrush BrushFor(string colorHex)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(colorHex));
        }
        catch
        {
            return new SolidColorBrush(Color.Parse("#4FC3F7"));
        }
    }

    /// <summary>Translucent fill under a pen trace (16% opacity of the pen color).</summary>
    private static SolidColorBrush FillFor(string colorHex)
    {
        try
        {
            Color color = Color.Parse(colorHex);
            return new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        }
        catch
        {
            return new SolidColorBrush(Color.FromArgb(0x26, 0x4F, 0xC3, 0xF7));
        }
    }

    private const double FontSize = 10;
    private const double TopPad = 6;
    private const double RightPad = 10;
    private const double BottomPad = 24;
    private const double StripGap = 6;

    private readonly record struct StripLayout(
        TrendSeries Series,
        TrendSample[] Used,
        Rect Bounds,
        bool IsDigital,
        bool Percent,
        double AxisMin,
        double AxisMax,
        double AxisStep);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        context.DrawRectangle(PanelBrush, null, new Rect(0, 0, width, height));

        DateTime from = FromUtc;
        DateTime to = ToUtc;
        if (to <= from)
        {
            return;
        }

        double totalTicks = (to - from).Ticks;
        if (totalTicks <= 0)
        {
            return;
        }

        IReadOnlyList<TrendSeries> series = GetSeries();

        // ---- Resolve each pen's strip axis ----
        List<StripLayout> strips = BuildStrips(series, IsPercentAxisMode);
        if (strips.Count == 0)
        {
            return;
        }

        var typeface = new Typeface(FontFamily.Default);
        if (IsMixedLayout)
        {
            RenderMixed(context, strips, from, to, totalTicks, width, height, typeface);
            return;
        }

        RenderStacked(context, strips, from, to, totalTicks, width, height, typeface);
    }

    /// <summary>
    /// Stacked renderer: one strip per pen stacked vertically, each with its own Y axis
    /// labels in the shared left gutter and one shared time axis at the bottom.
    /// </summary>
    private void RenderStacked(
        DrawingContext context,
        List<StripLayout> strips,
        DateTime from,
        DateTime to,
        double totalTicks,
        double width,
        double height,
        Typeface typeface)
    {
        // ---- Measure Y labels to size the shared left axis gutter ----
        double maxLabelWidth = 0;
        foreach (StripLayout strip in strips)
        {
            if (strip.IsDigital)
            {
                // State text (or 0/1) is drawn inside the strip; no left labels.
                continue;
            }

            string hi = FormatAxisLabel(strip.AxisMax, strip);
            string lo = FormatAxisLabel(strip.AxisMin, strip);
            maxLabelWidth = Math.Max(maxLabelWidth, MeasureText(typeface, hi).Width);
            maxLabelWidth = Math.Max(maxLabelWidth, MeasureText(typeface, lo).Width);
        }

        double plotLeft = Math.Min(maxLabelWidth + 14, width * 0.45);
        double plotRight = width - RightPad;
        double plotWidth = plotRight - plotLeft;
        if (plotWidth < 40)
        {
            return;
        }

        // ---- Stack the strips evenly over the available height ----
        double stripHeight = Math.Max(24, (height - TopPad - BottomPad - StripGap * (strips.Count - 1)) / strips.Count);
        double y = TopPad;
        for (int i = 0; i < strips.Count; i++)
        {
            Rect bounds = new(plotLeft, y, plotWidth, Math.Min(stripHeight, height - BottomPad - y));
            if (bounds.Height < 16)
            {
                break;
            }

            strips[i] = strips[i] with { Bounds = bounds };
            DrawStrip(context, strips[i], bounds, from, to, plotLeft, plotRight, typeface);
            y = bounds.Bottom + StripGap;
        }

        double plotBottom = y - StripGap;

        // ---- Bottom time axis: gridline ticks + labels ----
        var gridPen = new Pen(GridBrush, 1);
        TimeSpan timeStep = TrendTimeAxis.StepFor(to - from);
        for (DateTime tick = TrendTimeAxis.Floor(from, timeStep); tick <= to; tick += timeStep)
        {
            if (tick < from)
            {
                continue;
            }

            double x = XOf(tick, from, totalTicks, plotLeft, plotWidth);
            FormattedText label = MeasureText(typeface, FormatTime(tick));
            double labelLeft = x - label.Width / 2;
            if (labelLeft >= plotLeft - 2 && labelLeft + label.Width <= width - 2)
            {
                context.DrawText(label, new Point(labelLeft, height - BottomPad + 6));
            }
        }

        // ---- Pinned blue time cursors (drawn after strips, before the frame) ----
        foreach (DateTime pin in Pins)
        {
            if (pin < from || pin > to)
            {
                continue;
            }

            double pinX = XOf(pin, from, totalTicks, plotLeft, plotWidth);
            var pinPen = new Pen(PinBrush, 1.5);
            context.DrawLine(pinPen, new Point(pinX, TopPad), new Point(pinX, plotBottom));
            var head = MeasureText(typeface, "▼");
            context.DrawText(
                new FormattedText("▼", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, PinBrush),
                new Point(pinX - head.Width / 2, plotBottom + 2));

            // Stationary readout at each pin: time chip under the axis + value chip per pen.
            DrawPinTimeChip(context, pin, pinX, plotLeft, plotRight, plotBottom, width, typeface);
            DrawValueChips(context, strips, pin, pinX, plotRight, typeface);
        }

        // ---- Hover crosshair: one line across all strips + per-strip value chips ----
        if (!zoomDragging_ && cursorPoint_ is { } cursor)
        {
            DrawHoverCrosshair(context, strips, cursor, from, totalTicks, plotLeft, plotRight, plotBottom, width, typeface);
        }

        // ---- drag-zoom selection band ----
        if (zoomDragging_)
        {
            double x0 = Math.Max(plotLeft, Math.Min(zoomAnchorX_, zoomCurrentX_));
            double x1 = Math.Min(plotRight, Math.Max(zoomAnchorX_, zoomCurrentX_));
            if (x1 > x0)
            {
                context.DrawRectangle(ZoomFillBrush, new Pen(BrushFor(strips[0].Series.Color), 1), new Rect(x0, TopPad, x1 - x0, plotBottom - TopPad));
            }
        }

        // Capture geometry + window for pointer hit mapping.
        layoutPlotLeft_ = plotLeft;
        layoutPlotWidth_ = plotWidth;
        layoutFromUtc_ = from;
        layoutToUtc_ = to;
    }

    /// <summary>
    /// Mixed (overlay) renderer: all pens share one plot. Each analog pen maps its own
    /// scale to the full plot height (or to 0..100 % in percent mode); digital pens
    /// share a square-wave band along the bottom. A color-coded legend lists every
    /// pen's visible min-max scale so per-pen ranges stay readable.
    /// </summary>
    private void RenderMixed(
        DrawingContext context,
        List<StripLayout> strips,
        DateTime from,
        DateTime to,
        double totalTicks,
        double width,
        double height,
        Typeface typeface)
    {
        bool percent = IsPercentAxisMode;

        double maxLabelWidth = percent ? MeasureText(typeface, "100%").Width : 0;
        double plotLeft = Math.Min(maxLabelWidth + 14, width * 0.45);
        double plotRight = width - RightPad;
        double plotWidth = plotRight - plotLeft;
        if (plotWidth < 40)
        {
            return;
        }

        double plotTop = TopPad;
        double plotBottom = height - BottomPad;
        double plotHeight = plotBottom - plotTop;
        if (plotHeight < 40)
        {
            return;
        }

        Rect plot = new(plotLeft, plotTop, plotWidth, plotHeight);
        double bandHeight = Math.Max(28, plotHeight * 0.25);
        Rect digitalBand = new(plotLeft, plotBottom - bandHeight, plotWidth, bandHeight);

        context.DrawRectangle(StripBrush, null, plot);

        var gridPen = new Pen(GridBrush, 1);

        // Horizontal gridlines: labeled 0..100 % in percent mode; unlabeled quarters when
        // each pen carries its own scale (the legend lists those ranges instead).
        if (percent)
        {
            for (int i = 0; i <= 5; i++)
            {
                double yPos = plotBottom - plotHeight * i / 5.0;
                context.DrawLine(gridPen, new Point(plotLeft, yPos), new Point(plotRight, yPos));
                FormattedText label = MeasureText(typeface, i * 20 + "%");
                double textY = Math.Max(plotTop + 1, Math.Min(plotBottom - label.Height - 1, yPos - label.Height / 2));
                context.DrawText(label, new Point(plotLeft - 8 - label.Width, textY));
            }
        }
        else
        {
            for (int i = 1; i < 4; i++)
            {
                double yPos = plotTop + plotHeight * i / 4.0;
                context.DrawLine(gridPen, new Point(plotLeft, yPos), new Point(plotRight, yPos));
            }
        }

        // Shared vertical (time) gridlines — labels go on the shared bottom axis.
        TimeSpan timeStep = TrendTimeAxis.StepFor(to - from);
        for (DateTime tick = TrendTimeAxis.Floor(from, timeStep); tick <= to; tick += timeStep)
        {
            if (tick < from)
            {
                continue;
            }

            double x = XOf(tick, from, totalTicks, plotLeft, plotWidth);
            context.DrawLine(gridPen, new Point(x, plotTop), new Point(x, plotBottom));
            FormattedText timeLabel = MeasureText(typeface, FormatTime(tick));
            double labelLeft = x - timeLabel.Width / 2;
            if (labelLeft >= plotLeft - 2 && labelLeft + timeLabel.Width <= width - 2)
            {
                context.DrawText(timeLabel, new Point(labelLeft, plotBottom + 6));
            }
        }

        // Overlay every pen on the plot; digital pens share the bottom band.
        for (int i = 0; i < strips.Count; i++)
        {
            Rect bounds = strips[i].IsDigital ? digitalBand : plot;
            strips[i] = strips[i] with { Bounds = bounds };
            DrawStrip(context, strips[i], bounds, from, to, plotLeft, plotRight, typeface, compact: true);
        }

        context.DrawRectangle(null, new Pen(FrameBrush, 1), plot);

        // Pen scale legend: min-max of each pen's visible scale, color-coded.
        DrawScaleLegend(context, strips, plot, typeface);

        // Pinned blue time cursors (drawn after the legend so they stay on top of it).
        foreach (DateTime pin in Pins)
        {
            if (pin < from || pin > to)
            {
                continue;
            }

            double pinX = XOf(pin, from, totalTicks, plotLeft, plotWidth);
            var pinPen = new Pen(PinBrush, 1.5);
            context.DrawLine(pinPen, new Point(pinX, plotTop), new Point(pinX, plotBottom));
            var head = MeasureText(typeface, "▼");
            context.DrawText(
                new FormattedText("▼", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, PinBrush),
                new Point(pinX - head.Width / 2, plotBottom + 2));

            // Stationary readout at each pin: time chip under the axis + value chip per pen.
            DrawPinTimeChip(context, pin, pinX, plotLeft, plotRight, plotBottom, width, typeface);
            DrawValueChips(context, strips, pin, pinX, plotRight, typeface);
        }

        // Hover crosshair: one line across the plot + a value chip per pen.
        if (!zoomDragging_ && cursorPoint_ is { } cursor)
        {
            DrawHoverCrosshair(context, strips, cursor, from, totalTicks, plotLeft, plotRight, plotBottom, width, typeface);
        }

        // drag-zoom selection band
        if (zoomDragging_)
        {
            double x0 = Math.Max(plotLeft, Math.Min(zoomAnchorX_, zoomCurrentX_));
            double x1 = Math.Min(plotRight, Math.Max(zoomAnchorX_, zoomCurrentX_));
            if (x1 > x0)
            {
                context.DrawRectangle(ZoomFillBrush, new Pen(BrushFor(strips[0].Series.Color), 1), new Rect(x0, plotTop, x1 - x0, plotBottom - plotTop));
            }
        }

        // Capture geometry + window for pointer hit mapping.
        layoutPlotLeft_ = plotLeft;
        layoutPlotWidth_ = plotWidth;
        layoutFromUtc_ = from;
        layoutToUtc_ = to;
    }

    /// <summary>
    /// Draws the color-coded legend of each visible pen's scale range (min-max with
    /// unit) in the top-left of the mixed plot, so per-pen ranges stay visible.
    /// </summary>
    private static void DrawScaleLegend(DrawingContext context, IReadOnlyList<StripLayout> strips, Rect plot, Typeface typeface)
    {
        // Measure every row first so the backdrop fits exactly behind the legend.
        double lineHeight = FontSize + 5;
        var rows = new List<(string Text, string Color)>();
        double maxTextWidth = 0;
        foreach (StripLayout strip in strips)
        {
            string range = FormatAxisLabel(strip.AxisMin, strip) + " – " + FormatAxisLabel(strip.AxisMax, strip);
            string suffix = strip.IsDigital
                ? string.Empty
                : strip.Percent
                    ? " %"
                    : string.IsNullOrWhiteSpace(strip.Series.Unit) ? string.Empty : " " + strip.Series.Unit.Trim();
            maxTextWidth = Math.Max(maxTextWidth, MeasureText(typeface, range + suffix).Width);
            rows.Add((range + suffix, strip.Series.Color));
        }

        if (rows.Count == 0)
        {
            return;
        }

        double backdropHeight = Math.Min(rows.Count * lineHeight + 8, plot.Height - 8);
        var backdrop = new Rect(plot.Left + 6, plot.Top + 4, maxTextWidth + 32, backdropHeight);
        context.DrawRectangle(LegendBackdropBrush, new Pen(LegendBorderBrush, 1), backdrop);

        double x = backdrop.Left + 6;
        double y = backdrop.Top + 4;
        foreach ((string text, string color) in rows)
        {
            if (y + lineHeight > backdrop.Bottom)
            {
                break; // legend must not outgrow the plot
            }

            context.DrawRectangle(BrushFor(color), null, new Rect(x, y + 3, 8, 8));
            context.DrawText(MeasureText(typeface, text), new Point(x + 12, y));
            y += lineHeight;
        }
    }

    /// <summary>
    /// Resolves each visible pen into a strip: digital pens get a fixed 0..1 band,
    /// percent mode maps every analog pen to a 0..100 band, and the default gives each
    /// strip its own auto-fitted scale (VTScada-style independent vertical axes).
    /// </summary>
    private static List<StripLayout> BuildStrips(IReadOnlyList<TrendSeries> series, bool percentMode)
    {
        var strips = new List<StripLayout>(series.Count);
        foreach (TrendSeries pen in series)
        {
            if (!pen.Visible)
            {
                continue;
            }

            TrendSample[] used = pen.Samples?
                .Where(s => double.IsFinite(s.V))
                .ToArray() ?? Array.Empty<TrendSample>();

            if (pen.IsBoolean)
            {
                // Digital strip: square wave between the bottom and top of the band.
                strips.Add(new StripLayout(pen, used, default, IsDigital: true, Percent: false, 0, 1, 1));
                continue;
            }

            if (pen.UsePercentAxis || (percentMode && pen.FixedAxis is null))
            {
                strips.Add(new StripLayout(pen, used, default, IsDigital: false, Percent: true, 0, 100, 20));
                continue;
            }

            if (pen.FixedAxis is { } fixedAxis && fixedAxis.Max > fixedAxis.Min)
            {
                strips.Add(new StripLayout(pen, used, default, IsDigital: false, Percent: false, fixedAxis.Min, fixedAxis.Max, fixedAxis.Step));
                continue;
            }

            // Independent per-pen scale: fit this strip's own data with nice ticks.
            double lo = used.Length == 0 ? 0 : used.Min(s => s.V);
            double hi = used.Length == 0 ? 1 : used.Max(s => s.V);
            TrendAxis resolved = TrendScale.Resolve(autoRange: true, typeRange: null, lo, hi);
            double min = resolved.IsValid ? resolved.Min : 0;
            double max = resolved.IsValid ? resolved.Max : 1;
            double step = resolved.IsValid ? resolved.Step : 1;
            strips.Add(new StripLayout(pen, used, default, IsDigital: false, Percent: false, min, max, step));
        }

        return strips;
    }

    private static double XOf(DateTime time, DateTime from, double totalTicks, double plotLeft, double plotWidth)
    {
        double ratio = Math.Max(0.0, Math.Min(1.0, (time - from).Ticks / totalTicks));
        return plotLeft + ratio * plotWidth;
    }

    private static double YOf(double value, Rect bounds, double axisMin, double axisMax)
    {
        double span = axisMax - axisMin;
        if (!(span > 0))
        {
            return bounds.Bottom;
        }

        double clamped = Math.Max(axisMin, Math.Min(axisMax, value));
        return bounds.Bottom - (clamped - axisMin) / span * bounds.Height;
    }

    /// <summary>Draws one strip: frame, gridlines, Y labels (or state text), alarm bands, trace.
    /// In compact (mixed layout) mode the shared plot already provides background,
    /// frame, gridlines and time lines, so only the trace, alarms and state text draw.</summary>
    private void DrawStrip(
        DrawingContext context,
        StripLayout strip,
        Rect bounds,
        DateTime from,
        DateTime to,
        double plotLeft,
        double plotRight,
        Typeface typeface,
        bool compact = false)
    {
        double totalTicks = (to - from).Ticks;
        double plotWidth = plotRight - plotLeft;
        double frameTop = bounds.Top - 1;
        var frameRect = new Rect(bounds.Left, frameTop, bounds.Width, bounds.Height + 2);

        // Strip background + frame (the mixed renderer draws the shared plot frame).
        if (!compact)
        {
            context.DrawRectangle(StripBrush, null, frameRect);
        }

        // Horizontal gridlines + Y labels (analog strips only).
        var gridPen = new Pen(GridBrush, 1);
        if (!compact && !strip.IsDigital)
        {
            double intervalCount = Math.Round((strip.AxisMax - strip.AxisMin) / strip.AxisStep);
            int gridCount = (int)Math.Max(1, intervalCount);
            for (int i = 0; i <= gridCount; i++)
            {
                double value = i == gridCount ? strip.AxisMax : strip.AxisMin + i * strip.AxisStep;
                double yPos = YOf(value, bounds, strip.AxisMin, strip.AxisMax);
                context.DrawLine(gridPen, new Point(bounds.Left, yPos), new Point(bounds.Right, yPos));
                FormattedText label = MeasureText(typeface, FormatAxisLabel(value, strip));
                double textY = yPos - label.Height / 2;
                textY = Math.Max(frameTop + 1, Math.Min(frameRect.Bottom - label.Height - 1, textY));
                context.DrawText(label, new Point(bounds.Left - 8 - label.Width, textY));
            }
        }

        // Vertical (time) gridlines — no labels here, labels go on the shared bottom axis.
        // (Mixed layout draws one shared set across the plot instead.)
        if (!compact)
        {
            TimeSpan timeStep = TrendTimeAxis.StepFor(to - from);
            for (DateTime tick = TrendTimeAxis.Floor(from, timeStep); tick <= to; tick += timeStep)
            {
                if (tick < from)
                {
                    continue;
                }

                double x = XOf(tick, from, totalTicks, plotLeft, plotWidth);
                context.DrawLine(gridPen, new Point(x, frameTop), new Point(x, frameRect.Bottom));
            }
        }

        // Alarm threshold overlays (per-pen limits, single-pen mode also honors the window-level props).
        double? alarmHigh = strip.Series.AlarmLimits?.High ?? AlarmHigh;
        double? alarmLow = strip.Series.AlarmLimits?.Low ?? AlarmLow;
        if (alarmHigh is { } h && double.IsFinite(h) && h >= strip.AxisMin && h <= strip.AxisMax)
        {
            double yPos = YOf(h, bounds, strip.AxisMin, strip.AxisMax);
            if (!compact)
            {
                context.DrawRectangle(AlarmBandBrush, null, new Rect(bounds.Left, frameTop, bounds.Width, Math.Max(0, yPos - frameTop)));
            }

            context.DrawLine(AlarmPen, new Point(bounds.Left, yPos), new Point(bounds.Right, yPos));
        }

        if (alarmLow is { } l && double.IsFinite(l) && l >= strip.AxisMin && l <= strip.AxisMax)
        {
            double yPos = YOf(l, bounds, strip.AxisMin, strip.AxisMax);
            if (!compact)
            {
                context.DrawRectangle(AlarmBandBrush, null, new Rect(bounds.Left, yPos, bounds.Width, Math.Max(0, frameRect.Bottom - yPos)));
            }

            context.DrawLine(AlarmPen, new Point(bounds.Left, yPos), new Point(bounds.Right, yPos));
        }

        // Trace (fill + line or square wave) + last-sample marker.
        if (strip.Used.Length > 0)
        {
            var trace = new List<Point>(strip.Used.Length);
            foreach (TrendSample sample in strip.Used)
            {
                if (sample.T < from || sample.T > to)
                {
                    continue;
                }

                double x = XOf(sample.T, from, totalTicks, plotLeft, plotWidth);
                double yPos = strip.IsDigital
                    ? bounds.Bottom - Math.Clamp(sample.V, 0, 1) * bounds.Height
                    : YOf(sample.V, bounds, strip.AxisMin, strip.AxisMax);
                trace.Add(new Point(x, yPos));
            }

            if (strip.IsDigital && trace.Count > 1)
            {
                // Square wave: hold each level until the next sample.
                var stepped = new List<Point>(trace.Count * 2);
                stepped.Add(trace[0]);
                for (int i = 1; i < trace.Count; i++)
                {
                    stepped.Add(new Point(trace[i].X, trace[i - 1].Y));
                    stepped.Add(trace[i]);
                }

                trace = stepped;
            }
            else if (!strip.IsDigital && ShouldStep(strip.Series.TrendStyle) && trace.Count > 1)
            {
                var stepped = new List<Point>(trace.Count * 2);
                stepped.Add(trace[0]);
                for (int i = 1; i < trace.Count; i++)
                {
                    stepped.Add(new Point(trace[i].X, trace[i - 1].Y));
                    stepped.Add(trace[i]);
                }

                trace = stepped;
            }

            if (trace.Count > 1)
            {
                SolidColorBrush color = BrushFor(strip.Series.Color);
                // Area fill only in stacked layout: several overlaid fills turn to mud.
                if (!strip.IsDigital && !compact)
                {
                    var fillGeometry = new StreamGeometry();
                    using (StreamGeometryContext ctx = fillGeometry.Open())
                    {
                        ctx.BeginFigure(new Point(trace[0].X, bounds.Bottom), true);
                        ctx.LineTo(trace[0]);
                        for (int i = 1; i < trace.Count; i++)
                        {
                            ctx.LineTo(trace[i]);
                        }

                        ctx.LineTo(new Point(trace[trace.Count - 1].X, bounds.Bottom));
                        ctx.EndFigure(true);
                    }

                    context.DrawGeometry(FillFor(strip.Series.Color), null, fillGeometry);
                }

                var lineGeometry = new StreamGeometry();
                using (StreamGeometryContext ctx = lineGeometry.Open())
                {
                    ctx.BeginFigure(trace[0], false);
                    for (int i = 1; i < trace.Count; i++)
                    {
                        ctx.LineTo(trace[i]);
                    }

                    ctx.EndFigure(false);
                }

                context.DrawGeometry(null, new Pen(color, strip.Series.StrokeWidth > 0 ? strip.Series.StrokeWidth : 1.6), lineGeometry);

                // Last sample marker.
                Point last = trace[trace.Count - 1];
                context.DrawEllipse(color, null, last, 2.6, 2.6);
            }

            // Digital strip: state labels ("Stopped"/"Running") instead of numbers.
            if (strip.IsDigital)
            {
                string? offLabel = strip.Series.StateLabels is { } labels && labels.Length > 0 ? labels[0] : null;
                string? onLabel = strip.Series.StateLabels is { } labels2 && labels2.Length > 1 ? labels2[1] : null;
                DrawStateText(context, typeface, offLabel ?? "0", bounds.Left + 6, bounds.Bottom - 14);
                DrawStateText(context, typeface, onLabel ?? "1", bounds.Left + 6, bounds.Top + 2);
            }
        }

        // Strip frame drawn last so the trace is clipped visually by the border.
        if (!compact)
        {
            context.DrawRectangle(null, new Pen(FrameBrush, 1), frameRect);
        }
    }

    private static bool ShouldStep(string? trendStyle) => IsStepStyle(trendStyle);

    private static void DrawStateText(DrawingContext context, Typeface typeface, string text, double x, double y)
    {
        var layout = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize - 1,
            AxisLabelBrush);
        context.DrawText(layout, new Point(x, y));
    }

    /// <summary>
    /// Crosshair: one vertical line spanning all strips, a value chip on each strip at
    /// the crosshair time, and a floating time chip near the mouse.
    /// </summary>
    private void DrawHoverCrosshair(
        DrawingContext context,
        IReadOnlyList<StripLayout> strips,
        Point cursor,
        DateTime from,
        double totalTicks,
        double plotLeft,
        double plotRight,
        double plotBottom,
        double width,
        Typeface typeface)
    {
        if (cursor.X < plotLeft || cursor.X > plotRight || cursor.Y > plotBottom + 4)
        {
            return;
        }

        double plotWidth = plotRight - plotLeft;
        var crosshairPen = new Pen(CrosshairBrush, 1);
        context.DrawLine(crosshairPen, new Point(cursor.X, TopPad), new Point(cursor.X, plotBottom));

        // Time chip under the bottom axis (or above it when near the bottom).
        DateTime time = from + TimeSpan.FromTicks((long)(totalTicks * (cursor.X - plotLeft) / Math.Max(1, plotWidth)));
        string timeText = time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var timeLayout = new FormattedText(
            timeText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            AxisLabelBrush);
        const double chipPadX = 6;
        double timeChipWidth = timeLayout.Width + chipPadX * 2;
        double timeChipX = Math.Max(plotLeft, Math.Min(cursor.X - timeChipWidth / 2, width - RightPad - timeChipWidth));
        double timeChipY = plotBottom + 4;
        context.DrawRectangle(ReadoutBgBrush, new Pen(FrameBrush, 1), new RoundedRect(new Rect(timeChipX, timeChipY, timeChipWidth, timeLayout.Height + 4), 3, 3));
        context.DrawText(timeLayout, new Point(timeChipX + chipPadX, timeChipY + 2));

        // Value chip per strip: chip shows the value interpolated at the crosshair time.
        DrawValueChips(context, strips, time, cursor.X, plotRight, typeface);
    }

    /// <summary>
    /// Draws one value chip per visible strip at the given time, anchored right of
    /// <paramref name="anchorX"/> (flipped left near the right edge). Shared by the
    /// hover crosshair and the pinned-cursor readout, so a pin shows the same numbers
    /// the mouse does — but stationary, without needing the pointer on the chart.
    /// </summary>
    private void DrawValueChips(
        DrawingContext context,
        IReadOnlyList<StripLayout> strips,
        DateTime time,
        double anchorX,
        double plotRight,
        Typeface typeface)
    {
        const double chipPadX = 6;
        foreach (StripLayout strip in strips)
        {
            if (strip.Used.Length == 0)
            {
                continue;
            }

            double? valueAt = ValueAt(strip.Used, time);
            if (valueAt is not { } value)
            {
                continue;
            }

            double yPos = strip.IsDigital
                ? strip.Bounds.Top + strip.Bounds.Height / 2
                : YOf(value, strip.Bounds, strip.AxisMin, strip.AxisMax);
            string text = FormatValueChip(value, strip);
            var layout = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
                FontSize,
                BrushFor(strip.Series.Color));
            double chipWidth = layout.Width + chipPadX * 2;
            double chipHeight = layout.Height + 4;
            // Chip sits right of the anchor, vertically at the value; flip left near the right edge.
            double chipX = anchorX + 8 + chipWidth > plotRight ? anchorX - 8 - chipWidth : anchorX + 8;
            double chipY = Math.Max(strip.Bounds.Top + 1, Math.Min(yPos - chipHeight / 2, strip.Bounds.Bottom - chipHeight - 1));
            context.DrawRectangle(ReadoutBgBrush, new Pen(BrushFor(strip.Series.Color), 1), new RoundedRect(new Rect(chipX, chipY, chipWidth, chipHeight), 3, 3));
            context.DrawText(layout, new Point(chipX + chipPadX, chipY + 2));
        }
    }

    /// <summary>Time chip under the axis for the pinned cursor (pin-brushed, like the hover one).</summary>
    private void DrawPinTimeChip(
        DrawingContext context,
        DateTime pin,
        double pinX,
        double plotLeft,
        double plotRight,
        double plotBottom,
        double width,
        Typeface typeface)
    {
        string timeText = pin.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var timeLayout = new FormattedText(
            timeText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            PinBrush);
        const double chipPadX = 6;
        double timeChipWidth = timeLayout.Width + chipPadX * 2;
        double timeChipX = Math.Max(plotLeft, Math.Min(pinX - timeChipWidth / 2, width - RightPad - timeChipWidth));
        context.DrawRectangle(ReadoutBgBrush, new Pen(PinBrush, 1), new RoundedRect(new Rect(timeChipX, plotBottom + 4, timeChipWidth, timeLayout.Height + 4), 3, 3));
        context.DrawText(timeLayout, new Point(timeChipX + chipPadX, plotBottom + 6));
    }

    /// <summary>
    /// Value at the crosshair time: the sample at or just before it (sample-and-hold),
    /// or null when the strip has no data before that point.
    /// </summary>
    private static double? ValueAt(TrendSample[] used, DateTime time)
    {
        TrendSample? best = null;
        foreach (TrendSample sample in used)
        {
            if (sample.T > time)
            {
                break;
            }

            best = sample;
        }

        return best is { } hit ? hit.V : null;
    }

    private string FormatValueChip(double value, StripLayout strip)
    {
        string text = FormatNumber(value);
        if (strip.Percent)
        {
            text += " %";
        }
        else if (!string.IsNullOrWhiteSpace(strip.Series.Unit))
        {
            text += " " + strip.Series.Unit.Trim();
        }

        // With the pen table collapsed the chips are the only place a trace is
        // identified, so each one carries its pen name.
        return ShowNamesInHover ? strip.Series.Name + " " + text : text;
    }

    private static string FormatAxisLabel(double value, StripLayout strip)
    {
        if (strip.Percent)
        {
            return Math.Round(value) + "%";
        }

        return FormatNumber(value);
    }

    private static string FormatNumber(double value)
    {
        double rounded = Math.Round(value, 8);
        if (rounded == 0)
        {
            return "0";
        }

        double magnitude = Math.Abs(rounded);
        if (magnitude >= 1e15)
        {
            return rounded.ToString("0.###E+0", CultureInfo.InvariantCulture);
        }

        if (rounded == Math.Truncate(rounded))
        {
            return rounded.ToString("0", CultureInfo.InvariantCulture);
        }

        return rounded.ToString("0.########", CultureInfo.InvariantCulture)
            .TrimEnd('0')
            .TrimEnd('.');
    }

    private static string FormatTime(DateTime tick)
    {
        // 24h window: show dates; shorter windows: plain clock time.
        return tick.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static FormattedText MeasureText(Typeface typeface, string text)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            AxisLabelBrush);
    }
}
