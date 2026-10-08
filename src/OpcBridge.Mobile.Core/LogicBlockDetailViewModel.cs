using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>One condition on the block detail screen, with its live state and value.</summary>
public sealed partial class LogicConditionRowViewModel : ObservableObject
{
    private readonly LogicConditionDto condition_;

    [ObservableProperty] private string _mark = "—";
    [ObservableProperty] private string _stateKey = LogicConditionStates.Unknown;
    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private bool _showNextStep;

    public LogicConditionRowViewModel(LogicConditionDto condition, string tagLabel)
    {
        condition_ = condition;
        TagLabel = tagLabel;
        NextStepText = condition.NextStepText;
    }

    public Guid Id => condition_.Id;

    public string Text => condition_.Text;

    /// <summary>"Line 01 Start Permit · sim" — what the condition reads.</summary>
    public string TagLabel { get; }

    /// <summary>The "what to do when not true" hint, highlighted while the condition is not true.</summary>
    public string? NextStepText { get; }

    public bool Blocks => !string.Equals(condition_.Severity, LogicConditionSeverities.Warn, StringComparison.OrdinalIgnoreCase);

    public string SeverityLabel => Blocks ? string.Empty : "warn only";

    public void ApplyState(LogicConditionStateDto? state)
    {
        string value = state?.State ?? LogicConditionStates.Unknown;
        StateKey = value;
        Mark = value switch
        {
            LogicConditionStates.True => "✓",
            LogicConditionStates.False => "✗",
            _ => "—"
        };
        ShowNextStep = !string.IsNullOrWhiteSpace(NextStepText) && value != LogicConditionStates.True;
        if (!string.IsNullOrWhiteSpace(state?.ValueText))
        {
            ValueText = state!.ValueText;
        }
    }

    /// <summary>Live text straight from the tag cache (updated on every value delta).</summary>
    public void ApplyValueText(string valueText)
    {
        if (!string.IsNullOrWhiteSpace(valueText))
        {
            ValueText = valueText;
        }
    }
}

/// <summary>One ordered step of a sequence.</summary>
public sealed partial class LogicStepRowViewModel : ObservableObject
{
    [ObservableProperty] private string _stateLabel = "Pending";
    [ObservableProperty] private string _stateKey = LogicStepStates.Pending;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private bool _isCurrent;

    public LogicStepRowViewModel(LogicStepDto step, int number)
    {
        Id = step.Id;
        Number = number;
        Name = step.Name;
        NextStepText = step.NextStepText;
    }

    public Guid Id { get; }

    public int Number { get; }

    public string Name { get; }

    public string? NextStepText { get; }

    public ObservableCollection<LogicConditionRowViewModel> Conditions { get; } = new();

    public void ApplyState(LogicStepStateDto? state)
    {
        string value = state?.State ?? LogicStepStates.Pending;
        StateKey = value;
        StateLabel = value switch
        {
            LogicStepStates.Done => "Done",
            LogicStepStates.Current => "Current",
            LogicStepStates.Unknown => "No data",
            _ => "Pending"
        };
        Reason = state?.Reason;
    }
}

/// <summary>An action button on the block or a step (label, target tag, confirm flag).</summary>
public sealed class LogicActionButtonViewModel
{
    public LogicActionButtonViewModel(LogicActionDto action, string tagLabel)
    {
        Action = action;
        TagLabel = tagLabel;
    }

    public LogicActionDto Action { get; }

    public string Label => Action.Label;

    public string TagLabel { get; }

    public bool Confirm => Action.Confirm;
}

/// <summary>One operator note on the block.</summary>
public sealed class LogicNoteRowViewModel
{
    public LogicNoteRowViewModel(LogicNoteDto note)
    {
        Text = note.Text;
        Author = note.Author;
        WhenText = note.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    public string Text { get; }

    public string Author { get; }

    public string WhenText { get; }

    /// <summary>"operator1 · 2026-10-08 12:30:05" for the note list.</summary>
    public string Meta => Author + " · " + WhenText;
}

/// <summary>
/// The block detail screen: conditions (or the ordered step flow), action buttons and notes,
/// all updated in place from the pushed logic snapshot and value deltas.
/// </summary>
public sealed partial class LogicBlockDetailViewModel : ObservableObject
{
    private readonly LogicBlockDto block_;
    private readonly MultiBridgeTagCache? tags_;
    private readonly Dictionary<Guid, LogicConditionRowViewModel> conditionRows_ = new();
    private readonly Dictionary<Guid, LogicStepRowViewModel> stepRows_ = new();

    [ObservableProperty] private string _stateLabel = "no data";
    [ObservableProperty] private string _stateKey = LogicBlockStates.Unknown;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private string _blockedByText = string.Empty;

    public LogicBlockDetailViewModel(
        LogicBlockDto block,
        LogicBlockStateDto? state = null,
        MultiBridgeTagCache? tags = null,
        IReadOnlyList<LogicNoteDto>? notes = null)
    {
        block_ = block;
        tags_ = tags;

        Title = block.Name;
        Description = block.Description;
        IsSequence = string.Equals(block.Kind, LogicBlockKinds.Sequence, StringComparison.OrdinalIgnoreCase);
        KindLabel = IsSequence
            ? "Sequence"
            : string.Equals(block.Kind, LogicBlockKinds.Permissive, StringComparison.OrdinalIgnoreCase) ? "Permissive" : "Interlock";

        foreach (LogicConditionDto condition in block.Conditions)
        {
            LogicConditionRowViewModel row = CreateRow(condition);
            Conditions.Add(row);
        }

        int number = 0;
        foreach (LogicStepDto step in block.Steps)
        {
            number++;
            LogicStepRowViewModel row = new(step, number);
            stepRows_[step.Id] = row;
            foreach (LogicConditionDto condition in step.Conditions)
            {
                string label = StepConditionLabel(step, condition);
                LogicConditionRowViewModel conditionRow = CreateRow(condition, label);
                conditionRows_[condition.Id] = conditionRow;
                row.Conditions.Add(conditionRow);
            }

            Steps.Add(row);
        }

        foreach (LogicActionDto action in block.Actions)
        {
            Actions.Add(new LogicActionButtonViewModel(action, TagLabel(action.SourceId, action.ItemId)));
        }

        foreach (LogicStepDto step in block.Steps)
        {
            foreach (LogicActionDto action in step.Actions)
            {
                Actions.Add(new LogicActionButtonViewModel(action, $"{step.Name}: {TagLabel(action.SourceId, action.ItemId)}"));
            }
        }

        if (notes is not null)
        {
            SetNotes(notes);
        }

        ApplyValues();
        ApplyState(state);
    }

    /// <summary>Raised when an action button is pressed; the page confirms and writes.</summary>
    public event Action<LogicActionButtonViewModel>? ActionRequested;

    public Guid Id => block_.Id;
    public string Title { get; }
    public string Description { get; }
    public string KindLabel { get; }
    public bool IsSequence { get; }

    /// <summary>Interlocks and permissives show the flat condition list instead of steps.</summary>
    public bool ShowConditions => !IsSequence;

    public ObservableCollection<LogicConditionRowViewModel> Conditions { get; } = new();
    public ObservableCollection<LogicStepRowViewModel> Steps { get; } = new();
    public ObservableCollection<LogicActionButtonViewModel> Actions { get; } = new();
    public ObservableCollection<LogicNoteRowViewModel> Notes { get; } = new();

    [RelayCommand]
    private void RunAction(LogicActionButtonViewModel? action)
    {
        if (action is not null)
        {
            ActionRequested?.Invoke(action);
        }
    }

    public void ApplyState(LogicBlockStateDto? state)
    {
        Guid? currentStepId = state?.Steps
            .FirstOrDefault(step => string.Equals(step.State, LogicStepStates.Current, StringComparison.OrdinalIgnoreCase)
                || string.Equals(step.State, LogicStepStates.Unknown, StringComparison.OrdinalIgnoreCase))?.Id;

        StateKey = state?.State ?? LogicBlockStates.Unknown;
        StateLabel = StateKey switch
        {
            LogicBlockStates.Ready => "READY",
            LogicBlockStates.Blocked => "BLOCKED",
            LogicBlockStates.Disabled => "DISABLED",
            _ => "NO DATA"
        };
        Reason = string.IsNullOrWhiteSpace(state?.Reason)
            ? (StateKey == LogicBlockStates.Ready ? "all conditions met" : null)
            : state!.Reason;
        BlockedByText = StateKey == LogicBlockStates.Blocked || StateKey == LogicBlockStates.Unknown
            ? (string.IsNullOrWhiteSpace(Reason) ? string.Empty : "Blocked by: " + Reason)
            : string.Empty;

        Dictionary<Guid, LogicConditionStateDto> conditionStates = new();
        foreach (LogicConditionStateDto condition in state?.Conditions ?? Enumerable.Empty<LogicConditionStateDto>())
        {
            conditionStates[condition.Id] = condition;
        }

        foreach ((Guid id, LogicConditionRowViewModel row) in conditionRows_)
        {
            row.ApplyState(conditionStates.TryGetValue(id, out LogicConditionStateDto? conditionState) ? conditionState : null);
        }

        Dictionary<Guid, LogicStepStateDto> stepStates = new();
        foreach (LogicStepStateDto step in state?.Steps ?? Enumerable.Empty<LogicStepStateDto>())
        {
            stepStates[step.Id] = step;
        }

        foreach ((Guid id, LogicStepRowViewModel row) in stepRows_)
        {
            row.ApplyState(stepStates.TryGetValue(id, out LogicStepStateDto? stepState) ? stepState : null);
            row.IsCurrent = id == currentStepId;
        }
    }

    /// <summary>Live value text for every condition, straight from the tag cache.</summary>
    public void ApplyValues()
    {
        if (tags_ is null)
        {
            return;
        }

        foreach (LogicConditionDto condition in AllConditions())
        {
            if (conditionRows_.TryGetValue(condition.Id, out LogicConditionRowViewModel? row) &&
                tags_.TryGet(TagBindingKey.Create(MobileBridge.Id, condition.SourceId, condition.ItemId), out MultiBridgeTagEntry? entry))
            {
                row.ApplyValueText(LogicTagText.Format(entry));
            }
        }
    }

    public void SetNotes(IReadOnlyList<LogicNoteDto> notes)
    {
        Notes.Clear();
        foreach (LogicNoteDto note in notes)
        {
            Notes.Add(new LogicNoteRowViewModel(note));
        }
    }

    private LogicConditionRowViewModel CreateRow(LogicConditionDto condition, string? labelOverride = null)
    {
        LogicConditionRowViewModel row = new(condition, labelOverride ?? TagLabel(condition.SourceId, condition.ItemId));
        conditionRows_[condition.Id] = row;
        return row;
    }

    private string StepConditionLabel(LogicStepDto step, LogicConditionDto condition) =>
        $"{step.Name}: {TagLabel(condition.SourceId, condition.ItemId)}";

    private string TagLabel(string sourceId, string itemId)
    {
        if (tags_ is not null &&
            tags_.TryGet(TagBindingKey.Create(MobileBridge.Id, sourceId, itemId), out MultiBridgeTagEntry? entry) &&
            entry is not null)
        {
            return $"{entry.DisplayName} · {entry.SourceName}";
        }

        return string.IsNullOrWhiteSpace(sourceId) ? itemId : $"{itemId} · {sourceId}";
    }

    private IEnumerable<LogicConditionDto> AllConditions()
    {
        foreach (LogicConditionDto condition in block_.Conditions)
        {
            yield return condition;
        }

        foreach (LogicStepDto step in block_.Steps)
        {
            foreach (LogicConditionDto condition in step.Conditions)
            {
                yield return condition;
            }
        }
    }
}
