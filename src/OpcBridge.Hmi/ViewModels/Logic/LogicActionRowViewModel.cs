using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One block-level action button (label, tag, value, confirm). The phone renders these as
/// operator buttons; the HMI page authors them only — the write itself stays on the phone.
/// Only writeable tags are offered.
/// </summary>
public partial class LogicActionRowViewModel : ObservableObject, ILogicOrderRow
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicActionRowViewModel>? onRemove_;
    private readonly LogicRowList<LogicActionRowViewModel>? rowList_;
    private bool loading_;

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
        Label = action.Label;
        Value = action.Value;
        Confirm = action.Confirm;
        TagEntries = tagEntries ?? Array.Empty<LogicTagEntryViewModel>();
        SelectedBlockTag = FindTagEntry(action.Tag);
        LoadSourceAndItems(action.SourceId, action.ItemId);
    }

    public ObservableCollection<LogicSourceOption> Sources { get; } = new();

    public ObservableCollection<LogicTagOption> Items { get; } = new();

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _confirm = true;

    [ObservableProperty]
    private LogicSourceOption? _selectedSource;

    [ObservableProperty]
    private LogicTagOption? _selectedTag;

    public string SourceId => SelectedSource?.SourceId ?? string.Empty;

    public string ItemId => SelectedTag?.ItemId ?? string.Empty;

    /// <summary>The area's named tags this button may write instead of a direct tag.</summary>
    public IEnumerable<LogicTagEntryViewModel> TagEntries { get; }

    [ObservableProperty]
    private LogicTagEntryViewModel? _selectedBlockTag;

    /// <summary>The stored area-tags reference, "" when the button writes a direct tag.</summary>
    public string Tag => SelectedBlockTag?.Name.Trim() ?? string.Empty;

    public bool HasBlockTag => Tag.Length > 0;

    /// <summary>The direct source/tag pickers: hidden while a named tag is selected.</summary>
    public bool ShowsDirectTag => !HasBlockTag;

    partial void OnSelectedBlockTagChanged(LogicTagEntryViewModel? value)
    {
        OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(HasBlockTag));
        OnPropertyChanged(nameof(ShowsDirectTag));
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

    partial void OnSelectedSourceChanged(LogicSourceOption? value)
    {
        if (!loading_)
        {
            RebuildItems(string.Empty);
        }
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

    private void LoadSourceAndItems(string sourceId, string itemId)
    {
        loading_ = true;
        try
        {
            Sources.Clear();
            foreach (LogicSourceOption source in catalog_.Sources)
            {
                Sources.Add(source);
            }

            if (sourceId.Length > 0
                && !Sources.Any(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)))
            {
                Sources.Add(new LogicSourceOption(sourceId, sourceId));
            }

            SelectedSource = Sources.FirstOrDefault(
                source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            loading_ = false;
        }

        RebuildItems(itemId);
    }

    private void RebuildItems(string keepItemId)
    {
        string sourceId = SelectedSource?.SourceId ?? string.Empty;
        Items.Clear();
        foreach (LogicTagOption item in catalog_.ItemsFor(sourceId, writeableOnly: true))
        {
            Items.Add(item);
        }

        LogicTagOption? match = keepItemId.Length == 0
            ? null
            : Items.FirstOrDefault(item => string.Equals(item.ItemId, keepItemId, StringComparison.OrdinalIgnoreCase));
        if (match is null && keepItemId.Length > 0)
        {
            match = new LogicTagOption(sourceId, keepItemId, keepItemId, SelectedSource?.Name ?? sourceId, Writeable: true);
            Items.Insert(0, match);
        }

        SelectedTag = match;
    }
}
