using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Designer.Services;

namespace OpcBridge.Hmi.Designer.ViewModels;

/// <summary>One mapped tag in the picker, showing its live value as the cache updates.</summary>
public sealed class DesignerTagRow : ObservableObject
{
    private readonly MultiBridgeTagEntry entry_;

    public DesignerTagRow(MultiBridgeTagEntry entry) => entry_ = entry;

    public TagBindingKey Key => entry_.Key;

    public string DisplayName => entry_.DisplayName;

    public string ItemId => entry_.Key.DaItemId;

    public string ValueText => FormatValue(entry_.Value);

    public string QualityText => FormatQuality(entry_.DaQuality, entry_.IsGood);

    /// <summary>The cache mutates the entry in place, so a delta only needs these refreshes.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(QualityText));
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "—",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "—",
        _ => value.ToString() ?? "—"
    };

    private static string FormatQuality(int? daQuality, bool? isGood)
    {
        if (isGood == true) return daQuality is null ? "Good" : $"Good ({daQuality})";
        if (isGood == false) return daQuality is null ? "Bad" : $"Bad ({daQuality})";
        return daQuality is null ? "—" : daQuality.Value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Tag picker for the Designer: the mapped tags of one source, filtered by typing, with live
/// values. The caller confirms one row; <see cref="Result"/> then carries the tag to bind.
/// </summary>
public sealed partial class DesignerTagPickerViewModel : ObservableObject
{
    private readonly MultiBridgeTagCache cache_;

    public DesignerTagPickerViewModel(
        IReadOnlyList<BridgeSourceInfo> sources,
        MultiBridgeTagCache cache,
        string? initialSourceId)
    {
        cache_ = cache;
        foreach (BridgeSourceInfo source in sources)
        {
            Sources.Add(source);
        }

        BridgeSourceInfo? initial = Sources.FirstOrDefault(
            source => string.Equals(source.SourceId, initialSourceId, StringComparison.OrdinalIgnoreCase));
        SelectedSource = initial ?? Sources.FirstOrDefault();
        RebuildRows();
    }

    public string Title => "Bind tag";

    /// <summary>Sources the bridge reports; picked from here, never typed.</summary>
    public ObservableCollection<BridgeSourceInfo> Sources { get; } = new();

    /// <summary>Mapped tags of the selected source, filtered by <see cref="Filter"/>.</summary>
    public ObservableCollection<DesignerTagRow> Rows { get; } = new();

    [ObservableProperty]
    private BridgeSourceInfo? _selectedSource;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private DesignerTagRow? _selectedRow;

    /// <summary>Set when the operator confirms; null while nothing was confirmed.</summary>
    public TagBindingKey? Result { get; private set; }

    public bool HasRows => Rows.Count > 0;

    public bool CanBind => SelectedSource is not null && SelectedRow is not null;

    public string EmptyMessage
    {
        get
        {
            if (Sources.Count == 0)
            {
                return "No sources on this bridge. Add one in the bridge dashboard, then Refresh.";
            }

            if (SelectedSource is null)
            {
                return "Select a source.";
            }

            if (Filter.Trim().Length > 0 && Rows.Count == 0)
            {
                return "No tags match this filter.";
            }

            return $"No mapped tags on “{SelectedSource.DisplayNameOrId}”. Map tags in the bridge dashboard, then Refresh.";
        }
    }

    partial void OnSelectedSourceChanged(BridgeSourceInfo? value)
    {
        RebuildRows();
        OnPropertyChanged(nameof(EmptyMessage));
    }

    partial void OnFilterChanged(string value)
    {
        RebuildRows();
        OnPropertyChanged(nameof(EmptyMessage));
    }

    partial void OnSelectedRowChanged(DesignerTagRow? value) => OnPropertyChanged(nameof(CanBind));

    /// <summary>Accepts the current selection as the picker's result.</summary>
    public void Confirm()
    {
        if (CanBind)
        {
            Result = SelectedRow!.Key;
        }
    }

    /// <summary>
    /// Live update while the dialog is open: refresh values in place (deltas arrive many times a
    /// second), and rebuild only when the visible tag set itself changed (map edit or reconnect).
    /// </summary>
    public void RefreshLive()
    {
        foreach (DesignerTagRow row in Rows)
        {
            row.Refresh();
        }

        if (Rows.Count != CountVisibleEntries())
        {
            RebuildRows();
        }
    }

    private int CountVisibleEntries()
    {
        if (SelectedSource is null)
        {
            return 0;
        }

        string filter = Filter.Trim();
        return cache_.Tags.Count(entry =>
            string.Equals(entry.Key.SourceId, SelectedSource.SourceId, StringComparison.OrdinalIgnoreCase)
            && (filter.Length == 0 || Matches(entry, filter)));
    }

    private void RebuildRows()
    {
        TagBindingKey? previous = SelectedRow?.Key;
        Rows.Clear();

        if (SelectedSource is not null)
        {
            string filter = Filter.Trim();
            IEnumerable<MultiBridgeTagEntry> entries = cache_.Tags
                .Where(entry => string.Equals(
                    entry.Key.SourceId,
                    SelectedSource.SourceId,
                    StringComparison.OrdinalIgnoreCase));

            if (filter.Length > 0)
            {
                entries = entries.Where(entry => Matches(entry, filter));
            }

            foreach (MultiBridgeTagEntry entry in entries
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Key.DaItemId, StringComparer.OrdinalIgnoreCase))
            {
                Rows.Add(new DesignerTagRow(entry));
            }
        }

        DesignerTagRow? restored = previous is null
            ? null
            : Rows.FirstOrDefault(row => row.Key.EqualsIgnoreCase(previous.Value));
        SelectedRow = restored ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(HasRows));
    }

    private static bool Matches(MultiBridgeTagEntry entry, string filter) =>
        Contains(entry.DisplayName, filter)
        || Contains(entry.Key.DaItemId, filter)
        || Contains(entry.Description, filter)
        || Contains(entry.Unit, filter);

    private static bool Contains(string? value, string filter) =>
        !string.IsNullOrEmpty(value) && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
