using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// Group trend window: several tags as stacked pens, each in its own strip with its own
/// Y axis (digital pens as square-wave strips), plus the pen configuration table at the
/// bottom. Each pen loads its own history for the shared time window.
/// </summary>
public partial class TrendGroupViewModel : TrendWindowViewModelBase
{
    /// <summary>Backing pen collection; order also fixes the strip stack and palette colors.</summary>
    public ObservableCollection<TrendPenViewModel> PenRows { get; } = new();

    /// <inheritdoc />
    public override IReadOnlyList<TrendPenViewModel> Pens => PenRows;

    public TrendGroupViewModel(IEnumerable<TrendPenViewModel> pens)
    {
        foreach (TrendPenViewModel pen in pens)
        {
            PenRows.Add(pen);
            pen.PropertyChanged += OnPenPropertyChanged;
        }

        for (int i = 0; i < PenRows.Count; i++)
        {
            PenRows[i].Color = TrendSeriesPalette.ColorFor(i);
        }

        Title = PenRows.Count switch
        {
            0 => "Trend group",
            1 => PenRows[0].Name,
            _ => $"Group trend · {PenRows.Count} tags"
        };

        _ = ReloadAsync();
    }

    /// <summary>Group trends offer the per-pen/percent Y-axis choice for mixed units.</summary>
    public override bool SupportsPercentAxis => true;

    /// <summary>Chart input: one pen per strip, in stable order.</summary>
    public override IReadOnlyList<TrendSeries> Series => PenRows.Select(p => p.Series).ToArray();

    private void OnPenPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrendPenViewModel.Samples)
            or nameof(TrendPenViewModel.IsVisible)
            or nameof(TrendPenViewModel.Color))
        {
            OnPropertyChanged(nameof(Series));
        }
    }

    /// <summary>Toggles a pen's visibility from the pen-table checkbox.</summary>
    public void TogglePen(string penName)
    {
        TrendPenViewModel? pen = FindPen(penName);
        if (pen is not null)
        {
            pen.IsVisible = !pen.IsVisible;
        }
    }

    /// <summary>Cycles a pen's color from the pen-table swatch click.</summary>
    public void CyclePenColor(string penName)
    {
        FindPen(penName)?.CycleColor();
    }

    private TrendPenViewModel? FindPen(string penName)
    {
        foreach (TrendPenViewModel pen in PenRows)
        {
            if (string.Equals(pen.Name, penName, StringComparison.OrdinalIgnoreCase))
            {
                return pen;
            }
        }

        return null;
    }

    protected override async Task ReloadDataAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        Task[] loads = PenRows.Select(p => p.LoadAsync(from, to, ct)).ToArray();
        await Task.WhenAll(loads).ConfigureAwait(true);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        FromUtc = from;
        ToUtc = to;
        OnPropertyChanged(nameof(Series));

        int total = PenRows.Sum(p => p.Samples.Count);
        string[] errors = PenRows.Where(p => p.HasError).Select(p => p.Name).ToArray();
        string windowLabel = IsZoomed ? FormatDuration(ToUtc - FromUtc) : RangeLabel;
        StatusMessage = errors.Length > 0
            ? $"{total} points ({windowLabel}) · errors: {string.Join(", ", errors)}"
            : total == 0
                ? $"No history ({windowLabel})"
                : $"{total} points ({windowLabel}) across {PenRows.Count} tags";
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (TrendPenViewModel pen in PenRows)
        {
            pen.PropertyChanged -= OnPenPropertyChanged;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
