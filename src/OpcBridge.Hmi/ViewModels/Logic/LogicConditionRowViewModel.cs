using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One editable condition row (the simple form). The row reads one of its area's named tags —
/// the Tags tab's entries, where the tag and its source are assigned once for every block of the
/// area. A stored direct (source, tag) pair, left by a block authored before the area's tags
/// existed, is carried through a save until the area's list holds it, and reads as its own line
/// while it is not in that list.
/// </summary>
public partial class LogicConditionRowViewModel : ObservableObject, ILogicOrderRow
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicConditionRowViewModel>? onRemove_;
    private readonly LogicRowList<LogicConditionRowViewModel>? rowList_;
    private readonly string storedSourceId_;
    private readonly string storedItemId_;

    public LogicConditionRowViewModel(
        LogicConditionDto condition,
        LogicTagCatalog catalog,
        Action<LogicConditionRowViewModel>? onRemove = null,
        IEnumerable<LogicTagEntryViewModel>? tagEntries = null,
        LogicRowList<LogicConditionRowViewModel>? rowList = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
        rowList_ = rowList;
        storedSourceId_ = condition.SourceId ?? string.Empty;
        storedItemId_ = condition.ItemId ?? string.Empty;
        ConditionId = condition.Id == Guid.Empty ? Guid.NewGuid() : condition.Id;
        Text = condition.Text;
        Op = LogicSelectOption.Find(LogicOptions.Ops, condition.Op) ?? LogicOptions.Ops[0];
        ValueText = condition.Value is { } value ? FormatNumber(value) : string.Empty;
        Group = condition.Group;
        HoldSeconds = condition.HoldMs > 0 ? FormatNumber(condition.HoldMs / 1000.0) : string.Empty;
        Severity = LogicSelectOption.Find(LogicOptions.Severities, condition.Severity) ?? LogicOptions.Severities[0];
        NextStepText = condition.NextStepText ?? string.Empty;
        TagEntries = tagEntries ?? Array.Empty<LogicTagEntryViewModel>();
        SelectedBlockTag = FindTagEntry(condition.Tag);
    }

    public Guid ConditionId { get; }

    public IReadOnlyList<LogicSelectOption> OpOptions => LogicOptions.Ops;

    public IReadOnlyList<LogicSelectOption> SeverityOptions => LogicOptions.Severities;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private LogicSelectOption _op;

    [ObservableProperty]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string _group = string.Empty;

    [ObservableProperty]
    private string _holdSeconds = string.Empty;

    [ObservableProperty]
    private LogicSelectOption _severity;

    [ObservableProperty]
    private string _nextStepText = string.Empty;

    [ObservableProperty]
    private string _liveText = "—";

    [ObservableProperty]
    private bool _isLiveOk;

    [ObservableProperty]
    private bool _isLiveBad;

    [ObservableProperty]
    private bool _isLiveWarn = true;

    /// <summary>
    /// The direct pair the row was stored with, "" when it is not a legacy row. It is not edited
    /// here any more; it is carried through a save while the area's tags do not hold it yet.
    /// </summary>
    public string SourceId => storedSourceId_;

    /// <summary>The stored item id of a legacy row, "" when it reads a named tag.</summary>
    public string ItemId => storedItemId_;

    /// <summary>The area's named tags this row may read — the ones the Tags tab assigns.</summary>
    public IEnumerable<LogicTagEntryViewModel> TagEntries { get; }

    [ObservableProperty]
    private LogicTagEntryViewModel? _selectedBlockTag;

    /// <summary>The stored area-tags reference, "" while the row carries no name.</summary>
    public string Tag => SelectedBlockTag?.Name.Trim() ?? string.Empty;

    public bool HasBlockTag => Tag.Length > 0;

    /// <summary>A legacy row waiting for the area's tags to hold it: shown, never edited here.</summary>
    public bool HasStoredTag => !HasBlockTag && (SourceId.Length > 0 || ItemId.Length > 0);

    /// <summary>The tag a legacy row reads, while the area's list has no entry for it.</summary>
    public string StoredTagLabel => catalog_.TagLabel(SourceId, ItemId);

    /// <summary>The note the row's info mark carries: what that direct tag needs.</summary>
    public string StoredTagText => LogicText.StoredTagHint(StoredTagLabel);

    partial void OnSelectedBlockTagChanged(LogicTagEntryViewModel? value)
    {
        OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(HasBlockTag));
        OnPropertyChanged(nameof(HasStoredTag));
        ClearBlockTagCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearBlockTag))]
    private void ClearBlockTag() => SelectedBlockTag = null;

    private bool CanClearBlockTag() => CanEdit && HasBlockTag;

    private LogicTagEntryViewModel? FindTagEntry(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : TagEntries.FirstOrDefault(entry =>
                string.Equals(entry.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True for the numeric operators — the value box is only meaningful then.</summary>
    public bool NeedsValue => LogicConditionOps.RequiresValue(Op?.Value);

    partial void OnOpChanged(LogicSelectOption value) => OnPropertyChanged(nameof(NeedsValue));

    /// <summary>Applies the evaluated state to the live chip (1 / 0 / no data, or the live value).</summary>
    public void ApplyState(LogicElementStateDto? state)
    {
        LiveText = LogicText.LiveChip(state, Op?.Value);
        string key = LogicText.ConditionStateKey(state?.State);
        IsLiveOk = key == "ok";
        IsLiveBad = key == "bad";
        IsLiveWarn = key == "warn";
    }

    [ObservableProperty]
    private bool _canEdit = true;

    partial void OnCanEditChanged(bool value)
    {
        RemoveCommand.NotifyCanExecuteChanged();
        ClearBlockTagCommand.NotifyCanExecuteChanged();
        NotifyOrderChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRow))]
    private void Remove() => onRemove_?.Invoke(this);

    private bool CanRemoveRow() => CanEdit;

    /// <summary>A fresh condition above this row.</summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertAbove() => rowList_?.InsertBeside(this, above: true);

    /// <summary>A fresh condition below this row.</summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertBelow() => rowList_?.InsertBeside(this, above: false);

    [RelayCommand(CanExecute = nameof(CanMoveRowUp))]
    private void MoveUp() => rowList_?.MoveUp(this);

    [RelayCommand(CanExecute = nameof(CanMoveRowDown))]
    private void MoveDown() => rowList_?.MoveDown(this);

    private bool CanInsert() => CanEdit && rowList_?.CanInsert(this) == true;

    private bool CanMoveRowUp() => CanEdit && rowList_?.CanMoveUp(this) == true;

    private bool CanMoveRowDown() => CanEdit && rowList_?.CanMoveDown(this) == true;

    /// <summary>Keeps the row's order buttons in step with its place in the list.</summary>
    public void NotifyOrderChanged()
    {
        InsertAboveCommand.NotifyCanExecuteChanged();
        InsertBelowCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    internal static string FormatNumber(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}
