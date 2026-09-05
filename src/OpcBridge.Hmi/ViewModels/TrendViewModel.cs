using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Single-tag trend window: one tag as the only pen in the strip chart. The pen's Y
/// axis is pinned to the tag's data-type range when available (or auto-fitted).
/// </summary>
public partial class TrendViewModel : TrendWindowViewModelBase
{
    private readonly BridgeApiClient api_;
    private readonly bool ownsApi_;
    private readonly (double Min, double Max)? fixedRange_;

    public TrendViewModel(TagBindingKey key, BridgeApiClient api, bool ownsApi = false, string? dataType = null, string? unit = null, string? trendStyle = null, string? displayName = null, string? description = null)
    {
        Key = key;
        api_ = api;
        ownsApi_ = ownsApi;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? key.DaItemId : displayName!;
        Description = description ?? string.Empty;
        BridgeId = key.BridgeId;
        SourceId = key.SourceId;
        DaItemId = key.DaItemId;
        DataType = dataType ?? "Double";
        Unit = unit ?? string.Empty;
        // Booleans are always plotted on their natural 0..1 band so an on/off trace reads clearly.
        (double, double)? typeRange = DataTypeRanges.GetRange(DataType);
        fixedRange_ = IsBooleanLike(DataType) ? (0, 1) : typeRange;

        Pen = new TrendPenViewModel(key, api_, DisplayName, Description, DataType, Unit, trendStyle)
        {
            // Use the palette's single-tag default color; keep it stable across reloads.
            Color = TrendSeriesPalette.ColorFor(0)
        };
        Pen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrendPenViewModel.Samples) or nameof(TrendPenViewModel.IsVisible))
            {
                OnPropertyChanged(nameof(Series));
            }
        };

        Title = DisplayName;
        HasFixedRange = fixedRange_.HasValue;
        _ = ReloadAsync();
    }

    public TagBindingKey Key { get; }

    /// <summary>Short tag name shown in the pen table and window title.</summary>
    public string DisplayName { get; }

    /// <summary>Tag description shown in the pen table.</summary>
    public string Description { get; }

    public string DataType { get; }

    public override string BridgeId { get; }

    public string SourceId { get; }

    public override string DaItemId { get; }

    /// <summary>Tag's engineering unit (e.g. "°C"), shown on readouts and hover chips.</summary>
    public string Unit { get; }

    /// <summary>The single pen row for the pen configuration table.</summary>
    public TrendPenViewModel Pen { get; }

    /// <inheritdoc />
    public override IReadOnlyList<TrendPenViewModel> Pens => new[] { Pen };

    /// <summary>Single-tag trends can configure alarm threshold overlays.</summary>
    public override bool SupportsAlarmLimits => true;

    /// <summary>Chart input: this tag as a single pen in its own strip.</summary>
    public override IReadOnlyList<TrendSeries> Series
    {
        get
        {
            (double Low, double High)? alarms = (AlarmHigh is not null || AlarmLow is not null)
                ? (AlarmLow ?? double.NegativeInfinity, AlarmHigh ?? double.PositiveInfinity)
                : null;

            // Fixed data-type axis only when the operator turned Auto range off
            // (booleans always ride the digital 0..1 band instead).
            (double Min, double Max, double Step)? fixedAxis = null;
            if (!AutoRange && fixedRange_ is { } range)
            {
                TrendAxis fromRange = TrendScale.FromTypeRange(range);
                if (fromRange.IsValid)
                {
                    fixedAxis = (fromRange.Min, fromRange.Max, fromRange.Step);
                }
            }

            return new[]
            {
                new TrendSeries(
                    Pen.Name,
                    Pen.Unit,
                    Pen.TrendStyle,
                    Pen.Color,
                    Pen.Samples,
                    Pen.Description,
                    Visible: Pen.IsVisible,
                    IsBoolean: IsBooleanLike(DataType),
                    AlarmLimits: alarms,
                    FixedAxis: fixedAxis)
            };
        }
    }

    partial void OnAlarmHighChanged(double? value) => OnPropertyChanged(nameof(Series));

    partial void OnAlarmLowChanged(double? value) => OnPropertyChanged(nameof(Series));

    private static string NormalizeTrendStyle(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value.Trim(), "Step", StringComparison.OrdinalIgnoreCase)
            ? "Step"
            : "Continuous";
    }

    protected override async Task ReloadDataAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        await Pen.LoadAsync(from, to, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        FromUtc = from;
        ToUtc = to;

        int count = Pen.Samples.Count;
        string windowLabel = IsZoomed ? FormatDuration(ToUtc - FromUtc) : RangeLabel;
        StatusMessage = string.IsNullOrWhiteSpace(Pen.Error)
            ? (count == 0 ? "No history" : $"{count} points ({windowLabel})")
            : Pen.Error;
    }

    private static bool IsBooleanLike(string? dataType) =>
        string.Equals(dataType, "Boolean", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataType, "Bool", StringComparison.OrdinalIgnoreCase);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        if (ownsApi_)
        {
            api_.Dispose();
        }
    }
}
