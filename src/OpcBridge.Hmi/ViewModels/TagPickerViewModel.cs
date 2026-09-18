using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>What the operator confirmed in the tag picker.</summary>
/// <param name="Keys">The tags to plot or add, in list order.</param>
/// <param name="Name">Group name when the picker asked for one; null otherwise.</param>
public sealed record TagPickerResult(IReadOnlyList<TagBindingKey> Keys, string? Name);

/// <summary>
/// Tag picker for trend groups: filters a metadata-only tag snapshot (see <see cref="TagListRow"/>)
/// and reports the confirmed selection. The snapshot is taken when the dialog opens and is never
/// rebuilt by live value updates, so a multi-select survives them.
/// </summary>
public partial class TagPickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<TagListRow> all_;

    public TagPickerViewModel(
        IReadOnlyList<TagListRow> rows,
        string title,
        string confirmText,
        bool askForName = false)
    {
        all_ = rows;
        Title = title;
        ConfirmText = confirmText;
        AskForName = askForName;
        RebuildRows();
    }

    public string Title { get; }

    /// <summary>Caption of the confirm button ("Open trend", "Create group", "Add tags").</summary>
    public string ConfirmText { get; }

    /// <summary>True when the dialog also asks for a group name (create-group flow).</summary>
    public bool AskForName { get; }

    /// <summary>Rows matching the current filter.</summary>
    public ObservableCollection<TagListRow> Rows { get; } = new();

    [ObservableProperty]
    private string _filter = string.Empty;

    /// <summary>Name the operator typed; only used when <see cref="AskForName"/>.</summary>
    [ObservableProperty]
    private string _groupName = string.Empty;

    /// <summary>Rows currently selected in the list; kept up to date by the window.</summary>
    public int SelectedCount { get; private set; }

    public bool CanConfirm =>
        SelectedCount > 0 && (!AskForName || !string.IsNullOrWhiteSpace(GroupName));

    /// <summary>Set by the dialog on confirm; null while nothing was confirmed (cancel/close).</summary>
    public TagPickerResult? Result { get; set; }

    public void SetSelectedCount(int count)
    {
        if (SelectedCount == count)
        {
            return;
        }

        SelectedCount = count;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnFilterChanged(string value) => RebuildRows();

    partial void OnGroupNameChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    private void RebuildRows()
    {
        string filter = Filter.Trim();
        Rows.Clear();
        foreach (TagListRow row in all_)
        {
            if (Matches(row, filter))
            {
                Rows.Add(row);
            }
        }
    }

    private static bool Matches(TagListRow row, string filter) =>
        filter.Length == 0
        || Contains(row.DisplayName, filter)
        || Contains(row.DaItemId, filter)
        || Contains(row.SourceName, filter)
        || Contains(row.BridgeId, filter);

    private static bool Contains(string value, string filter) =>
        value.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
