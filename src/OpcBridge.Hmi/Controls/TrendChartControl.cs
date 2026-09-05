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

/// <summary>Raised after a right-drag zoom selection on the plot.</summary>
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

/// <summary>Raised when the operator clicks a legend row (toggles visibility or cycles color).</summary>
public sealed class TrendLegendEventArgs : EventArgs
{
    public TrendLegendEventArgs(string seriesName)
    {
        SeriesName = seriesName;
    }

    public string SeriesName { get; }
}

/// <summary>
/// Standard SCADA-style trend: numeric Y axis with min/max gridline labels on the left,
/// clock-aligned time axis along the bottom, and value traces drawn across the plot.
/// One or several series can be plotted (single-tag trend = one series, group trend =
/// many); each series has its own color and line style, and an interactive legend maps
/// colors to tags when more than one series is shown (click a swatch to cycle its color,
/// click the row to show/hide the trace). Group trends additionally support a 0..100%
/// normalized axis for mixed units and dedicated lanes for boolean signals. Alarm
/// thresholds (single-tag) render as shaded bands. Right-drag (when
/// <see cref="EnableRangeZoom"/> is set) selects a time range that the host can load via
/// <see cref="ZoomRequested"/>.
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

    public static readonly StyledProperty<double> YMinProperty =
        AvaloniaProperty.Register<TrendChartControl, double>(nameof(YMin));

    public static readonly StyledProperty<double> YMaxProperty =
        AvaloniaProperty.Register<TrendChartControl, double>(nameof(YMax));

    public static readonly StyledProperty<double> YStepProperty =
        AvaloniaProperty.Register<TrendChartControl, double>(nameof(YStep));

    public static readonly StyledProperty<bool> EnableRangeZoomProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(EnableRangeZoom), false);

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<TrendChartControl, string?>(nameof(Unit));

    public static readonly StyledProperty<string?> TrendStyleProperty =
        AvaloniaProperty.Register<TrendChartControl, string?>(nameof(TrendStyle), "Continuous");

    /// <summary>Y-axis mapping: "Shared" (one scale) or "Percent" (each series 0..100%).</summary>
    public static readonly StyledProperty<string?> YAxisModeProperty =
        AvaloniaProperty.Register<TrendChartControl, string?>(nameof(YAxisMode), "Shared");

    /// <summary>High alarm limit; the chart shades everything above it (single-tag trends).</summary>
    public static readonly StyledProperty<double?> AlarmHighProperty =
        AvaloniaProperty.Register<TrendChartControl, double?>(nameof(AlarmHigh));

    /// <summary>Low alarm limit; the chart shades everything below it (single-tag trends).</summary>
    public static readonly StyledProperty<double?> AlarmLowProperty =
        AvaloniaProperty.Register<TrendChartControl, double?>(nameof(AlarmLow));

    /// <summary>Single-series input (used by the faceplate's 1h block).</summary>
    public IEnumerable<TrendSample>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>
    /// Multi-series input: one entry per trace. When set and non-empty it takes
    /// precedence over <see cref="Samples"/>/<see cref="Unit"/>/<see cref="TrendStyle"/>.
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

    /// <summary>Bottom of the Y axis (gridlines/labels drawn down to this value).</summary>
    public double YMin
    {
        get => GetValue(YMinProperty);
        set => SetValue(YMinProperty, value);
    }

    /// <summary>Top of the Y axis.</summary>
    public double YMax
    {
        get => GetValue(YMaxProperty);
        set => SetValue(YMaxProperty, value);
    }

    /// <summary>Value distance between horizontal gridlines.</summary>
    public double YStep
    {
        get => GetValue(YStepProperty);
        set => SetValue(YStepProperty, value);
    }

    /// <summary>
    /// When true, right-drag selects a time range and raises <see cref="ZoomRequested"/>.
    /// </summary>
    public bool EnableRangeZoom
    {
        get => GetValue(EnableRangeZoomProperty);
        set => SetValue(EnableRangeZoomProperty, value);
    }

    /// <summary>Tag's engineering unit (e.g. "°C"), appended to the cursor and stats readouts.</summary>
    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>
    /// How the value trace is drawn: "Continuous" (line through the samples, default) or
    /// "Step" (sample-and-hold — the value is held until the next sample's time, so the
    /// trace steps vertically between samples instead of interpolating a diagonal).
    /// </summary>
    public string? TrendStyle
    {
        get => GetValue(TrendStyleProperty);
        set => SetValue(TrendStyleProperty, value);
    }

    public string? YAxisMode
    {
        get => GetValue(YAxisModeProperty);
        set => SetValue(YAxisModeProperty, value);
    }

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

    /// <summary>Raised when the operator right-drags a time range on the plot.</summary>
    public event EventHandler<TrendZoomRequestedEventArgs>? ZoomRequested;

    /// <summary>Raised when the operator double-clicks the plot (request to zoom back out).</summary>
    public event EventHandler? ZoomResetRequested;

    /// <summary>Raised when the operator clicks a legend row body (toggle trace visibility).</summary>
    public event EventHandler<TrendLegendEventArgs>? LegendVisibilityRequested;

    /// <summary>Raised when the operator clicks a legend color swatch (cycle the trace color).</summary>
    public event EventHandler<TrendLegendEventArgs>? LegendColorRequested;

    static TrendChartControl()
    {
        AffectsRender<TrendChartControl>(
            SamplesProperty,
            SeriesProperty,
            FromUtcProperty,
            ToUtcProperty,
            YMinProperty,
            YMaxProperty,
            YStepProperty,
            UnitProperty,
            TrendStyleProperty,
            YAxisModeProperty,
            AlarmHighProperty,
            AlarmLowProperty);
    }

    public TrendChartControl()
    {
        // Painting an opaque background makes the whole chart a hit region so pointer
        // events (hover cursor, legend clicks, right-drag zoom) are handled right here.
        Cursor = new Cursor(StandardCursorType.Cross);
        AddHandler(Gestures.DoubleTappedEvent, OnChartDoubleTapped);
    }

    private static bool IsStepStyle(string? trendStyle) =>
        !string.IsNullOrWhiteSpace(trendStyle)
        && string.Equals(trendStyle.Trim(), "Step", StringComparison.OrdinalIgnoreCase);

    private bool IsPercentAxisMode => string.Equals(YAxisMode, "Percent", StringComparison.OrdinalIgnoreCase);

    /// <summary>The traces to draw: <see cref="Series"/> when set, else the single-series props.</summary>
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
        return new[]
        {
            new TrendSeries(
                string.Empty,
                Unit ?? string.Empty,
                TrendStyle ?? "Continuous",
                TrendSeriesPalette.ColorFor(0),
                samples)
        };
    }

    private Point? cursorPoint_;
    private bool zoomDragging_;
    private double zoomAnchorX_;
    private double zoomCurrentX_;

    // Geometry + window captured at the last successful render, used by the zoom mapping.
    private double layoutPlotLeft_;
    private double layoutPlotWidth_;
    private DateTime layoutFromUtc_;
    private DateTime layoutToUtc_;

    // Legend hit regions captured at the last render, used for pointer clicks.
    private readonly List<LegendHitRect> legendHitRects_ = new();

    private readonly record struct LegendHitRect(Rect SwatchRect, Rect RowRect, string Name);

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

        // Left click on the legend: swatch cycles the trace color, row toggles visibility.
        if (props.IsLeftButtonPressed && legendHitRects_.Count > 0)
        {
            foreach (LegendHitRect hit in legendHitRects_)
            {
                if (hit.SwatchRect.Contains(position))
                {
                    LegendColorRequested?.Invoke(this, new TrendLegendEventArgs(hit.Name));
                    e.Handled = true;
                    return;
                }

                if (hit.RowRect.Contains(position))
                {
                    LegendVisibilityRequested?.Invoke(this, new TrendLegendEventArgs(hit.Name));
                    e.Handled = true;
                    return;
                }
            }
        }

        if (!EnableRangeZoom || !props.IsRightButtonPressed)
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
    private static readonly SolidColorBrush AxisLabelBrush = new(Color.Parse("#B4B4BE"));
    private static readonly SolidColorBrush GridBrush = new(Color.Parse("#3A3A44"));
    private static readonly SolidColorBrush FrameBrush = new(Color.Parse("#555560"));
    private static readonly SolidColorBrush CrosshairBrush = new(Color.Parse("#7A7A86"));
    private static readonly SolidColorBrush DotFillBrush = new(Color.Parse("#FFFFFF"));
    private static readonly SolidColorBrush ReadoutBgBrush = new(Color.FromArgb(0xEC, 0x23, 0x23, 0x29));
    private static readonly SolidColorBrush ZoomFillBrush = new(Color.FromArgb(0x40, 0x4F, 0xC3, 0xF7));
    private static readonly SolidColorBrush LaneBrush = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush AlarmBandBrush = new(Color.FromArgb(0x1F, 0xEF, 0x53, 0x50));
    private static readonly Pen AlarmPen = new(new SolidColorBrush(Color.Parse("#EF5350")), 1) { DashStyle = DashStyle.Dash };

    /// <summary>Solid brush for a series color (hex string from the palette or view model).</summary>
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

    /// <summary>Translucent fill under a series trace (16% opacity of the series color).</summary>
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

    /// <summary>Series color dimmed for hidden legend rows.</summary>
    private static SolidColorBrush DimBrushFor(string colorHex)
    {
        try
        {
            Color color = Color.Parse(colorHex);
            return new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B));
        }
        catch
        {
            return new SolidColorBrush(Color.FromArgb(0x66, 0x4F, 0xC3, 0xF7));
        }
    }

    private const double FontSize = 11;
    private const double TopPad = 8;
    private const double RightPad = 10;
    private const double BottomPad = 24;
    private const double LaneHeight = 20;
    private const double LaneGap = 2;

    private readonly record struct PlottedSeries(
        TrendSeries Series,
        TrendSample[] Used,
        Point[] Trace,
        Func<double, double> YOf,
        double FillBottom);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        legendHitRects_.Clear();
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        // Opaque background: identical to the hosting card, and it makes the control's
        // whole bounds hit-testable for pointer input.
        context.DrawRectangle(PanelBrush, null, new Rect(0, 0, width, height));

        double yMin = YMin;
        double yMax = YMax;
        double yStep = YStep;
        if (!(yStep > 0) || !(yMax > yMin))
        {
            return;
        }

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
        bool isGroup = series.Count > 1;

        // Boolean signals in a group chart get their own stacked lanes below the analog plot.
        int laneCount = 0;
        if (isGroup)
        {
            foreach (TrendSeries item in series)
            {
                if (item.IsBoolean && item.Visible)
                {
                    laneCount++;
                }
            }
        }

        double laneArea = laneCount > 0 ? laneCount * LaneHeight + (laneCount - 1) * LaneGap : 0;

        // ---- Y axis gridline values (min..max, last forced onto max) ----
        double intervalCount = Math.Round((yMax - yMin) / yStep);
        if (intervalCount < 1)
        {
            intervalCount = 1;
        }

        int gridCount = (int)intervalCount;
        var labelTexts = new string[gridCount + 1];
        for (int i = 0; i <= gridCount; i++)
        {
            labelTexts[i] = FormatNumber(i == gridCount ? yMax : yMin + i * yStep);
        }

        // Measure labels so the plot area leaves room for the widest one.
        var typeface = new Typeface(FontFamily.Default);
        var labelLayouts = new FormattedText[gridCount + 1];
        double maxLabelWidth = 0;
        for (int i = 0; i < labelTexts.Length; i++)
        {
            var layout = new FormattedText(
                labelTexts[i],
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                AxisLabelBrush);
            labelLayouts[i] = layout;
            maxLabelWidth = Math.Max(maxLabelWidth, layout.Width);
        }

        double plotLeft = Math.Min(maxLabelWidth + 14, width * 0.45);
        double plotTop = TopPad;
        double plotRight = width - RightPad;
        double plotBottomMain = height - BottomPad - laneArea;
        double plotWidth = plotRight - plotLeft;
        double plotHeightMain = plotBottomMain - plotTop;
        if (plotWidth < 40 || plotHeightMain < 20)
        {
            return;
        }

        double valueSpan = yMax - yMin;
        double yOfShared(double value)
        {
            double clamped = Math.Max(yMin, Math.Min(yMax, value));
            return plotBottomMain - ((clamped - yMin) / valueSpan) * plotHeightMain;
        }

        double xOf(DateTime time)
        {
            double ratio = Math.Max(0.0, Math.Min(1.0, (time - from).Ticks / (double)totalTicks));
            return plotLeft + ratio * plotWidth;
        }

        // ---- per-series trace points (only samples inside the window) ----
        List<PlottedSeries> plots = BuildPlots(series, isGroup, from, to, xOf, yOfShared, plotBottomMain);
        if (plots.Sum(p => p.Trace.Length) < 2)
        {
            return;
        }

        // ---- horizontal gridlines + labels (analog plot only) ----
        var gridPen = new Pen(GridBrush, 1);
        for (int i = 0; i <= gridCount; i++)
        {
            double y = yOfShared(i == gridCount ? yMax : yMin + i * yStep);
            context.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotRight, y));
            FormattedText label = labelLayouts[i];
            double textY = y - label.Height / 2;
            textY = Math.Max(0, Math.Min(height - label.Height, textY));
            context.DrawText(label, new Point(plotLeft - 8 - label.Width, textY));
        }

        // ---- vertical (time) gridlines + labels ----
        TimeSpan timeStep = TrendTimeAxis.StepFor(to - from);
        double plotBottomWithLanes = height - BottomPad;
        if (timeStep > TimeSpan.Zero)
        {
            for (DateTime tick = TrendTimeAxis.Floor(from, timeStep); tick <= to; tick += timeStep)
            {
                if (tick < from)
                {
                    continue;
                }

                double x = xOf(tick);
                context.DrawLine(gridPen, new Point(x, plotTop), new Point(x, plotBottomWithLanes));

                string text = FormatTime(tick);
                var layout = new FormattedText(
                    text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    FontSize,
                    AxisLabelBrush);
                double labelLeft = x - layout.Width / 2;
                if (labelLeft >= plotLeft - 2 && labelLeft + layout.Width <= width - 2)
                {
                    context.DrawText(layout, new Point(labelLeft, plotBottomWithLanes + 6));
                }
            }
        }

        // ---- alarm threshold overlays (shaded bands, drawn behind the traces) ----
        DrawAlarmOverlays(context, AlarmHigh, AlarmLow, yOfShared, plotLeft, plotTop, plotRight, plotBottomMain);

        // ---- digital lane backgrounds for boolean series ----
        DrawLaneBackgrounds(context, laneCount, plotLeft, plotRight, plotBottomMain);

        // ---- value traces (fill + line + last-sample marker per series) ----
        foreach (PlottedSeries plot in plots)
        {
            if (plot.Trace.Length < 2)
            {
                continue;
            }

            SolidColorBrush color = BrushFor(plot.Series.Color);
            var lineGeometry = new StreamGeometry();
            using (StreamGeometryContext ctx = lineGeometry.Open())
            {
                ctx.BeginFigure(plot.Trace[0], false);
                for (int i = 1; i < plot.Trace.Length; i++)
                {
                    ctx.LineTo(plot.Trace[i]);
                }

                ctx.EndFigure(false);
            }

            var fillGeometry = new StreamGeometry();
            using (StreamGeometryContext ctx = fillGeometry.Open())
            {
                ctx.BeginFigure(new Point(plot.Trace[0].X, plot.FillBottom), true);
                ctx.LineTo(plot.Trace[0]);
                for (int i = 1; i < plot.Trace.Length; i++)
                {
                    ctx.LineTo(plot.Trace[i]);
                }

                ctx.LineTo(new Point(plot.Trace[plot.Trace.Length - 1].X, plot.FillBottom));
                ctx.EndFigure(true);
            }

            context.DrawGeometry(FillFor(plot.Series.Color), null, fillGeometry);
            context.DrawGeometry(null, new Pen(color, 1.6), lineGeometry);

            // Last sample marker (current value)
            Point last = plot.Trace[plot.Trace.Length - 1];
            context.DrawEllipse(color, null, last, 2.6, 2.6);
        }

        // ---- lane label chips (on top of the traces so they stay readable) ----
        DrawLaneLabels(context, plots, plotRight, plotBottomMain, typeface);

        // ---- pinned readout: interactive legend for groups, max/min/avg/delta for single ----
        if (isGroup)
        {
            DrawSeriesLegend(context, series, from, to, plotLeft, plotTop, width, height, typeface);
        }
        else
        {
            DrawStatsReadout(context, plots[0].Used, plots[0].Series.Unit ?? string.Empty, plotLeft, plotTop, width, height, typeface);
        }

        // ---- right-drag zoom selection band ----
        if (zoomDragging_)
        {
            double x0 = Math.Max(plotLeft, Math.Min(zoomAnchorX_, zoomCurrentX_));
            double x1 = Math.Min(plotRight, Math.Max(zoomAnchorX_, zoomCurrentX_));
            if (x1 > x0)
            {
                var band = new Rect(x0, plotTop, x1 - x0, plotHeightMain);
                context.DrawRectangle(ZoomFillBrush, new Pen(BrushFor(plots[0].Series.Color), 1), band);
            }
        }

        // ---- hover cursor: snap to the nearest sample and read out value + time ----
        if (!zoomDragging_
            && cursorPoint_ is { } cursor
            && cursor.X >= plotLeft
            && cursor.X <= plotRight
            && cursor.Y >= plotTop
            && cursor.Y <= plotBottomWithLanes)
        {
            DrawHoverReadout(context, plots, cursor, xOf, plotLeft, plotTop, plotRight, plotBottomMain, width, height, typeface);
        }

        // ---- plot frame ----
        var framePen = new Pen(FrameBrush, 1);
        context.DrawRectangle(null, framePen, new Rect(plotLeft, plotTop, plotWidth, plotHeightMain));

        // Capture the geometry + window for the zoom mapping on pointer input.
        layoutPlotLeft_ = plotLeft;
        layoutPlotWidth_ = plotWidth;
        layoutFromUtc_ = from;
        layoutToUtc_ = to;
    }

    /// <summary>
    /// Filters each visible series to the window, maps samples to pixel points (using the
    /// shared/percent axis for analog series and the lane band for boolean series) and
    /// applies the series' line style (step transform) where configured.
    /// </summary>
    private List<PlottedSeries> BuildPlots(
        IReadOnlyList<TrendSeries> series,
        bool isGroup,
        DateTime from,
        DateTime to,
        Func<DateTime, double> xOf,
        Func<double, double> yOfShared,
        double plotBottomMain)
    {
        var plots = new List<PlottedSeries>(series.Count);
        int laneIndex = 0;
        foreach (TrendSeries item in series)
        {
            if (!item.Visible)
            {
                continue;
            }

            IReadOnlyList<TrendSample> samples = item.Samples ?? Array.Empty<TrendSample>();
            var used = new List<TrendSample>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                TrendSample sample = samples[i];
                if (sample.T < from || sample.T > to || !double.IsFinite(sample.V))
                {
                    continue;
                }

                used.Add(sample);
            }

            // Boolean signals become stacked lanes only in group charts; a single boolean
            // tag keeps its natural 0..1 band on the regular axis.
            bool isLane = isGroup && item.IsBoolean;
            double fillBottom;
            Func<double, double> yFor;
            if (isLane)
            {
                // Boolean lane: value 1 at the top of the band, 0 at the bottom.
                double laneTop = plotBottomMain + laneIndex * (LaneHeight + LaneGap);
                fillBottom = laneTop;
                yFor = v => laneTop + LaneHeight - Math.Clamp(v, 0, 1) * LaneHeight;
            }
            else if (IsPercentAxisMode)
            {
                double min = double.MaxValue;
                double max = double.MinValue;
                foreach (TrendSample sample in used)
                {
                    min = Math.Min(min, sample.V);
                    max = Math.Max(max, sample.V);
                }

                double lo = min == double.MaxValue ? 0 : min;
                double hi = max == double.MinValue ? 100 : max;
                fillBottom = plotBottomMain;
                yFor = v => yOfShared(TrendPercentAxis.PercentFor(v, lo, hi));
            }
            else
            {
                fillBottom = plotBottomMain;
                yFor = v => yOfShared(v);
            }

            // Map to points, then apply the step (sample-and-hold) transform.
            var trace = new List<Point>(used.Count);
            for (int i = 0; i < used.Count; i++)
            {
                trace.Add(new Point(xOf(used[i].T), yFor(used[i].V)));
            }

            if ((IsStepStyle(item.TrendStyle) || isLane) && trace.Count > 1)
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

            plots.Add(new PlottedSeries(item, used.ToArray(), trace.ToArray(), yFor, fillBottom));
            if (isLane)
            {
                laneIndex++;
            }
        }

        return plots;
    }

    /// <summary>Draws the digital lane bands (boolean series) below the analog plot.</summary>
    private static void DrawLaneBackgrounds(
        DrawingContext context,
        int laneCount,
        double plotLeft,
        double plotRight,
        double plotBottomMain)
    {
        if (laneCount == 0)
        {
            return;
        }

        var framePen = new Pen(FrameBrush, 1);
        for (int lane = 0; lane < laneCount; lane++)
        {
            double laneTop = plotBottomMain + lane * (LaneHeight + LaneGap);
            var laneRect = new Rect(plotLeft, laneTop, plotRight - plotLeft, LaneHeight);
            context.DrawRectangle(LaneBrush, framePen, laneRect);
        }
    }

    /// <summary>
    /// Label chips at the right edge of each boolean lane so the tag name sits above its
    /// own track. Drawn after the traces so the chip stays readable.
    /// </summary>
    private static void DrawLaneLabels(
        DrawingContext context,
        IReadOnlyList<PlottedSeries> plots,
        double plotRight,
        double plotBottomMain,
        Typeface typeface)
    {
        foreach (PlottedSeries plot in plots)
        {
            if (!plot.Series.IsBoolean || plot.Trace.Length == 0)
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(plot.Series.Name) ? "Tag" : plot.Series.Name!;
            var label = new FormattedText(
                name,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize - 1,
                DimBrushFor(plot.Series.Color));
            double chipX = plotRight - 4 - label.Width;
            double chipY = plot.FillBottom + (LaneHeight - label.Height) / 2;
            context.DrawRectangle(ReadoutBgBrush, null, new Rect(chipX - 4, chipY - 2, label.Width + 8, label.Height + 4));
            context.DrawText(label, new Point(chipX, chipY));
        }
    }

    /// <summary>Shaded horizontal bands + dashed lines for configured alarm thresholds.</summary>
    private static void DrawAlarmOverlays(
        DrawingContext context,
        double? high,
        double? low,
        Func<double, double> yOf,
        double plotLeft,
        double plotTop,
        double plotRight,
        double plotBottom)
    {
        if (high is { } h && double.IsFinite(h))
        {
            double y = Math.Max(plotTop, Math.Min(plotBottom, yOf(h)));
            context.DrawRectangle(AlarmBandBrush, null, new Rect(plotLeft, plotTop, plotRight - plotLeft, Math.Max(0, y - plotTop)));
            context.DrawLine(AlarmPen, new Point(plotLeft, y), new Point(plotRight, y));
        }

        if (low is { } l && double.IsFinite(l))
        {
            double y = Math.Max(plotTop, Math.Min(plotBottom, yOf(l)));
            context.DrawRectangle(AlarmBandBrush, null, new Rect(plotLeft, y, plotRight - plotLeft, Math.Max(0, plotBottom - y)));
            context.DrawLine(AlarmPen, new Point(plotLeft, y), new Point(plotRight, y));
        }
    }

    /// <summary>
    /// Hover cursor: vertical crosshair follows the mouse; the dot marks the nearest real
    /// sample and the horizontal hairline lets the value be read against the Y axis. The
    /// readout box (tag name for groups, value + timestamp) is kept under the mouse.
    /// </summary>
    private void DrawHoverReadout(
        DrawingContext context,
        IReadOnlyList<PlottedSeries> plots,
        Point cursor,
        Func<DateTime, double> xOf,
        double plotLeft,
        double plotTop,
        double plotRight,
        double plotBottom,
        double width,
        double height,
        Typeface typeface)
    {
        // Nearest sample across all series (snap to real samples, not stepped points).
        TrendSample? bestSample = null;
        double bestDistance = double.MaxValue;
        double bestX = 0;
        PlottedSeries? bestPlot = null;
        foreach (PlottedSeries plot in plots)
        {
            for (int i = 0; i < plot.Used.Length; i++)
            {
                double x = xOf(plot.Used[i].T);
                double distance = Math.Abs(x - cursor.X);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestSample = plot.Used[i];
                    bestX = x;
                    bestPlot = plot;
                }
            }
        }

        if (bestSample is not { } sample || bestPlot is not { } hit)
        {
            return;
        }

        Point snapped = new(bestX, hit.YOf(sample.V));

        // Vertical crosshair follows the mouse; the dot marks the nearest real sample
        // and the horizontal hairline lets the value be read against the Y axis.
        var crosshairPen = new Pen(CrosshairBrush, 1);
        context.DrawLine(crosshairPen, new Point(cursor.X, plotTop), new Point(cursor.X, plotBottom));
        context.DrawLine(crosshairPen, new Point(plotLeft, snapped.Y), new Point(plotRight, snapped.Y));
        context.DrawEllipse(DotFillBrush, new Pen(BrushFor(hit.Series.Color), 1.6), snapped, 3.4, 3.4);

        // Readout box (tag name for groups, value + timestamp) kept under the mouse,
        // next to the crosshair.
        string unit = hit.Series.Unit ?? string.Empty;
        string valueText = AppendUnit(FormatCursorValue(sample.V), unit);
        string timeText = sample.T.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        bool showName = plots.Count > 1 && !string.IsNullOrWhiteSpace(hit.Series.Name);
        string nameText = showName ? hit.Series.Name! : string.Empty;

        var valueTypeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var valueLayout = new FormattedText(
            valueText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            valueTypeface,
            FontSize,
            BrushFor(hit.Series.Color));
        var timeLayout = new FormattedText(
            timeText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            AxisLabelBrush);
        FormattedText? nameLayout = null;
        if (showName)
        {
            nameLayout = new FormattedText(
                nameText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                DotFillBrush);
        }

        const double padX = 8;
        const double padTop = 5;
        double boxWidth = Math.Max(valueLayout.Width, timeLayout.Width) + padX * 2;
        if (nameLayout is not null)
        {
            boxWidth = Math.Max(boxWidth, nameLayout.Width + padX * 2);
        }

        double boxHeight = valueLayout.Height + timeLayout.Height + padTop * 2 + 2;
        if (nameLayout is not null)
        {
            boxHeight += nameLayout.Height + 2;
        }

        double boxX = cursor.X + 12;
        if (boxX + boxWidth > width - 4)
        {
            boxX = cursor.X - 12 - boxWidth;
        }

        boxX = Math.Max(4, boxX);
        double boxY = Math.Max(4, Math.Min(cursor.Y - boxHeight / 2, height - boxHeight - 4));
        var boxRect = new Rect(boxX, boxY, boxWidth, boxHeight);
        context.DrawRectangle(ReadoutBgBrush, new Pen(FrameBrush, 1), new RoundedRect(boxRect, 4, 4));
        double lineY = boxY + padTop;
        if (nameLayout is not null)
        {
            context.DrawText(nameLayout, new Point(boxX + padX, lineY));
            lineY += nameLayout.Height + 2;
        }

        context.DrawText(valueLayout, new Point(boxX + padX, lineY));
        lineY += valueLayout.Height + 2;
        context.DrawText(timeLayout, new Point(boxX + padX, lineY));
    }

    /// <summary>
    /// Interactive legend drawn inside the chart when several series are plotted: one row
    /// per tag with its color swatch, name, current value and (when space allows) a
    /// min/max/avg stats line. Clicking a swatch cycles the trace color; clicking the row
    /// toggles the trace on/off (hidden rows are dimmed).
    /// </summary>
    private void DrawSeriesLegend(
        DrawingContext context,
        IReadOnlyList<TrendSeries> series,
        DateTime from,
        DateTime to,
        double plotLeft,
        double plotTop,
        double width,
        double height,
        Typeface typeface)
    {
        var rows = new List<LegendRow>(series.Count);
        foreach (TrendSeries item in series)
        {
            TrendSample[] used = UsedInWindow(item.Samples, from, to);
            string name = string.IsNullOrWhiteSpace(item.Name) ? "Tag" : item.Name!;
            string unit = item.Unit ?? string.Empty;
            string value = used.Length == 0 ? "—" : AppendUnit(FormatCursorValue(used[used.Length - 1].V), unit);
            string? stats = null;
            if (used.Length > 0)
            {
                double min = used.Min(s => s.V);
                double max = used.Max(s => s.V);
                double avg = used.Average(s => s.V);
                stats = $"min {FormatCursorValue(min)} · max {FormatCursorValue(max)} · avg {FormatCursorValue(avg)}";
            }

            rows.Add(new LegendRow(item, name, value, stats, item.Visible));
        }

        if (rows.Count == 0)
        {
            return;
        }

        var valueTypeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var statsTypeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
        var nameLayouts = new FormattedText[rows.Count];
        var valueLayouts = new FormattedText[rows.Count];
        FormattedText?[] statsLayouts = new FormattedText?[rows.Count];
        double maxNameWidth = 0;
        double maxValueWidth = 0;
        double maxStatsWidth = 0;
        double baseRowHeight = 0;
        double statsExtra = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            LegendRow row = rows[i];
            IBrush nameBrush = row.Visible ? DotFillBrush : DimBrushFor(row.Series.Color);
            IBrush valueBrush = row.Visible ? BrushFor(row.Series.Color) : DimBrushFor(row.Series.Color);
            nameLayouts[i] = new FormattedText(
                row.Name,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                nameBrush);
            valueLayouts[i] = new FormattedText(
                row.Value,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                valueTypeface,
                FontSize,
                valueBrush);
            if (row.Stats is { } statsText)
            {
                statsLayouts[i] = new FormattedText(
                    statsText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    statsTypeface,
                    FontSize - 1,
                    AxisLabelBrush);
                maxStatsWidth = Math.Max(maxStatsWidth, statsLayouts[i]!.Width);
                statsExtra = Math.Max(statsExtra, statsLayouts[i]!.Height + 1);
            }

            maxNameWidth = Math.Max(maxNameWidth, nameLayouts[i].Width);
            maxValueWidth = Math.Max(maxValueWidth, valueLayouts[i].Width);
            baseRowHeight = Math.Max(baseRowHeight, Math.Max(nameLayouts[i].Height, valueLayouts[i].Height));
        }

        const double padX = 8;
        const double padTop = 6;
        const double padBottom = 6;
        const double dotGap = 8;
        const double nameValueGap = 10;
        const double rowGap = 2;
        const double dotSize = 9;

        double statsWidth = padX * 2 + maxStatsWidth;
        double boxWidth = Math.Max(
            padX * 2 + dotSize + dotGap + maxNameWidth + nameValueGap + maxValueWidth,
            statsWidth);

        // Prefer two-line rows (name+value, then min/max/avg); fall back to single-line
        // rows when the chart is too short, and give up entirely if nothing fits.
        double twoLineHeight = padTop + padBottom + rowGap * (rows.Count - 1)
            + rows.Count * (baseRowHeight + statsExtra);
        double singleLineHeight = padTop + padBottom + rowGap * (rows.Count - 1)
            + rows.Count * baseRowHeight;
        bool withStats = twoLineHeight <= height - 8 && boxWidth <= width - 8;
        if (!withStats && singleLineHeight > height - 8)
        {
            legendHitRects_.Clear();
            return;
        }

        double boxHeight = withStats ? twoLineHeight : singleLineHeight;
        double boxX = Math.Min(plotLeft + 8, Math.Max(4, width - boxWidth - 4));
        double boxY = Math.Min(plotTop + 8, Math.Max(4, height - boxHeight - 4));

        var boxRect = new Rect(boxX, boxY, boxWidth, boxHeight);
        context.DrawRectangle(ReadoutBgBrush, new Pen(FrameBrush, 1), new RoundedRect(boxRect, 4, 4));
        double rowY = boxY + padTop;
        double dotX = boxX + padX;
        double nameX = dotX + dotSize + dotGap;
        double valueX = nameX + maxNameWidth + nameValueGap;
        for (int i = 0; i < rows.Count; i++)
        {
            LegendRow row = rows[i];
            double rowHeight = withStats ? baseRowHeight + statsExtra : baseRowHeight;
            double swatchY = rowY + (rowHeight - dotSize) / 2;
            if (row.Visible)
            {
                context.DrawEllipse(BrushFor(row.Series.Color), null, new Point(dotX + dotSize / 2, swatchY + dotSize / 2), dotSize / 2, dotSize / 2);
            }
            else
            {
                context.DrawEllipse(null, new Pen(DimBrushFor(row.Series.Color), 1), new Rect(dotX, swatchY, dotSize, dotSize));
            }

            context.DrawText(nameLayouts[i], new Point(nameX, rowY));
            context.DrawText(valueLayouts[i], new Point(valueX, rowY));
            if (withStats && statsLayouts[i] is { } statsLayout)
            {
                context.DrawText(statsLayout, new Point(nameX, rowY + nameLayouts[i].Height + 1));
            }

            var swatchRect = new Rect(dotX - 3, swatchY - 3, dotSize + 6, dotSize + 6);
            var rowRect = new Rect(boxX, rowY, boxWidth, rowHeight);
            legendHitRects_.Add(new LegendHitRect(swatchRect, rowRect, row.Series.Name ?? string.Empty));
            rowY += rowHeight + rowGap;
        }
    }

    private readonly record struct LegendRow(TrendSeries Series, string Name, string Value, string? Stats, bool Visible);

    private static TrendSample[] UsedInWindow(IReadOnlyList<TrendSample>? samples, DateTime from, DateTime to)
    {
        if (samples is null)
        {
            return Array.Empty<TrendSample>();
        }

        return samples
            .Where(s => s.T >= from && s.T <= to && double.IsFinite(s.V))
            .ToArray();
    }

    /// <summary>
    /// Always-visible readout pinned in the chart corner showing the data min, max,
    /// average, delta and sample count over the displayed window (single-series charts only).
    /// </summary>
    private static void DrawStatsReadout(
        DrawingContext context,
        IReadOnlyList<TrendSample> samples,
        string unit,
        double plotLeft,
        double plotTop,
        double width,
        double height,
        Typeface typeface)
    {
        if (samples.Count == 0)
        {
            return;
        }

        double min = double.MaxValue;
        double max = double.MinValue;
        double sum = 0;
        int count = 0;
        foreach (TrendSample sample in samples)
        {
            if (!double.IsFinite(sample.V))
            {
                continue;
            }

            min = Math.Min(min, sample.V);
            max = Math.Max(max, sample.V);
            sum += sample.V;
            count++;
        }

        if (count == 0)
        {
            return;
        }

        double avg = sum / count;
        string[] labels = { "Max", "Min", "Avg", "Δ", "Pts" };
        string[] values =
        {
            AppendUnit(FormatCursorValue(max), unit),
            AppendUnit(FormatCursorValue(min), unit),
            AppendUnit(FormatCursorValue(avg), unit),
            AppendUnit(FormatCursorValue(max - min), unit),
            count.ToString("N0", CultureInfo.InvariantCulture)
        };

        var valueTypeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var labelLayouts = new FormattedText[labels.Length];
        var valueLayouts = new FormattedText[values.Length];
        double[] rowHeights = new double[labels.Length];
        double maxLabelWidth = 0;
        double maxValueWidth = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            labelLayouts[i] = new FormattedText(
                labels[i],
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                AxisLabelBrush);
            valueLayouts[i] = new FormattedText(
                values[i],
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                valueTypeface,
                FontSize,
                DotFillBrush);
            maxLabelWidth = Math.Max(maxLabelWidth, labelLayouts[i].Width);
            maxValueWidth = Math.Max(maxValueWidth, valueLayouts[i].Width);
            rowHeights[i] = Math.Max(labelLayouts[i].Height, valueLayouts[i].Height);
        }

        const double padX = 8;
        const double padTop = 6;
        const double padBottom = 6;
        const double valueGap = 8;
        const double rowGap = 1;

        double boxWidth = padX * 2 + maxLabelWidth + valueGap + maxValueWidth;
        double boxHeight = padTop + padBottom + rowGap * (labels.Length - 1);
        for (int i = 0; i < rowHeights.Length; i++)
        {
            boxHeight += rowHeights[i];
        }

        if (boxWidth > width - 8 || boxHeight > height - 8)
        {
            return;
        }

        double boxX = Math.Min(plotLeft + 8, Math.Max(4, width - boxWidth - 4));
        double boxY = Math.Min(plotTop + 8, Math.Max(4, height - boxHeight - 4));

        var boxRect = new Rect(boxX, boxY, boxWidth, boxHeight);
        context.DrawRectangle(ReadoutBgBrush, new Pen(FrameBrush, 1), new RoundedRect(boxRect, 4, 4));
        double rowY = boxY + padTop;
        double valueX = boxX + padX + maxLabelWidth + valueGap;
        for (int i = 0; i < labels.Length; i++)
        {
            context.DrawText(labelLayouts[i], new Point(boxX + padX, rowY));
            context.DrawText(valueLayouts[i], new Point(valueX, rowY));
            rowY += rowHeights[i] + rowGap;
        }
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

    private static string AppendUnit(string value, string unit) =>
        string.IsNullOrWhiteSpace(unit) ? value : value + " " + unit.Trim();

    private static string FormatCursorValue(double value)
    {
        double rounded = Math.Round(value, 6);
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

        return rounded.ToString("0.######", CultureInfo.InvariantCulture)
            .TrimEnd('0')
            .TrimEnd('.');
    }

    private static string FormatTime(DateTime time)
    {
        // Mark midnight so a 24h trace that crosses a day boundary stays readable.
        if (time.Hour == 0 && time.Minute == 0)
        {
            return time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}