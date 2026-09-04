using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

public partial class TrendViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BridgeApiClient api_;
    private readonly bool ownsApi_;
    private readonly (double Min, double Max)? fixedRange_;
    private DateTime? zoomFromUtc_;
    private DateTime? zoomToUtc_;
    private CancellationTokenSource? cts_;
    private readonly PeriodicTimer? refreshTimer_;
    private readonly Task? refreshLoop_;

    public TrendViewModel(TagBindingKey key, BridgeApiClient api, bool ownsApi = false, string? dataType = null, string? unit = null, string? trendStyle = null)
    {
        Key = key;
        api_ = api;
        ownsApi_ = ownsApi;
        Title = $"{key.BridgeId} / {key.DaItemId}";
        BridgeId = key.BridgeId;
        SourceId = key.SourceId;
        DaItemId = key.DaItemId;
        DataType = dataType ?? "Double";
        Unit = unit ?? string.Empty;
        TrendStyle = NormalizeTrendStyle(trendStyle);
        // Booleans are always plotted on their natural 0..1 band so an on/off trace reads clearly.
        (double, double)? typeRange = DataTypeRanges.GetRange(DataType);
        fixedRange_ = IsBooleanLike(DataType) ? (0, 1) : typeRange;
        HasFixedRange = fixedRange_.HasValue;
        RecomputeAxis();
        _ = ReloadAsync();
        refreshTimer_ = new PeriodicTimer(TimeSpan.FromSeconds(30));
        refreshLoop_ = RefreshLoopAsync();
    }

    public TagBindingKey Key { get; }

    public string DataType { get; }

    /// <summary>Tag's engineering unit (e.g. "°C"), shown on the pinned readout and hover cursor.</summary>
    [ObservableProperty]
    private string _unit = string.Empty;

    /// <summary>
    /// How this tag's history renders: "Continuous" (line through the samples, default) or
    /// "Step" (sample-and-hold). Set per-tag in the dashboard Maps faceplate.
    /// </summary>
    [ObservableProperty]
    private string _trendStyle = "Continuous";

    private static string NormalizeTrendStyle(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value.Trim(), "Step", StringComparison.OrdinalIgnoreCase)
            ? "Step"
            : "Continuous";
    }

    [ObservableProperty]
    private string _title = "Trend";

    [ObservableProperty]
    private string _bridgeId = string.Empty;

    [ObservableProperty]
    private string _sourceId = string.Empty;

    [ObservableProperty]
    private string _daItemId = string.Empty;

    [ObservableProperty]
    private string _rangeLabel = "1h";

    public bool IsRange1h => RangeLabel == "1h";
    public bool IsRange8h => RangeLabel == "8h";
    public bool IsRange24h => RangeLabel == "24h";

    partial void OnRangeLabelChanged(string value)
    {
        OnPropertyChanged(nameof(IsRange1h));
        OnPropertyChanged(nameof(IsRange8h));
        OnPropertyChanged(nameof(IsRange24h));
    }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Numeric samples with timestamps, newest last.</summary>
    [ObservableProperty]
    private IReadOnlyList<TrendSample> _samples = Array.Empty<TrendSample>();

    /// <summary>Start of the displayed window (UTC).</summary>
    [ObservableProperty]
    private DateTime _fromUtc = DateTime.UtcNow.AddHours(-1);

    /// <summary>End of the displayed window (UTC).</summary>
    [ObservableProperty]
    private DateTime _toUtc = DateTime.UtcNow;

    /// <summary>
    /// Fit the Y axis to the data when true; pin it to the tag's data-type range when false.
    /// Disabled (and effectively true) for floating types, which have no natural range.
    /// </summary>
    [ObservableProperty]
    private bool _autoRange = true;

    /// <summary>True when the tag's data type has a natural min/max range to pin to.</summary>
    [ObservableProperty]
    private bool _hasFixedRange;

    /// <summary>True when at least two numeric samples are available to draw.</summary>
    [ObservableProperty]
    private bool _hasData;

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

    [RelayCommand]
    private async Task SetRangeAsync(string hoursText)
    {
        if (!int.TryParse(hoursText, out int hours) || hours <= 0)
        {
            hours = 1;
        }

        RangeLabel = hours + "h";
        if (IsZoomed)
        {
            zoomFromUtc_ = null;
            zoomToUtc_ = null;
            IsZoomed = false;
        }

        await ReloadAsync(hours).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync().ConfigureAwait(true);

    /// <summary>Returns to the base time range (1h/8h/24h).</summary>
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

    /// <summary>Zooms the trend to a fixed UTC window, requested by a right-drag on the chart.</summary>
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
                await ReloadAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReloadAsync(int? hours = null)
    {
        int rangeHours = hours ?? ParseRangeHours(RangeLabel);
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
            HmiTrendResponse response = await api_.GetTrendsAsync(SourceId, DaItemId, from, to, 1000, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            List<TrendSample> samples = new();
            foreach (HmiTrendPoint point in response.Points ?? Array.Empty<HmiTrendPoint>())
            {
                if (TryToDouble(point.V, out double y))
                {
                    samples.Add(new TrendSample(point.T, y));
                }
            }

            samples.Sort((a, b) => a.T.CompareTo(b.T));
            Samples = samples;

            DateTime fromUtc = response.FromUtc == default ? from : response.FromUtc;
            DateTime toUtc = response.ToUtc == default ? to : response.ToUtc;
            if (toUtc <= fromUtc)
            {
                fromUtc = from;
                toUtc = to;
            }

            FromUtc = fromUtc;
            ToUtc = toUtc;
            RecomputeAxis();

            string windowLabel = IsZoomed && zoomFromUtc_ is { } wf && zoomToUtc_ is { } wt
                ? FormatDuration(wt - wf)
                : RangeLabel;
            StatusMessage = string.IsNullOrWhiteSpace(response.Error)
                ? (samples.Count == 0 ? "No history" : $"{samples.Count} points ({windowLabel})")
                : response.Error!;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = "Trend error: " + ex.Message;
            Samples = Array.Empty<TrendSample>();
            HasData = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Resolves the min/max/step the chart should draw for the current window,
    /// honoring the auto/fixed range toggle.
    /// </summary>
    private void RecomputeAxis()
    {
        bool hasNumeric = false;
        double dataMin = double.MaxValue;
        double dataMax = double.MinValue;
        foreach (TrendSample sample in Samples)
        {
            if (!double.IsFinite(sample.V))
            {
                continue;
            }

            hasNumeric = true;
            dataMin = Math.Min(dataMin, sample.V);
            dataMax = Math.Max(dataMax, sample.V);
        }

        HasData = hasNumeric;
        double? min = hasNumeric ? dataMin : null;
        double? max = hasNumeric ? dataMax : null;

        bool effectiveAuto = AutoRange && !IsBooleanLike(DataType);
        TrendAxis axis = TrendScale.Resolve(effectiveAuto, fixedRange_, min, max);

        TrendAxis fallback = fixedRange_ is { } tr ? TrendScale.FromTypeRange(tr) : default;
        AxisMin = axis.IsValid ? axis.Min : fallback.IsValid ? fallback.Min : 0;
        AxisMax = axis.IsValid ? axis.Max : fallback.IsValid ? fallback.Max : 1;
        AxisStep = axis.IsValid ? axis.Step : fallback.IsValid ? fallback.Step : 1;
    }

    private static string FormatDuration(TimeSpan span)
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

    private static int ParseRangeHours(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return 1;
        }

        string digits = new string(label.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int h) && h > 0 ? h : 1;
    }

    private static bool IsBooleanLike(string? dataType) =>
        string.Equals(dataType, "Boolean", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataType, "Bool", StringComparison.OrdinalIgnoreCase);

    private static bool TryToDouble(object? value, out double y)
    {
        switch (value)
        {
            case null:
                y = 0;
                return false;
            case double d:
                y = d;
                return true;
            case float f:
                y = f;
                return true;
            case int i:
                y = i;
                return true;
            case long l:
                y = l;
                return true;
            case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number
                && je.TryGetDouble(out double jd):
                y = jd;
                return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double sd):
                y = sd;
                return true;
            default:
                y = 0;
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts_?.Cancel();
        cts_?.Dispose();
        refreshTimer_?.Dispose();
        if (refreshLoop_ is not null)
        {
            try { await refreshLoop_.ConfigureAwait(false); } catch { }
        }

        if (ownsApi_)
        {
            api_.Dispose();
        }
    }
}
