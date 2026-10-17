using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One ordered step of a sequence block: its name, its next-step text, an optional completion
/// handshake tag and its own condition rows. The handshake reads one of the area's named tags
/// (the Tags tab's entries, where the tag and its source are assigned); a stored direct pair is
/// carried through a save until the area's list holds it. The editor writes the conditions form;
/// step action buttons are not authored here and are carried through the save untouched.
/// </summary>
public partial class LogicStepRowViewModel : ObservableObject, ILogicOrderRow
{
    private readonly LogicTagCatalog catalog_;
    private readonly LogicRowList<LogicStepRowViewModel>? rowList_;
    private readonly LogicRowList<LogicConditionRowViewModel> conditionRows_;
    private readonly Action<LogicStepRowViewModel>? onRemove_;
    private readonly string? storedCompletionSourceId_;
    private readonly string? storedCompletionItemId_;

    public LogicStepRowViewModel(
        LogicStepDto step,
        LogicTagCatalog catalog,
        LogicRowList<LogicStepRowViewModel>? rowList = null,
        Action<LogicStepRowViewModel>? onRemove = null,
        IEnumerable<LogicTagEntryViewModel>? tagEntries = null)
    {
        catalog_ = catalog;
        rowList_ = rowList;
        onRemove_ = onRemove;
        conditionRows_ = new LogicRowList<LogicConditionRowViewModel>(Conditions, _ => NewConditionRow());
        storedCompletionSourceId_ = step.CompletionSourceId;
        storedCompletionItemId_ = step.CompletionItemId;
        StepId = step.Id == Guid.Empty ? Guid.NewGuid() : step.Id;
        StoredActions = step.Actions.ToList();
        Name = step.Name;
        NextStepText = step.NextStepText ?? string.Empty;
        TagEntries = tagEntries ?? Array.Empty<LogicTagEntryViewModel>();
        SelectedCompletionTag = FindTagEntry(step.CompletionTag);
        foreach (LogicConditionDto condition in step.Conditions)
        {
            Conditions.Add(NewConditionRow(condition));
        }
    }

    /// <summary>The area's named tags the step's conditions and handshake may read.</summary>
    public IEnumerable<LogicTagEntryViewModel> TagEntries { get; }

    [ObservableProperty]
    private LogicTagEntryViewModel? _selectedCompletionTag;

    /// <summary>The stored handshake reference, "" when the step has no named handshake.</summary>
    public string CompletionTagName => SelectedCompletionTag?.Name.Trim() ?? string.Empty;

    public bool HasCompletionTag => CompletionTagName.Length > 0;

    /// <summary>A legacy handshake waiting for the area's tags to hold it: shown, never edited here.</summary>
    public bool HasStoredTag =>
        !HasCompletionTag && (!string.IsNullOrWhiteSpace(CompletionSourceId) || !string.IsNullOrWhiteSpace(CompletionItemId));

    /// <summary>The tag a legacy handshake waits on, while the area's list has no entry for it.</summary>
    public string StoredTagLabel =>
        catalog_.TagLabel(CompletionSourceId ?? string.Empty, CompletionItemId ?? string.Empty);

    /// <summary>The note the row's info mark carries: what that direct tag needs.</summary>
    public string StoredTagText => LogicText.StoredTagHint(StoredTagLabel);

    partial void OnSelectedCompletionTagChanged(LogicTagEntryViewModel? value)
    {
        OnPropertyChanged(nameof(CompletionTagName));
        OnPropertyChanged(nameof(HasCompletionTag));
        OnPropertyChanged(nameof(HasStoredTag));
        ClearCompletionTagCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearCompletionTag))]
    private void ClearCompletionTag() => SelectedCompletionTag = null;

    private bool CanClearCompletionTag() => CanEdit && HasCompletionTag;

    private LogicTagEntryViewModel? FindTagEntry(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : TagEntries.FirstOrDefault(entry =>
                string.Equals(entry.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    public Guid StepId { get; }

    /// <summary>Action buttons stored on the step; preserved verbatim through a save.</summary>
    public IReadOnlyList<LogicActionDto> StoredActions { get; }

    public ObservableCollection<LogicConditionRowViewModel> Conditions { get; } = new();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _nextStepText = string.Empty;

    /// <summary>"STEP 1" — renumbered by the panel whenever the order changes.</summary>
    [ObservableProperty]
    private string _stepLabel = string.Empty;

    [ObservableProperty]
    private string _stateWord = "No data";

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isPending = true;

    [ObservableProperty]
    private bool _isNoData;

    [ObservableProperty]
    private string _reason = string.Empty;

    public bool HasReason => Reason.Length > 0;

    /// <summary>
    /// The direct handshake pair the step was stored with, or null when it is not a legacy row.
    /// It is not edited here any more; it is carried through a save while the area's tags do not
    /// hold it yet.
    /// </summary>
    public string? CompletionSourceId => storedCompletionSourceId_;

    public string? CompletionItemId => storedCompletionItemId_;

    partial void OnReasonChanged(string value) => OnPropertyChanged(nameof(HasReason));

    public void ApplyState(LogicStepStateDto? state)
    {
        StateWord = LogicText.StepStateWord(state?.State);
        IsDone = string.Equals(state?.State, LogicStepStates.Done, StringComparison.OrdinalIgnoreCase);
        IsCurrent = string.Equals(state?.State, LogicStepStates.Current, StringComparison.OrdinalIgnoreCase);
        IsPending = string.Equals(state?.State, LogicStepStates.Pending, StringComparison.OrdinalIgnoreCase);
        IsNoData = !IsDone && !IsCurrent && !IsPending;
        Reason = state?.Reason ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddCondition()
    {
        Conditions.Add(NewConditionRow());
        conditionRows_.NotifyChanged();
    }

    /// <summary>Removes one of the step's condition rows (also the remove callback the rows carry).</summary>
    public void RemoveCondition(LogicConditionRowViewModel row)
    {
        Conditions.Remove(row);
        conditionRows_.NotifyChanged();
    }

    [ObservableProperty]
    private bool _canEdit = true;

    partial void OnCanEditChanged(bool value)
    {
        RemoveStepCommand.NotifyCanExecuteChanged();
        AddConditionCommand.NotifyCanExecuteChanged();
        ClearCompletionTagCommand.NotifyCanExecuteChanged();
        NotifyOrderChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRow))]
    private void RemoveStep() => onRemove_?.Invoke(this);

    private bool CanRemoveRow() => CanEdit;

    /// <summary>A fresh step above this one.</summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertAbove() => rowList_?.InsertBeside(this, above: true);

    /// <summary>A fresh step below this one.</summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertBelow() => rowList_?.InsertBeside(this, above: false);

    [RelayCommand(CanExecute = nameof(CanMoveRowUp))]
    private void MoveUp() => rowList_?.MoveUp(this);

    [RelayCommand(CanExecute = nameof(CanMoveRowDown))]
    private void MoveDown() => rowList_?.MoveDown(this);

    private bool CanInsert() => CanEdit && rowList_?.CanInsert(this) == true;

    private bool CanMoveRowUp() => CanEdit && rowList_?.CanMoveUp(this) == true;

    private bool CanMoveRowDown() => CanEdit && rowList_?.CanMoveDown(this) == true;

    /// <summary>Keeps the step's insert / move buttons in step with its place in the list.</summary>
    public void NotifyOrderChanged()
    {
        InsertAboveCommand.NotifyCanExecuteChanged();
        InsertBelowCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private LogicConditionRowViewModel NewConditionRow(LogicConditionDto condition) =>
        new(condition, catalog_, RemoveCondition, TagEntries, conditionRows_);

    private LogicConditionRowViewModel NewConditionRow() =>
        NewConditionRow(new LogicConditionDto
        {
            Id = Guid.NewGuid(),
            Op = LogicConditionOps.On,
            Severity = LogicConditionSeverities.Block
        });
}
