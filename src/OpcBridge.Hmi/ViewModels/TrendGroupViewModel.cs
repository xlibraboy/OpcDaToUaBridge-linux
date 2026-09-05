using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Group trend window: several tags plotted on one chart with a shared time window,
/// zoom and Y axis. Each tag is a <see cref="TrendSeriesViewModel"/> drawn in its own
/// palette color; a legend inside the chart maps colors to tags.
/// </summary>
public partial class TrendGroupViewModel : TrendWindowViewModelBase
{
    /// <summary>Tags in stable order; order also fixes the legend/trace palette colors.</summary>
    public ObservableCollection<TrendSeriesViewModel> SeriesItems { get; } = new();

    public TrendGroupViewModel(IEnumerable<TrendSeriesViewModel> series)
    {
        foreach (TrendSeriesViewModel item in series)
        {
            SeriesItems.Add(item);
            item.PropertyChanged += OnSeriesPropertyChanged;
        }

        for (int i = 0; i < SeriesItems.Count; i++)
        {
            SeriesItems[i].Color = TrendSeriesPalette.ColorFor(i);
        }

        Title = SeriesItems.Count switch
        {
            0 => "Trend group",
            1 => SeriesItems[0].Title,
            _ => $"Group trend · {SeriesItems.Count} tags"
        };
        // A group mixes tags, so the Y axis is always fitted to the data (no fixed
        // data-type range); the Auto range toggle stays visible but disabled.
        HasFixedRange = false;
        AutoRange = true;
        RecomputeAxis();
        _ = ReloadAsync();
    }

    /// <summary>Group trends offer the shared/percent Y-axis choice for mixed units.</summary>
    public override bool SupportsPercentAxis => true;

    /// <summary>Chart input: one series per tag, in stable order.</summary>
    public override IReadOnlyList<TrendSeries> Series => SeriesItems.Select(s => s.Series).ToArray();

    private void OnSeriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrendSeriesViewModel.Samples)
            or nameof(TrendSeriesViewModel.Color)
            or nameof(TrendSeriesViewModel.IsVisible))
        {
            OnPropertyChanged(nameof(Series));
        }
    }

    /// <summary>Toggles a trace's visibility from the legend row click.</summary>
    public void ToggleSeriesVisibility(string seriesName)
    {
        TrendSeriesViewModel? item = FindSeries(seriesName);
        if (item is not null)
        {
            item.IsVisible = !item.IsVisible;
        }
    }

    /// <summary>Cycles a trace's color from the legend swatch click.</summary>
    public void CycleSeriesColor(string seriesName)
    {
        FindSeries(seriesName)?.CycleColor();
    }

    private TrendSeriesViewModel? FindSeries(string seriesName)
    {
        foreach (TrendSeriesViewModel item in SeriesItems)
        {
            if (string.Equals(item.Title, seriesName, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    protected override async Task ReloadDataAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        Task[] loads = SeriesItems.Select(s => s.LoadAsync(from, to, ct)).ToArray();
        await Task.WhenAll(loads).ConfigureAwait(true);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        FromUtc = from;
        ToUtc = to;
        RecomputeAxis();
        OnPropertyChanged(nameof(Series));

        int total = SeriesItems.Sum(s => s.Samples.Count);
        string[] errors = SeriesItems.Where(s => s.HasError).Select(s => s.Title).ToArray();
        string windowLabel = IsZoomed ? FormatDuration(ToUtc - FromUtc) : RangeLabel;
        StatusMessage = errors.Length > 0
            ? $"{total} points ({windowLabel}) · errors: {string.Join(", ", errors)}"
            : total == 0
                ? $"No history ({windowLabel})"
                : $"{total} points ({windowLabel}) across {SeriesItems.Count} tags";
    }

    /// <summary>Fits the shared Y axis to the numeric samples of every series.</summary>
    protected override void RecomputeAxis()
    {
        double? min = null;
        double? max = null;
        foreach (TrendSeriesViewModel item in SeriesItems)
        {
            foreach (TrendSample sample in item.Samples)
            {
                if (!double.IsFinite(sample.V))
                {
                    continue;
                }

                min = min is null ? sample.V : Math.Min(min.Value, sample.V);
                max = max is null ? sample.V : Math.Max(max.Value, sample.V);
            }
        }

        TrendAxis axis = TrendScale.Resolve(autoRange: true, typeRange: null, min, max);
        AxisMin = axis.IsValid ? axis.Min : 0;
        AxisMax = axis.IsValid ? axis.Max : 1;
        AxisStep = axis.IsValid ? axis.Step : 1;
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (TrendSeriesViewModel item in SeriesItems)
        {
            item.PropertyChanged -= OnSeriesPropertyChanged;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}