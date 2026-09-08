using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// One pen row in the trend's pen configuration table: checkbox (visible), color swatch
/// (click to cycle), name, description, unit, live value and windowed min/max/avg
/// readouts, plus its own history loading. Rendered as one stacked strip with its own
/// Y axis (VTScada Historical Data Viewer layout).
/// </summary>
public partial class TrendPenViewModel : ObservableObject
{
    private readonly BridgeApiClient api_;

    public TrendPenViewModel(
        TagBindingKey key,
        BridgeApiClient api,
        string? displayName = null,
        string? description = null,
        string? dataType = null,
        string? unit = null,
        string? trendStyle = null,
        string? color = null)
    {
        Key = key;
        api_ = api;
        Name = string.IsNullOrWhiteSpace(displayName) ? key.DaItemId : displayName!;
        Description = description ?? string.Empty;
        DataType = dataType ?? "Double";
        Unit = unit ?? string.Empty;
        TrendStyle = NormalizeTrendStyle(trendStyle);
        Color = string.IsNullOrWhiteSpace(color) ? TrendSeriesPalette.ColorFor(0) : color!;
    }

    public TagBindingKey Key { get; }

    /// <summary>Short pen name (tag display name or item id).</summary>
    public string Name { get; }

    /// <summary>Tag description shown in the pen table (e.g. "Level of Tank 1").</summary>
    public string Description { get; }

    public string DataType { get; }

    /// <summary>Engineering unit (e.g. "%" or "°C") appended to the value readouts.</summary>
    public string Unit { get; }

    /// <summary>"Continuous" (line) or "Step" (sample-and-hold) trace rendering.</summary>
    public string TrendStyle { get; }

    /// <summary>True when this pen plots a discrete on/off signal (square-wave strip).</summary>
    public bool IsBoolean => IsBooleanLike(DataType);

    /// <summary>Pen color; assigned by the owning trend from the shared palette.</summary>
    private string color_ = TrendSeriesPalette.ColorFor(0);

    public string Color
    {
        get => color_;
        set
        {
            if (string.Equals(color_, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            color_ = value;
            OnPropertyChanged(nameof(Color));
            OnPropertyChanged(nameof(Series));
        }
    }

    /// <summary>True while this pen is plotted; toggled from the pen-table checkbox.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Numeric samples with timestamps, newest last.</summary>
    [ObservableProperty]
    private IReadOnlyList<TrendSample> _samples = Array.Empty<TrendSample>();

    /// <summary>Non-empty when the last load for this tag failed.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>Chart input: this pen as one series in its strip.</summary>
    public TrendSeries Series => new(
        Name,
        Unit,
        TrendStyle,
        Color,
        Samples,
        Description,
        Visible: IsVisible,
        IsBoolean: IsBoolean,
        FixedAxis: CustomAxis);

    partial void OnSamplesChanged(IReadOnlyList<TrendSample> value) => OnPropertyChanged(nameof(Series));

    partial void OnIsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(Series));
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Value column text (last sample, with unit or %/state formatting).</summary>
    public string ValueText => FormatValue(LastValue);

    /// <summary>
    /// Actual data range of the loaded window ("min – max"), shown as the Range column
    /// readout and as the editing watermarks. Booleans report their 0..1 band.
    /// </summary>
    public string ActualRangeText
    {
        get
        {
            if (IsBoolean)
            {
                return "0 – 1";
            }

            return Stats is { } stats
                && double.IsFinite(stats.Minimum)
                && double.IsFinite(stats.Maximum)
                ? FormatValue(stats.Minimum) + " – " + FormatValue(stats.Maximum)
                : "—";
        }
    }

    /// <summary>Watermark for the min box: the pen's actual data minimum.</summary>
    public string RangeMinWatermark
    {
        get
        {
            if (IsBoolean)
            {
                return "0";
            }

            return Stats is { } stats && double.IsFinite(stats.Minimum)
                ? Math.Round(stats.Minimum, 3).ToString("0.###", CultureInfo.InvariantCulture)
                : "min";
        }
    }

    /// <summary>Watermark for the max box: the pen's actual data maximum.</summary>
    public string RangeMaxWatermark
    {
        get
        {
            if (IsBoolean)
            {
                return "1";
            }

            return Stats is { } stats && double.IsFinite(stats.Maximum)
                ? Math.Round(stats.Maximum, 3).ToString("0.###", CultureInfo.InvariantCulture)
                : "max";
        }
    }

    /// <summary>Editable custom minimum for this pen's Y axis; empty = auto.</summary>
    [ObservableProperty]
    private string _rangeMinText = string.Empty;

    /// <summary>Editable custom maximum for this pen's Y axis; empty = auto.</summary>
    [ObservableProperty]
    private string _rangeMaxText = string.Empty;

    /// <summary>True when the operator typed a custom min and/or max for this pen.</summary>
    public bool HasCustomAxis => ParsedMin is not null || ParsedMax is not null;

    /// <summary>Resets this pen's Y axis to auto-fit (clears the typed min/max).</summary>
    public void ClearCustomRange()
    {
        RangeMinText = string.Empty;
        RangeMaxText = string.Empty;
    }

    private double? ParsedMin => TryParse(RangeMinText);

    private double? ParsedMax => TryParse(RangeMaxText);

    /// <summary>
    /// Custom Y axis for the chart when a min and/or max is typed: missing bounds fall
    /// back to the pen's actual data extremes so a one-sided limit still plots well.
    /// Null when no custom range is set (chart auto-fits).
    /// </summary>
    public (double Min, double Max, double Step)? CustomAxis
    {
        get
        {
            double? lo = ParsedMin;
            double? hi = ParsedMax;
            if (lo is null && hi is null)
            {
                return null;
            }

            double dataLo = Stats is { } s && double.IsFinite(s.Minimum) ? s.Minimum : 0;
            double dataHi = Stats is { } s2 && double.IsFinite(s2.Maximum) ? s2.Maximum : lo is { } l ? l + 1 : hi!.Value;
            if (dataHi <= dataLo)
            {
                dataHi = dataLo + 1;
            }

            double min = Math.Min(lo ?? dataLo, hi ?? dataHi);
            double max = Math.Max(lo ?? dataLo, hi ?? dataHi);
            if (max <= min)
            {
                max = min + 1;
            }

            TrendAxis nice = TrendScale.FromBounds(min, max);
            return nice.IsValid ? (nice.Min, nice.Max, nice.Step) : (min, max, (max - min) / 5);
        }
    }

    partial void OnRangeMinTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasCustomAxis));
        OnPropertyChanged(nameof(Series));
        OnPropertyChanged(nameof(RangeText));
    }

    partial void OnRangeMaxTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasCustomAxis));
        OnPropertyChanged(nameof(Series));
        OnPropertyChanged(nameof(RangeText));
    }

    private static double? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value)
            ? value
            : null;
    }

    /// <summary>
    /// The scale this pen is currently plotted at: the custom range when set, otherwise
    /// the auto-fit axis the chart computes (same policy). Shown in the Range column.
    /// </summary>
    public string RangeText
    {
        get
        {
            if (CustomAxis is { } custom)
            {
                return FormatValue(custom.Min) + " – " + FormatValue(custom.Max);
            }

            if (IsBoolean)
            {
                return "0 – 1";
            }

            if (Stats is not { } stats
                || !double.IsFinite(stats.Minimum)
                || !double.IsFinite(stats.Maximum))
            {
                return "—";
            }

            TrendAxis axis = TrendScale.Resolve(
                AxisAutoRange,
                DataTypeRanges.GetRange(DataType),
                stats.Minimum,
                stats.Maximum);
            return axis.IsValid
                ? FormatValue(axis.Min) + " – " + FormatValue(axis.Max)
                : "—";
        }
    }

    /// <summary>
    /// True when this pen's axis auto-fits its data; the owning trend pins it to the
    /// tag's data-type range when the operator turns Auto range off (single tags).
    /// </summary>
    [ObservableProperty]
    private bool _axisAutoRange = true;

    partial void OnAxisAutoRangeChanged(bool value) => OnPropertyChanged(nameof(RangeText));

    /// <summary>
    /// Minimum column text over the visible window.</summary>
    public string MinimumText => FormatValue(Stats?.Minimum);

    /// <summary>Maximum column text over the visible window.</summary>
    public string MaximumText => FormatValue(Stats?.Maximum);

    /// <summary>Average column text over the visible window.</summary>
    public string AverageText => FormatValue(Stats?.Average);

    /// <summary>Delta (max−min) text, shown in the table's summary group.</summary>
    public string DeltaText => FormatValue(Stats?.Delta);

    /// <summary>Standard deviation text (statistical analytics panel column).</summary>
    public string StdDevText => FormatValue(Stats?.StdDev);

    /// <summary>Sample count in the window (diagnostic column).</summary>
    public string PointsText => Stats is { } stats ? stats.Count.ToString("N0", CultureInfo.InvariantCulture) : "0";

    private TrendPenStats.Result? Stats { get; set; }

    private double? LastValue { get; set; }

    // Window the current stats were computed over (set by the latest LoadAsync).
    private DateTime statsFrom_ = DateTime.UtcNow.AddHours(-24);

    private DateTime statsTo_ = DateTime.UtcNow;

    private string FormatValue(double? value)
    {
        if (value is not { } v)
        {
            return "—";
        }

        string text = Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        if (IsBoolean)
        {
            return text;
        }

        return string.IsNullOrWhiteSpace(Unit) ? text : text + " " + Unit.Trim();
    }

    /// <summary>Recomputes the pen-table readouts over the last loaded window.</summary>
    private void RecomputeStats()
    {
        Stats = TrendPenStats.Compute(Samples, statsFrom_, statsTo_);
        LastValue = Stats?.Value;
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(ActualRangeText));
        OnPropertyChanged(nameof(RangeMinWatermark));
        OnPropertyChanged(nameof(RangeMaxWatermark));
        OnPropertyChanged(nameof(MinimumText));
        OnPropertyChanged(nameof(MaximumText));
        OnPropertyChanged(nameof(AverageText));
        OnPropertyChanged(nameof(DeltaText));
        OnPropertyChanged(nameof(StdDevText));
        OnPropertyChanged(nameof(PointsText));
    }

    /// <summary>
    /// Loads history for this pen over the window (thinned to <paramref name="maxPoints"/>
    /// by the bridge); failures are recorded, not thrown.
    /// </summary>
    public async Task LoadAsync(DateTime from, DateTime to, int maxPoints, CancellationToken ct)
    {
        try
        {
            HmiTrendResponse response = await api_.GetTrendsAsync(Key.SourceId, Key.DaItemId, from, to, maxPoints, ct)
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
            Error = response.Error ?? string.Empty;
            statsFrom_ = from;
            statsTo_ = to;
            RecomputeStats();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Samples = Array.Empty<TrendSample>();
            statsFrom_ = from;
            statsTo_ = to;
            RecomputeStats();
        }
    }

    /// <summary>Cycles to the next palette color (pen-table swatch click).</summary>
    public void CycleColor()
    {
        int index = Array.IndexOf(TrendSeriesPalette.Colors, Color);
        Color = TrendSeriesPalette.ColorFor(index < 0 ? 1 : index + 1);
    }

    private static bool IsBooleanLike(string? dataType) =>
        string.Equals(dataType, "Boolean", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataType, "Bool", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTrendStyle(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value.Trim(), "Step", StringComparison.OrdinalIgnoreCase)
            ? "Step"
            : "Continuous";
    }

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
}
