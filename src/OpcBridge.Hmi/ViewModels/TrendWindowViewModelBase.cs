using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Shared shell for trend windows: the time range (15m/1h/8h/24h), right-drag time zoom,
/// timeline panning, live play/pause, CSV export, the shared Y-axis plumbing and alarm
/// limit overlays. Subclasses load the actual samples for the requested window
/// (<see cref="ReloadDataAsync"/>) and compute the chart axis (<see cref="RecomputeAxis"/>).
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

    public bool IsRange15m => RangeLabel == "15m";

    public bool IsRange1h => RangeLabel == "1h";

    public bool IsRange8h => RangeLabel == "8h";

    public bool IsRange24h => RangeLabel == "24h";

    partial void OnRangeLabelChanged(string value)
    {
        OnPropertyChanged(nameof(IsRange15m));
        OnPropertyChanged(nameof(IsRange1h));
        OnPropertyChanged(nameof(IsRange8h));
        OnPropertyChanged(nameof(IsRange24h));
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

    [ObservableProperty]
    private double _axisMin;

    [ObservableProperty]
    private double _axisMax = 1;

    [ObservableProperty]
    private double _axisStep = 0.2;

    partial void OnAutoRangeChanged(bool value) => RecomputeAxis();

    /// <summary>True while a right-drag time-range zoom is active instead of the base range.</summary>
    [ObservableProperty]
    private bool _isZoomed;

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
        if (value == "Percent")
        {
            // Percentage axis is always a fixed 0..100 band.
            AxisMin = 0;
            AxisMax = 100;
            AxisStep = 20;
        }
        else
        {
            RecomputeAxis();
        }
    }

    /// <summary>High alarm limit; when set, the chart shades everything above it.</summary>
    [ObservableProperty]
    private double? _alarmHigh;

    /// <summary>Low alarm limit; when set, the chart shades everything below it.</summary>
    [ObservableProperty]
    private double? _alarmLow;

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

    /// <summary>True when this trend type offers the shared/percent Y-axis choice (groups only).</summary>
    public virtual bool SupportsPercentAxis => false;

    /// <summary>Series the chart should draw; one entry per trace.</summary>
    public abstract IReadOnlyList<TrendSeries> Series { get; }

    /// <summary>Bridge id shown in the window hints; empty for group trends.</summary>
    public virtual string BridgeId => string.Empty;

    /// <summary>OPC item id shown in the window hints; empty for group trends.</summary>
    public virtual string DaItemId => string.Empty;

    /// <summary>True when this trend belongs to a single tag with a bridge id to show.</summary>
    public bool HasBridgeHint => !string.IsNullOrWhiteSpace(BridgeId);

    [RelayCommand]
    private async Task SetRangeAsync(string label)
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
    protected abstract Task ReloadDataAsync(DateTime from, DateTime to, CancellationToken ct);

    /// <summary>Subclasses recompute the shared Y axis from their loaded samples.</summary>
    protected abstract void RecomputeAxis();

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
            await ReloadDataAsync(from, to, ct).ConfigureAwait(true);
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