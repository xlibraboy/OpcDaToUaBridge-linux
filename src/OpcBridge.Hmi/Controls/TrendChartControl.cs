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

/// <summary>
/// Standard SCADA-style trend: numeric Y axis with min/max gridline labels on the left,
/// clock-aligned time axis along the bottom, and the value trace drawn across the plot.
/// Values, window and axis layout come from the view model, so the control stays a dumb
/// renderer. Right-drag (when <see cref="EnableRangeZoom"/> is set) selects a time range
/// that the host can load via <see cref="ZoomRequested"/>.
/// </summary>
public sealed class TrendChartControl : Control
{
    public static readonly StyledProperty<IEnumerable<TrendSample>?> SamplesProperty =
        AvaloniaProperty.Register<TrendChartControl, IEnumerable<TrendSample>?>(nameof(Samples));

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

    public IEnumerable<TrendSample>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
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

    /// <summary>Raised when the operator right-drags a time range on the plot.</summary>
    public event EventHandler<TrendZoomRequestedEventArgs>? ZoomRequested;

    /// <summary>Raised when the operator double-clicks the plot (request to zoom back out).</summary>
    public event EventHandler? ZoomResetRequested;

    static TrendChartControl()
    {
        AffectsRender<TrendChartControl>(
            SamplesProperty,
            FromUtcProperty,
            ToUtcProperty,
            YMinProperty,
            YMaxProperty,
            YStepProperty,
            UnitProperty);
    }

    public TrendChartControl()
    {
        // Painting an opaque background makes the whole chart a hit region so pointer
        // events (hover cursor, right-drag zoom) are handled right here in the control.
        Cursor = new Cursor(StandardCursorType.Cross);
        AddHandler(Gestures.DoubleTappedEvent, OnChartDoubleTapped);
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
        if (!EnableRangeZoom || !e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (layoutPlotWidth_ <= 0 || layoutToUtc_ <= layoutFromUtc_)
        {
            return;
        }

        Point position = e.GetPosition(this);
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
    private static readonly SolidColorBrush TraceBrush = new(Color.Parse("#4FC3F7"));
    private static readonly SolidColorBrush TraceFillBrush = new(Color.FromArgb(0x26, 0x4F, 0xC3, 0xF7));
    private static readonly SolidColorBrush AxisLabelBrush = new(Color.Parse("#B4B4BE"));
    private static readonly SolidColorBrush GridBrush = new(Color.Parse("#3A3A44"));
    private static readonly SolidColorBrush FrameBrush = new(Color.Parse("#555560"));
    private static readonly SolidColorBrush CrosshairBrush = new(Color.Parse("#7A7A86"));
    private static readonly SolidColorBrush DotFillBrush = new(Color.Parse("#FFFFFF"));
    private static readonly SolidColorBrush ReadoutBgBrush = new(Color.FromArgb(0xEC, 0x23, 0x23, 0x29));
    private static readonly SolidColorBrush ZoomFillBrush = new(Color.FromArgb(0x40, 0x4F, 0xC3, 0xF7));

    private const double FontSize = 11;
    private const double TopPad = 8;
    private const double RightPad = 10;
    private const double BottomPad = 24;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
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

        TrendSample[] samples = Samples is null
            ? Array.Empty<TrendSample>()
            : Samples as TrendSample[] ?? Samples.ToArray();
        if (samples.Length < 2)
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
        double plotBottom = height - BottomPad;
        double plotWidth = plotRight - plotLeft;
        double plotHeight = plotBottom - plotTop;
        if (plotWidth < 40 || plotHeight < 20)
        {
            return;
        }

        double valueSpan = yMax - yMin;
        double yOf(double value)
        {
            double clamped = Math.Max(yMin, Math.Min(yMax, value));
            return plotBottom - ((clamped - yMin) / valueSpan) * plotHeight;
        }

        double xOf(DateTime time)
        {
            double ratio = Math.Max(0.0, Math.Min(1.0, (time - from).Ticks / (double)totalTicks));
            return plotLeft + ratio * plotWidth;
        }

        // ---- trace points (only samples inside the window) ----
        var used = new List<TrendSample>(samples.Length);
        var trace = new List<Point>(samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            TrendSample sample = samples[i];
            if (sample.T < from || sample.T > to || !double.IsFinite(sample.V))
            {
                continue;
            }

            used.Add(sample);
            trace.Add(new Point(xOf(sample.T), yOf(sample.V)));
        }

        // ---- horizontal gridlines + labels ----
        var gridPen = new Pen(GridBrush, 1);
        for (int i = 0; i <= gridCount; i++)
        {
            double y = yOf(i == gridCount ? yMax : yMin + i * yStep);
            context.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotRight, y));
            FormattedText label = labelLayouts[i];
            double textY = y - label.Height / 2;
            textY = Math.Max(0, Math.Min(height - label.Height, textY));
            context.DrawText(label, new Point(plotLeft - 8 - label.Width, textY));
        }

        // ---- vertical (time) gridlines + labels ----
        TimeSpan timeStep = TrendTimeAxis.StepFor(to - from);
        if (timeStep > TimeSpan.Zero)
        {
            for (DateTime tick = TrendTimeAxis.Floor(from, timeStep); tick <= to; tick += timeStep)
            {
                if (tick < from)
                {
                    continue;
                }

                double x = xOf(tick);
                context.DrawLine(gridPen, new Point(x, plotTop), new Point(x, plotBottom));

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
                    context.DrawText(layout, new Point(labelLeft, plotBottom + 6));
                }
            }
        }

        // ---- value trace + area fill ----
        if (trace.Count >= 2)
        {
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

            var fillGeometry = new StreamGeometry();
            using (StreamGeometryContext ctx = fillGeometry.Open())
            {
                ctx.BeginFigure(new Point(trace[0].X, plotBottom), true);
                ctx.LineTo(trace[0]);
                for (int i = 1; i < trace.Count; i++)
                {
                    ctx.LineTo(trace[i]);
                }

                ctx.LineTo(new Point(trace[trace.Count - 1].X, plotBottom));
                ctx.EndFigure(true);
            }

            context.DrawGeometry(TraceFillBrush, null, fillGeometry);
            context.DrawGeometry(null, new Pen(TraceBrush, 1.6), lineGeometry);

            // Last sample marker (current value)
            Point last = trace[trace.Count - 1];
            context.DrawEllipse(TraceBrush, null, last, 2.6, 2.6);
        }

        // ---- pinned readout: data min / max / point count over the visible window ----
        DrawStatsReadout(context, used, plotLeft, plotTop, width, height, typeface, Unit ?? string.Empty);

        // ---- right-drag zoom selection band ----
        if (zoomDragging_)
        {
            double x0 = Math.Max(plotLeft, Math.Min(zoomAnchorX_, zoomCurrentX_));
            double x1 = Math.Min(plotRight, Math.Max(zoomAnchorX_, zoomCurrentX_));
            if (x1 > x0)
            {
                var band = new Rect(x0, plotTop, x1 - x0, plotHeight);
                context.DrawRectangle(ZoomFillBrush, new Pen(TraceBrush, 1), band);
            }
        }

        // ---- hover cursor: snap to the nearest sample and read out value + time ----
        if (!zoomDragging_
            && trace.Count >= 2
            && cursorPoint_ is { } cursor
            && cursor.X >= plotLeft
            && cursor.X <= plotRight
            && cursor.Y >= plotTop
            && cursor.Y <= plotBottom)
        {
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < trace.Count; i++)
            {
                double distance = Math.Abs(trace[i].X - cursor.X);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            Point snapped = trace[best];
            TrendSample hoverSample = used[best];

            // Vertical crosshair follows the mouse; the dot marks the nearest real sample
            // and the horizontal hairline lets the value be read against the Y axis.
            var crosshairPen = new Pen(CrosshairBrush, 1);
            context.DrawLine(crosshairPen, new Point(cursor.X, plotTop), new Point(cursor.X, plotBottom));
            context.DrawLine(crosshairPen, new Point(plotLeft, snapped.Y), new Point(plotRight, snapped.Y));
            context.DrawEllipse(DotFillBrush, new Pen(TraceBrush, 1.6), snapped, 3.4, 3.4);

            // Readout box (value + timestamp) kept under the mouse, next to the crosshair.
            string unit = Unit ?? string.Empty;
            string valueText = AppendUnit(FormatCursorValue(hoverSample.V), unit);
            string timeText = hoverSample.T.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var valueTypeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
            var valueLayout = new FormattedText(
                valueText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                valueTypeface,
                FontSize,
                TraceBrush);
            var timeLayout = new FormattedText(
                timeText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                AxisLabelBrush);

            const double padX = 8;
            const double padTop = 5;
            double boxWidth = Math.Max(valueLayout.Width, timeLayout.Width) + padX * 2;
            double boxHeight = valueLayout.Height + timeLayout.Height + padTop * 2 + 2;
            double boxX = cursor.X + 12;
            if (boxX + boxWidth > width - 4)
            {
                boxX = cursor.X - 12 - boxWidth;
            }

            boxX = Math.Max(4, boxX);
            double boxY = Math.Max(4, Math.Min(cursor.Y - boxHeight / 2, height - boxHeight - 4));
            var boxRect = new Rect(boxX, boxY, boxWidth, boxHeight);
            context.DrawRectangle(ReadoutBgBrush, new Pen(FrameBrush, 1), new RoundedRect(boxRect, 4, 4));
            context.DrawText(valueLayout, new Point(boxX + padX, boxY + padTop));
            context.DrawText(timeLayout, new Point(boxX + padX, boxY + padTop + valueLayout.Height + 2));
        }

        // ---- plot frame ----
        var framePen = new Pen(FrameBrush, 1);
        context.DrawRectangle(null, framePen, new Rect(plotLeft, plotTop, plotWidth, plotHeight));

        // Capture the geometry + window for the zoom mapping on pointer input.
        layoutPlotLeft_ = plotLeft;
        layoutPlotWidth_ = plotWidth;
        layoutFromUtc_ = from;
        layoutToUtc_ = to;
    }

    /// <summary>
    /// Always-visible readout pinned in the chart corner showing the real data min and max
    /// plus the sample count over the displayed window.
    /// </summary>
    private static void DrawStatsReadout(
        DrawingContext context,
        IReadOnlyList<TrendSample> samples,
        double plotLeft,
        double plotTop,
        double width,
        double height,
        Typeface typeface,
        string unit)
    {
        if (samples.Count == 0)
        {
            return;
        }

        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (TrendSample sample in samples)
        {
            if (!double.IsFinite(sample.V))
            {
                continue;
            }

            min = Math.Min(min, sample.V);
            max = Math.Max(max, sample.V);
        }

        if (min == double.MaxValue)
        {
            return;
        }

        string[] labels = { "Max", "Min", "Pts" };
        string[] values =
        {
            AppendUnit(FormatCursorValue(max), unit),
            AppendUnit(FormatCursorValue(min), unit),
            samples.Count.ToString("N0", CultureInfo.InvariantCulture)
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
