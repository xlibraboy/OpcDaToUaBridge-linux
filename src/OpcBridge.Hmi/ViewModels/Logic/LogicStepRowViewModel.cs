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
public partial class LogicStepRowViewModel : ObservableObject
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicStepRowViewModel>? onRemove_;
    private readonly Action<LogicStepRowViewModel>? onMoveUp_;
    private readonly Action<LogicStepRowViewModel>? onMoveDown_;
    private bool loadingCompletion_;

    public LogicStepRowViewModel(
        LogicStepDto step,
        LogicTagCatalog catalog,
        Action<LogicStepRowViewModel>? onRemove = null,
        Action<LogicStepRowViewModel>? onMoveUp = null,
        Action<LogicStepRowViewModel>? onMoveDown = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
        onMoveUp_ = onMoveUp;
        onMoveDown_ = onMoveDown;
        StepId = step.Id == Guid.Empty ? Guid.NewGuid() : step.Id;
        StoredActions = step.Actions.ToList();
        Name = step.Name;
        NextStepText = step.NextStepText ?? string.Empty;
        LoadCompletion(step.CompletionSourceId ?? string.Empty, step.CompletionItemId ?? string.Empty);
        foreach (LogicConditionDto condition in step.Conditions)
        {
            Conditions.Add(new LogicConditionRowViewModel(condition, catalog, RemoveCondition));
        }
    }

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

    [RelayCommand]
    private void AddCondition() =>
        Conditions.Add(new LogicConditionRowViewModel(
            new LogicConditionDto { Id = Guid.NewGuid(), Op = LogicConditionOps.On, Severity = LogicConditionSeverities.Block },
            catalog_,
            RemoveCondition));

    /// <summary>Removes one of the step's condition rows (also the remove callback the rows carry).</summary>
    public void RemoveCondition(LogicConditionRowViewModel row) => Conditions.Remove(row);

    [RelayCommand]
    private void RemoveStep() => onRemove_?.Invoke(this);

    [RelayCommand]
    private void MoveUp() => onMoveUp_?.Invoke(this);

    [RelayCommand]
    private void MoveDown() => onMoveDown_?.Invoke(this);

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
