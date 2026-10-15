using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One row of the Tags tab's "From bridge…" picker: a mapped bridge tag as the snapshot taken when
/// the window opened — its name, its source, the reading it had, and whether the area's tags
/// already hold it.
/// </summary>
/// <param name="Marker">Short right-hand note ("in the area's tags"); empty hides it.</param>
/// <param name="Enabled">False greys the row out — the area's tags already hold that tag.</param>
public sealed record LogicTagPickRow(
    string SourceId,
    string ItemId,
    string DisplayName,
    string SourceName,
    string ValueText,
    string Marker = "",
    bool Enabled = true)
{
    /// <summary>The tag's display name, else its item id — what a row reads as.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? ItemId : DisplayName;

    /// <summary>True when there is a note to show in the row's right-hand marker cell.</summary>
    public bool HasMarker => Marker.Length > 0;
}

/// <summary>
/// The Tags tab's "From bridge…" picker: filters the bridge's mapped tags and reports the confirmed
/// selection, which the panel adds to the area's tags as one named entry per tag. The list is a
/// snapshot taken when the window opens, so ticking several rows is never disturbed by the value
/// stream, and a tag the area's tags already hold is greyed out rather than offered twice.
/// </summary>
public partial class LogicTagPickViewModel : ObservableObject
{
    private readonly IReadOnlyList<LogicTagPickRow> all_;

    public LogicTagPickViewModel(IReadOnlyList<LogicTagPickRow> rows, string areaName)
    {
        all_ = rows;
        AreaName = areaName;
        RebuildRows();
    }

    /// <summary>The area's tags the picked tags are added to.</summary>
    public string AreaName { get; }

    public string Title => $"Add tags from the bridge — {AreaName}";

    /// <summary>The window's footer note (what the readings are, and that nothing is saved yet).</summary>
    public string Hint => LogicText.TagPickHint;

    /// <summary>Rows matching the current filter.</summary>
    public ObservableCollection<LogicTagPickRow> Rows { get; } = new();

    [ObservableProperty]
    private string _filter = string.Empty;

    /// <summary>Rows currently selected in the list; kept up to date by the window.</summary>
    public int SelectedCount { get; private set; }

    public bool CanConfirm => SelectedCount > 0;

    /// <summary>Shown in place of the list when it has no rows to offer.</summary>
    public string EmptyText => all_.Count == 0
        ? LogicText.TagPickNoTags
        : LogicText.TagPickNoMatch(Filter.Trim());

    public bool ShowsEmptyText => Rows.Count == 0;

    /// <summary>Set by the dialog on confirm; null while nothing was confirmed (cancel/close).</summary>
    public IReadOnlyList<LogicTagPickRow>? Result { get; set; }

    public void SetSelectedCount(int count)
    {
        if (SelectedCount == count)
        {
            return;
        }

        SelectedCount = count;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnFilterChanged(string value)
    {
        RebuildRows();
        OnPropertyChanged(nameof(ShowsEmptyText));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void RebuildRows()
    {
        string filter = Filter.Trim();
        Rows.Clear();
        foreach (LogicTagPickRow row in all_)
        {
            if (Matches(row, filter))
            {
                Rows.Add(row);
            }
        }
    }

    private static bool Matches(LogicTagPickRow row, string filter) =>
        filter.Length == 0
        || Contains(row.DisplayName, filter)
        || Contains(row.ItemId, filter)
        || Contains(row.SourceName, filter);

    private static bool Contains(string value, string filter) =>
        value.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
