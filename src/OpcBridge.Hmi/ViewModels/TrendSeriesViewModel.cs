using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// One tag inside a group trend: owns its API call and the loaded samples for the
/// group's shared time window, plus the per-tag rendering settings (name, unit, line
/// style, color). Loads only via the owning <see cref="TrendGroupViewModel"/>, which
/// drives all series with the same window.
/// </summary>
public partial class TrendSeriesViewModel : ObservableObject
{
    private readonly BridgeApiClient api_;

    public TrendSeriesViewModel(
        TagBindingKey key,
        BridgeApiClient api,
        string? dataType = null,
        string? unit = null,
        string? trendStyle = null,
        string? color = null)
    {
        Key = key;
        api_ = api;
        Title = string.IsNullOrWhiteSpace(key.SourceId) ? key.DaItemId : $"{key.SourceId} / {key.DaItemId}";
        DataType = dataType ?? "Double";
        Unit = unit ?? string.Empty;
        TrendStyle = NormalizeTrendStyle(trendStyle);
        Color = string.IsNullOrWhiteSpace(color) ? TrendSeriesPalette.ColorFor(0) : color!;
    }

    public TagBindingKey Key { get; }

    public string Title { get; }

    public string DataType { get; }

    /// <summary>Tag's engineering unit (e.g. "°C"), shown on the legend and hover cursor.</summary>
    public string Unit { get; }

    /// <summary>
    /// How this tag's trace renders: "Continuous" (line, default) or "Step" (sample-and-hold).
    /// Set per-tag in the dashboard Maps faceplate.
    /// </summary>
    public string TrendStyle { get; }

    /// <summary>Trace color on the chart, assigned by the owning group from the shared palette.</summary>
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

    /// <summary>True while this trace is shown on the chart; toggled from the legend.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Cycles to the next palette color (legend color swatch click).</summary>
    public void CycleColor()
    {
        int index = Array.IndexOf(TrendSeriesPalette.Colors, Color);
        Color = TrendSeriesPalette.ColorFor(index < 0 ? 1 : index + 1);
    }

    /// <summary>Numeric samples with timestamps, newest last.</summary>
    [ObservableProperty]
    private IReadOnlyList<TrendSample> _samples = Array.Empty<TrendSample>();

    /// <summary>Non-empty when the last load for this tag failed.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>Chart input: this tag as one series with its own color.</summary>
    public TrendSeries Series => new(Title, Unit, TrendStyle, Color, Samples, Visible: IsVisible, IsBoolean: IsBooleanLike(DataType));

    partial void OnSamplesChanged(IReadOnlyList<TrendSample> value) => OnPropertyChanged(nameof(Series));

    partial void OnIsVisibleChanged(bool value) => OnPropertyChanged(nameof(Series));

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Loads history for this tag over the window; failures are recorded, not thrown.</summary>
    public async Task LoadAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            HmiTrendResponse response = await api_.GetTrendsAsync(Key.SourceId, Key.DaItemId, from, to, 1000, ct)
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Samples = Array.Empty<TrendSample>();
        }
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