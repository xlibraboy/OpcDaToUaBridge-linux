using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Single-tag trend window: one tag as the only pen in the strip chart. The pen's Y axis is
/// pinned to the tag's configured engineering range when the dashboard set one, else to the
/// tag's data-type range when available (or auto-fitted).
/// </summary>
public partial class TrendViewModel : TrendWindowViewModelBase
{
    private readonly BridgeApiClient api_;
    private readonly bool ownsApi_;
    private readonly (double Min, double Max)? fixedRange_;

    public TrendViewModel(TagBindingKey key, BridgeApiClient api, bool ownsApi = false, string? dataType = null, string? unit = null, double? rangeMin = null, double? rangeMax = null, string? trendStyle = null, string? displayName = null, string? description = null)
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
        // The axis this trend opens on: the tag's own range when the dashboard set one, else
        // the natural range of its type. Booleans are always plotted on their 0..1 band so an
        // on/off trace reads clearly; floating types without a range auto-fit.
        (double, double)? tagRange = TrendScale.TagRange(rangeMin, rangeMax);
        (double, double)? typeRange = DataTypeRanges.GetRange(DataType);
        fixedRange_ = IsBooleanLike(DataType) ? (0, 1) : tagRange ?? typeRange;

        Pen = new TrendPenViewModel(key, api_, DisplayName, Description, DataType, Unit, trendStyle, rangeMin: rangeMin, rangeMax: rangeMax)
        {
            // Use the palette's single-tag default color; keep it stable across reloads.
            Color = TrendSeriesPalette.ColorFor(0)
        };
        Pen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrendPenViewModel.Samples)
                or nameof(TrendPenViewModel.IsVisible)
                or nameof(TrendPenViewModel.Series)
                or nameof(TrendPenViewModel.IsSelected))
            {
                OnPropertyChanged(nameof(Series));
            }

            if (e.PropertyName is nameof(TrendPenViewModel.HasCustomAxis))
            {
                OnPropertyChanged(nameof(HasCustomRanges));
            }
        };

        Title = DisplayName;
        HasFixedRange = fixedRange_.HasValue;
        // Keep the pen's Range column in step with the Auto-range toggle (it decides
        // between auto-fit and the pinned scale).
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AutoRange))
            {
                Pen.AxisAutoRange = AutoRange;
            }
        };
        // A tag with a configured range opens on it — that is the scale the operator set up;
        // with no range the fitted axis stays the default.
        AutoRange = !tagRange.HasValue;
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

    /// <summary>
    /// Row click on the single pen row: toggles the thick-line emphasis. (Ctrl+click
    /// behaves the same — there is only one pen to emphasize.)
    /// </summary>
    public override void SelectPen(string penName, bool additive = false)
    {
        Pen.HasSelectionContext = !Pen.IsSelected;
        Pen.IsSelected = !Pen.IsSelected;
        OnPropertyChanged(nameof(Series));
    }

    /// <summary>Click on the row's empty area: clears the thick-line emphasis.</summary>
    public override void ClearPenSelection()
    {
        if (Pen.IsSelected || Pen.HasSelectionContext)
        {
            Pen.HasSelectionContext = false;
            Pen.IsSelected = false;
            OnPropertyChanged(nameof(Series));
        }
    }

    /// <summary>Chart input: this tag as a single pen in its own strip.</summary>
    public override IReadOnlyList<TrendSeries> Series
    {
        get
        {
            (double Low, double High)? alarms = (AlarmHigh is not null || AlarmLow is not null)
                ? (AlarmLow ?? double.NegativeInfinity, AlarmHigh ?? double.PositiveInfinity)
                : null;

            // The pen owns the axis policy: the operator's typed range wins, then the tag's
            // configured range (else its data-type range) while Auto range is off.
            (double Min, double Max, double Step)? fixedAxis = Pen.FixedAxis;

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
                    FixedAxis: fixedAxis,
                    StrokeWidth: Pen.StrokeWidthFor())
            };
        }
    }

    private static string NormalizeTrendStyle(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value.Trim(), "Step", StringComparison.OrdinalIgnoreCase)
            ? "Step"
            : "Continuous";
    }

    protected override async Task ReloadDataAsync(DateTime from, DateTime to, int maxPoints, CancellationToken ct)
    {
        await Pen.LoadAsync(from, to, maxPoints, ct).ConfigureAwait(true);
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
