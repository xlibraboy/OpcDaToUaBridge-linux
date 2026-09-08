using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Shared shell for trend windows: the time range (15m/1h/8h/24h), drag time zoom,
/// timeline panning, live play/pause, CSV export and alarm limit overlays. Subclasses
/// load the actual samples for the requested window (<see cref="ReloadDataAsync"/>).
/// A single-tag trend and a multi-tag group trend both derive from this, so
/// <see cref="Views.TrendWindow"/> hosts either.
/// </summary>
public abstract partial class TrendWindowViewModelBase : ObservableObject, IAsyncDisposable
{
    private DateTime? zoomFromUtc_;
    private DateTime? zoomToUtc_;
    private CancellationTokenSource? cts_;
    private readonly PeriodicTimer? refreshTimer_;
    private readonly Task? refreshLoop_;
    private bool disposed_;

    protected TrendWindowViewModelBase()
    {
        refreshTimer_ = new PeriodicTimer(TimeSpan.FromSeconds(30));
        refreshLoop_ = RefreshLoopAsync();
    }

    [ObservableProperty]
    private string _title = "Trend";

    [ObservableProperty]
    private string _rangeLabel = "1h";

    /// <summary>Fixed period choices for the toolbar dropdown; "Custom" is a display state while zoomed.</summary>
    public string[] RangeChoices { get; } = { "15m", "1h", "8h", "24h", "Custom" };

    /// <summary>
    /// Period dropdown selection: the base range label, or "Custom" while a zoomed/
    /// custom time window is active. Picking a period applies it and drops the zoom.
    /// </summary>
    public string RangeSelection
    {
        get => IsZoomed ? "Custom" : RangeLabel;
        set
        {
            if (value == "Custom" || (value == RangeLabel && !IsZoomed))
            {
                return;
            }

            _ = ApplyRangeAsync(value);
        }
    }

    partial void OnRangeLabelChanged(string value) => OnPropertyChanged(nameof(RangeSelection));

    /// <summary>Applies a period from the dropdown: resets any zoom, then reloads.</summary>
    private async Task ApplyRangeAsync(string label)
    {
        RangeLabel = TrendRange.Normalize(TrendRange.ParseHours(label));
        if (IsZoomed)
        {
            zoomFromUtc_ = null;
            zoomToUtc_ = null;
            IsZoomed = false;
        }

        await ReloadAsync(RangeLabel).ConfigureAwait(true);
    }

    [ObservableProperty]
    private string _intervalLabel = "Auto";

    /// <summary>Sampling-interval choices for the toolbar dropdown (Auto = up to 1000 points per pen).</summary>
    public string[] IntervalChoices { get; } = { "Auto", "1s", "5s", "10s", "30s", "1m" };

    /// <summary>Selected sampling interval; null = Auto (bridge default point budget).</summary>
    public TimeSpan? TrendInterval => IntervalLabel switch
    {
        "1s" => TimeSpan.FromSeconds(1),
        "5s" => TimeSpan.FromSeconds(5),
        "10s" => TimeSpan.FromSeconds(10),
        "30s" => TimeSpan.FromSeconds(30),
        "1m" => TimeSpan.FromMinutes(1),
        _ => null
    };

    partial void OnIntervalLabelChanged(string value) => _ = ReloadAsync();

    /// <summary>
    /// Point budget for the window: one sample per interval (e.g. 1h at 30s = 120),
    /// clamped to the bridge's 10..2000 limit; Auto keeps the 1000-point default.
    /// </summary>
    protected static int MaxPointsFor(TimeSpan span, TimeSpan? interval)
    {
        if (interval is not { } step || step <= TimeSpan.Zero || span <= TimeSpan.Zero)
        {
            return 1000;
        }

        double points = span.Ticks / (double)step.Ticks;
        return (int)Math.Clamp(Math.Round(points), 10, 2000);
    }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Start of the displayed window (UTC).</summary>
    [ObservableProperty]
    private DateTime _fromUtc = DateTime.UtcNow.AddHours(-1);

    /// <summary>End of the displayed window (UTC).</summary>
    [ObservableProperty]
    private DateTime _toUtc = DateTime.UtcNow;

    /// <summary>
    /// Fit the Y axis to the data when true; pin it to the tag's data-type range when
    /// false. Disabled (and effectively true) for floating-point tags and group trends.
    /// </summary>
    [ObservableProperty]
    private bool _autoRange = true;

    /// <summary>True when the trend can pin the Y axis to a fixed data-type range.</summary>
    [ObservableProperty]
    private bool _hasFixedRange;

    partial void OnAutoRangeChanged(bool value) => OnPropertyChanged(nameof(Series));

    /// <summary>UTC timestamp of the pinned blue time cursor; null = no pin.</summary>
    [ObservableProperty]
    private DateTime? _pinnedAtUtc;

    /// <summary>True while a pin is set (enables the "Clear pin" button).</summary>
    public bool HasPin => PinnedAtUtc is not null;

    partial void OnPinnedAtUtcChanged(DateTime? value) => OnPropertyChanged(nameof(HasPin));

    /// <summary>Clears the pinned time cursor.</summary>
    [RelayCommand]
    private void ClearPin()
    {
        PinnedAtUtc = null;
    }

    /// <summary>True while a drag time-range zoom is active instead of the base range.</summary>
    [ObservableProperty]
    private bool _isZoomed;

    partial void OnIsZoomedChanged(bool value) => OnPropertyChanged(nameof(RangeSelection));

    /// <summary>True when the live auto-refresh is frozen (stream paused).</summary>
    [ObservableProperty]
    private bool _isPaused;

    public bool IsLive => !IsPaused;

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(PauseLabel));
    }

    /// <summary>
    /// How the Y axis maps multiple series: "Shared" (one scale for all analog tags,
    /// the default) or "Percent" (each tag normalized to its own 0..100 band so mixed
    /// engineering units stay readable). Only group trends can switch.
    /// </summary>
    [ObservableProperty]
    private string _yAxisMode = "Shared";

    public bool IsSharedAxis => YAxisMode == "Shared";

    public bool IsPercentAxis => YAxisMode == "Percent";

    partial void OnYAxisModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSharedAxis));
        OnPropertyChanged(nameof(IsPercentAxis));
        // The chart maps every pen to its own scale (shared mode) or a 0..100 band
        // (percent mode); the series list just needs to re-read the mode.
        OnPropertyChanged(nameof(Series));
    }

    /// <summary>
    /// How multiple pens are laid out: "Stacked" (one strip per pen, each with its own
    /// Y axis) or "Mixed" (all pens overlaid on one plot, with a color-coded legend of
    /// each pen's min-max scale). Single-tag trends always stay stacked; group trends
    /// default to mixed and can switch.
    /// </summary>
    [ObservableProperty]
    private string _layoutMode = "Stacked";

    public bool IsMixedLayout => LayoutMode == "Mixed";

    partial void OnLayoutModeChanged(string value) => OnPropertyChanged(nameof(IsMixedLayout));

    /// <summary>True when this trend type offers the mixed/stacked layout choice (groups only).</summary>
    public virtual bool SupportsLayoutToggle => false;

    /// <summary>Switches between the mixed overlay and the stacked strip layout.</summary>
    [RelayCommand]
    private void SetLayoutMode(string mode)
    {
        if (SupportsLayoutToggle && mode is "Mixed" or "Stacked")
        {
            LayoutMode = mode;
        }
    }

    /// <summary>High alarm limit; when set, the chart shades everything above it.</summary>
    [ObservableProperty]
    private double? _alarmHigh;

    /// <summary>Low alarm limit; when set, the chart shades everything below it.</summary>
    [ObservableProperty]
    private double? _alarmLow;

    // Alarm edits change the single-tag pen's AlarmLimits, so re-publish the series.
    partial void OnAlarmHighChanged(double? value) => OnPropertyChanged(nameof(Series));

    partial void OnAlarmLowChanged(double? value) => OnPropertyChanged(nameof(Series));

    /// <summary>Editable high-limit text; parsed into <see cref="AlarmHigh"/> (single-tag trends).</summary>
    [ObservableProperty]
    private string _alarmHighText = string.Empty;

    /// <summary>Editable low-limit text; parsed into <see cref="AlarmLow"/> (single-tag trends).</summary>
    [ObservableProperty]
    private string _alarmLowText = string.Empty;

    partial void OnAlarmHighTextChanged(string value) => AlarmHigh = TryParseLimit(value);

    partial void OnAlarmLowTextChanged(string value) => AlarmLow = TryParseLimit(value);

    private static double? TryParseLimit(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    /// <summary>True when this trend type can configure alarm limit overlays (single tags only).</summary>
    public virtual bool SupportsAlarmLimits => false;

    /// <summary>Pen rows for the pen configuration table (name, color, value, min/max/avg).</summary>
    public abstract IReadOnlyList<TrendPenViewModel> Pens { get; }

    /// <summary>True when any pen has a typed custom Y-axis range (shows the reset button).</summary>
    public bool HasCustomRanges => Pens.Any(p => p.HasCustomAxis);

    /// <summary>Resets every pen's Y axis back to auto-fit (clears all typed ranges).</summary>
    [RelayCommand]
    private void ClearCustomRanges()
    {
        foreach (TrendPenViewModel pen in Pens)
        {
            pen.ClearCustomRange();
        }

        OnPropertyChanged(nameof(HasCustomRanges));
    }

    /// <summary>True when this trend type offers the shared/percent Y-axis choice (groups only).</summary>
    public virtual bool SupportsPercentAxis => false;

    /// <summary>Pens the chart should draw; one entry per strip.</summary>
    public abstract IReadOnlyList<TrendSeries> Series { get; }

    /// <summary>Bridge id shown in the window hints; empty for group trends.</summary>
    public virtual string BridgeId => string.Empty;

    /// <summary>OPC item id shown in the window hints; empty for group trends.</summary>
    public virtual string DaItemId => string.Empty;

    /// <summary>True when this trend belongs to a single tag with a bridge id to show.</summary>
    public bool HasBridgeHint => !string.IsNullOrWhiteSpace(BridgeId);

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync().ConfigureAwait(true);

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    /// <summary>Pans the timeline back by half the visible span.</summary>
    [RelayCommand]
    private async Task PanBackAsync() => await PanByFractionAsync(-0.5).ConfigureAwait(true);

    /// <summary>Pans the timeline forward by half the visible span.</summary>
    [RelayCommand]
    private async Task PanForwardAsync() => await PanByFractionAsync(0.5).ConfigureAwait(true);

    [RelayCommand]
    private void SetAxisMode(string mode)
    {
        if (SupportsPercentAxis && mode is "Shared" or "Percent")
        {
            YAxisMode = mode;
        }
    }

    private async Task PanByFractionAsync(double fraction)
    {
        TimeSpan span = ToUtc - FromUtc;
        if (span <= TimeSpan.Zero)
        {
            return;
        }

        long shiftTicks = (long)(span.Ticks * Math.Abs(fraction));
        TimeSpan shift = fraction < 0 ? -TimeSpan.FromTicks(shiftTicks) : TimeSpan.FromTicks(shiftTicks);
        zoomFromUtc_ = FromUtc + shift;
        zoomToUtc_ = ToUtc + shift;
        IsZoomed = true;
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Returns to the base time range (15m/1h/8h/24h).</summary>
    [RelayCommand]
    private async Task ResetZoomAsync()
    {
        if (!IsZoomed)
        {
            return;
        }

        zoomFromUtc_ = null;
        zoomToUtc_ = null;
        IsZoomed = false;
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Zooms the trend to a fixed UTC window, requested by a right-drag or the time-range picker.</summary>
    public async Task ZoomToAsync(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc || toUtc - fromUtc < TimeSpan.FromSeconds(1))
        {
            return;
        }

        zoomFromUtc_ = fromUtc;
        zoomToUtc_ = toUtc;
        IsZoomed = true;
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>CSV text of the visible series only (hidden traces are excluded).</summary>
    public string BuildCsv() => TrendCsv.Build(VisibleSeries());

    /// <summary>Writes the visible samples (hidden traces excluded) to a CSV file.</summary>
    public async Task<string?> ExportCsvAsync(string path)
    {
        try
        {
            TrendSeries[] visible = VisibleSeries();
            if (visible.Length == 0)
            {
                StatusMessage = "Nothing to export — all traces are hidden";
                return null;
            }

            await File.WriteAllTextAsync(path, TrendCsv.Build(visible)).ConfigureAwait(true);
            int rows = visible.Sum(s => s.Samples?.Count ?? 0);
            string message = $"Exported {rows} rows to {Path.GetFileName(path)}";
            StatusMessage = message;
            return message;
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
            return null;
        }
    }

    private TrendSeries[] VisibleSeries() => Series.Where(s => s.Visible).ToArray();

    private async Task RefreshLoopAsync()
    {
        if (refreshTimer_ is null)
        {
            return;
        }

        try
        {
            while (await refreshTimer_.WaitForNextTickAsync().ConfigureAwait(true))
            {
                if (IsPaused)
                {
                    continue;
                }

                await ReloadAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Subclasses load their samples for the window and update their own state.</summary>
    protected abstract Task ReloadDataAsync(DateTime from, DateTime to, int maxPoints, CancellationToken ct);

    protected async Task ReloadAsync(string? rangeLabel = null)
    {
        double rangeHours = TrendRange.ParseHours(rangeLabel ?? RangeLabel);
        DateTime to = DateTime.UtcNow;
        DateTime from = to.AddHours(-rangeHours);
        if (zoomFromUtc_ is { } zoomFrom && zoomToUtc_ is { } zoomTo && zoomTo > zoomFrom)
        {
            from = zoomFrom;
            to = zoomTo;
        }

        cts_?.Cancel();
        cts_?.Dispose();
        cts_ = new CancellationTokenSource();
        CancellationToken ct = cts_.Token;
        IsLoading = true;
        try
        {
            await ReloadDataAsync(from, to, MaxPointsFor(to - from, TrendInterval), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = "Trend error: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected static string FormatDuration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return "0s";
        }

        if (span.TotalMinutes < 1)
        {
            return $"{(int)Math.Ceiling(span.TotalSeconds)}s";
        }

        if (span.TotalHours >= 1 && span.TotalMinutes % 60 == 0)
        {
            return $"{(int)span.TotalHours}h";
        }

        return $"{(int)Math.Ceiling(span.TotalMinutes)}m";
    }

    public virtual async ValueTask DisposeAsync()
    {
        // The trend window disposes its view model when the window closes, and callers may
        // dispose again during teardown — make disposal idempotent instead of throwing on
        // a double Cancel/Dispose of the CancellationTokenSource.
        if (disposed_)
        {
            return;
        }

        disposed_ = true;
        cts_?.Cancel();
        cts_?.Dispose();
        cts_ = null;
        refreshTimer_?.Dispose();
        if (refreshLoop_ is not null)
        {
            try { await refreshLoop_.ConfigureAwait(false); } catch { }
        }
    }
}