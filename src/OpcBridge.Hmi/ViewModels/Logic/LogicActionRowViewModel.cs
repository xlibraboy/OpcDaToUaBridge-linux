using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One block-level action button (label, tag, value, confirm). The phone renders these as
/// operator buttons; the HMI page authors them only — the write itself stays on the phone.
/// The button writes one of its area's named tags (the Tags tab's entries, where the tag and
/// its source are assigned); the Tags tab marks an entry whose tag cannot be written. A stored
/// direct (source, tag) pair is carried through a save until the area's list holds it.
/// </summary>
public partial class LogicActionRowViewModel : ObservableObject, ILogicOrderRow
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicActionRowViewModel>? onRemove_;
    private readonly LogicRowList<LogicActionRowViewModel>? rowList_;
    private readonly string storedSourceId_;
    private readonly string storedItemId_;

    public LogicActionRowViewModel(
        LogicActionDto action,
        LogicTagCatalog catalog,
        Action<LogicActionRowViewModel>? onRemove = null,
        IEnumerable<LogicTagEntryViewModel>? tagEntries = null,
        LogicRowList<LogicActionRowViewModel>? rowList = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
        rowList_ = rowList;
        storedSourceId_ = action.SourceId ?? string.Empty;
        storedItemId_ = action.ItemId ?? string.Empty;
        Label = action.Label;
        Value = action.Value;
        Confirm = action.Confirm;
        TagEntries = tagEntries ?? Array.Empty<LogicTagEntryViewModel>();
        SelectedBlockTag = FindTagEntry(action.Tag);
    }

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _confirm = true;

    /// <summary>
    /// The direct pair the button was stored with, "" when it is not a legacy row. It is not
    /// edited here any more; it is carried through a save while the area's tags do not hold it yet.
    /// </summary>
    public string SourceId => storedSourceId_;

    public string ItemId => storedItemId_;

    /// <summary>The area's named tags this button may write — the ones the Tags tab assigns.</summary>
    public IEnumerable<LogicTagEntryViewModel> TagEntries { get; }

    [ObservableProperty]
    private LogicTagEntryViewModel? _selectedBlockTag;

    /// <summary>The stored area-tags reference, "" while the button carries no name.</summary>
    public string Tag => SelectedBlockTag?.Name.Trim() ?? string.Empty;

    public bool HasBlockTag => Tag.Length > 0;

    /// <summary>A legacy button waiting for the area's tags to hold it: shown, never edited here.</summary>
    public bool HasStoredTag => !HasBlockTag && (SourceId.Length > 0 || ItemId.Length > 0);

    /// <summary>The tag a legacy button writes, while the area's list has no entry for it.</summary>
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

    /// <summary>A fresh action above this row.</summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertAbove() => rowList_?.InsertBeside(this, above: true);

    /// <summary>A fresh action below this row.</summary>
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
}
