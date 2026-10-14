using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One ordered step of a sequence block: its name, its next-step text, an optional completion
/// handshake tag and its own condition rows. The editor writes the conditions form; step action
/// buttons are not authored here and are carried through the save untouched.
/// </summary>
public partial class LogicStepRowViewModel : ObservableObject, ILogicOrderRow
{
    private readonly LogicTagCatalog catalog_;
    private readonly LogicRowList<LogicStepRowViewModel>? rowList_;
    private readonly LogicRowList<LogicConditionRowViewModel> conditionRows_;
    private readonly Action<LogicStepRowViewModel>? onRemove_;
    private bool loadingCompletion_;

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
        StepId = step.Id == Guid.Empty ? Guid.NewGuid() : step.Id;
        StoredActions = step.Actions.ToList();
        Name = step.Name;
        NextStepText = step.NextStepText ?? string.Empty;
        TagEntries = tagEntries ?? Array.Empty<LogicTagEntryViewModel>();
        SelectedCompletionTag = FindTagEntry(step.CompletionTag);
        LoadCompletion(step.CompletionSourceId ?? string.Empty, step.CompletionItemId ?? string.Empty);
        foreach (LogicConditionDto condition in step.Conditions)
        {
            Conditions.Add(NewConditionRow(condition));
        }
    }

    /// <summary>The area's named tags the step's conditions and handshake may read.</summary>
    public IEnumerable<LogicTagEntryViewModel> TagEntries { get; }

    [ObservableProperty]
    private LogicTagEntryViewModel? _selectedCompletionTag;

    /// <summary>The stored handshake reference, "" when the step has a direct pair or none.</summary>
    public string CompletionTagName => SelectedCompletionTag?.Name.Trim() ?? string.Empty;

    public bool HasCompletionTag => CompletionTagName.Length > 0;

    /// <summary>The direct source/tag pickers: hidden while a named handshake is selected.</summary>
    public bool ShowsDirectCompletion => !HasCompletionTag;

    partial void OnSelectedCompletionTagChanged(LogicTagEntryViewModel? value)
    {
        OnPropertyChanged(nameof(CompletionTagName));
        OnPropertyChanged(nameof(HasCompletionTag));
        OnPropertyChanged(nameof(ShowsDirectCompletion));
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

    public ObservableCollection<LogicSourceOption> CompletionSources { get; } = new();

    public ObservableCollection<LogicTagOption> CompletionItems { get; } = new();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _nextStepText = string.Empty;

    /// <summary>"STEP 1" — renumbered by the panel whenever the order changes.</summary>
    [ObservableProperty]
    private string _stepLabel = string.Empty;

    [ObservableProperty]
    private LogicSourceOption? _completionSource;

    [ObservableProperty]
    private LogicTagOption? _completionTag;

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

    public string? CompletionSourceId =>
        string.IsNullOrWhiteSpace(CompletionSource?.SourceId) ? null : CompletionSource!.SourceId;

    public string? CompletionItemId =>
        string.IsNullOrWhiteSpace(CompletionTag?.ItemId) ? null : CompletionTag!.ItemId;

    partial void OnReasonChanged(string value) => OnPropertyChanged(nameof(HasReason));

    partial void OnCompletionSourceChanged(LogicSourceOption? value)
    {
        if (!loadingCompletion_)
        {
            RebuildCompletionItems(string.Empty);
        }
    }

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

    private void LoadCompletion(string sourceId, string itemId)
    {
        loadingCompletion_ = true;
        try
        {
            CompletionSources.Clear();
            foreach (LogicSourceOption source in catalog_.Sources)
            {
                CompletionSources.Add(source);
            }

            if (sourceId.Length > 0
                && !CompletionSources.Any(source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)))
            {
                CompletionSources.Add(new LogicSourceOption(sourceId, sourceId));
            }

            CompletionSource = CompletionSources.FirstOrDefault(
                source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            loadingCompletion_ = false;
        }

        RebuildCompletionItems(itemId);
    }

    private void RebuildCompletionItems(string keepItemId)
    {
        string sourceId = CompletionSource?.SourceId ?? string.Empty;
        CompletionItems.Clear();
        foreach (LogicTagOption item in catalog_.ItemsFor(sourceId))
        {
            CompletionItems.Add(item);
        }

        LogicTagOption? match = keepItemId.Length == 0
            ? null
            : CompletionItems.FirstOrDefault(item => string.Equals(item.ItemId, keepItemId, StringComparison.OrdinalIgnoreCase));
        if (match is null && keepItemId.Length > 0)
        {
            match = new LogicTagOption(sourceId, keepItemId, keepItemId, CompletionSource?.Name ?? sourceId, Writeable: true);
            CompletionItems.Insert(0, match);
        }

        CompletionTag = match;
    }
}
