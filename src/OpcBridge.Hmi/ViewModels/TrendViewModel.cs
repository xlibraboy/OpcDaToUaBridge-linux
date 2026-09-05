using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Single-tag trend window: one tag's history plotted as one trace, with the Y axis
/// pinned to the tag's data-type range when available (or auto-fitted).
/// </summary>
public partial class TrendViewModel : TrendWindowViewModelBase
{
    private readonly BridgeApiClient api_;
    private readonly bool ownsApi_;
    private readonly (double Min, double Max)? fixedRange_;

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
    }

    public TagBindingKey Key { get; }

    public string DataType { get; }

    public override string BridgeId { get; }

    public string SourceId { get; }

    public override string DaItemId { get; }

    /// <summary>Tag's engineering unit (e.g. "°C"), shown on the pinned readout and hover cursor.</summary>
    [ObservableProperty]
    private string _unit = string.Empty;

    /// <summary>
    /// How this tag's history renders: "Continuous" (line through the samples, default) or
    /// "Step" (sample-and-hold). Set per-tag in the dashboard Maps faceplate.
    /// </summary>
    [ObservableProperty]
    private string _trendStyle = "Continuous";

    /// <summary>Numeric samples with timestamps, newest last.</summary>
    [ObservableProperty]
    private IReadOnlyList<TrendSample> _samples = Array.Empty<TrendSample>();

    /// <summary>True when at least two numeric samples are available to draw.</summary>
    [ObservableProperty]
    private bool _hasData;

    /// <summary>Single-tag trends can configure alarm threshold overlays.</summary>
    public override bool SupportsAlarmLimits => true;

    /// <summary>Chart input: this tag as a single series, using the palette's default color.</summary>
    public override IReadOnlyList<TrendSeries> Series =>
        new[] { new TrendSeries(Title, Unit, TrendStyle, TrendSeriesPalette.ColorFor(0), Samples, IsBoolean: IsBooleanLike(DataType)) };

    partial void OnSamplesChanged(IReadOnlyList<TrendSample> value)
    {
        OnPropertyChanged(nameof(Series));
    }

    partial void OnUnitChanged(string value)
    {
        OnPropertyChanged(nameof(Series));
    }

    partial void OnTrendStyleChanged(string value)
    {
        OnPropertyChanged(nameof(Series));
    }

    private static string NormalizeTrendStyle(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value.Trim(), "Step", StringComparison.OrdinalIgnoreCase)
            ? "Step"
            : "Continuous";
    }

    protected override async Task ReloadDataAsync(DateTime from, DateTime to, CancellationToken ct)
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

        string windowLabel = IsZoomed ? FormatDuration(ToUtc - FromUtc) : RangeLabel;
        StatusMessage = string.IsNullOrWhiteSpace(response.Error)
            ? (samples.Count == 0 ? "No history" : $"{samples.Count} points ({windowLabel})")
            : response.Error!;
    }

    /// <summary>
    /// Resolves the min/max/step the chart should draw for the current window,
    /// honoring the auto/fixed range toggle.
    /// </summary>
    protected override void RecomputeAxis()
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

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        if (ownsApi_)
        {
            api_.Dispose();
        }
    }
}