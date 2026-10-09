using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One element of a block's IEC 61131-3 network on the detail screen: a contact, a gate or a
/// function block, with its live state. A contact carries the reading it needs against the live
/// one ("should 1 · actual 0"); a timer or counter carries its progress.
/// </summary>
public sealed partial class LogicElementRowViewModel : ObservableObject
{
    private readonly LogicElementDto element_;
    private string lastState_ = LogicConditionStates.Unknown;
    private bool? lastMatches_;
    private string should_text_ = string.Empty;

    [ObservableProperty] private string _mark = "—";
    [ObservableProperty] private string _stateKey = LogicConditionStates.Unknown;
    [ObservableProperty] private string _actualText = "—";
    [ObservableProperty] private string _valueText = string.Empty;
    [ObservableProperty] private bool _showNextStep;

    /// <summary>"should 1 · actual 0" — the reading a contact expects against the live one.</summary>
    [ObservableProperty] private string _shouldLine = string.Empty;

    /// <summary>True for a contact whose requirement can be stated.</summary>
    [ObservableProperty] private bool _showShould;

    /// <summary>True while the live reading does not satisfy a contact.</summary>
    [ObservableProperty] private bool _mismatch;

    /// <summary>State key the should/actual line colours by: "true" matched, "false" mismatched.</summary>
    [ObservableProperty] private string _shouldKey = LogicConditionStates.Unknown;

    /// <summary>"holding 1.5 s of 3 s" / "count 2 of 5" — a timer's or counter's progress.</summary>
    [ObservableProperty] private string _progressText = string.Empty;

    [ObservableProperty] private bool _showProgress;

    public LogicElementRowViewModel(LogicElementDto element, int depth, string tagLabel)
    {
        element_ = element;
        Depth = depth;
        TagLabel = tagLabel;
        NextStepText = element.NextStepText;

        string kind = element.Kind.ToLowerInvariant();
        IsContact = kind == LogicElementKinds.Contact;
        isBoolean_ = IsContact && element.Op is LogicConditionOps.On or LogicConditionOps.Off;
        KindLabel = DescribeKind(element, kind);
    }

    private bool isBoolean_;

    public Guid Id => element_.Id;

    /// <summary>Depth in the network (0 = a root) — the row indents by it.</summary>
    public int Depth { get; }

    /// <summary>Pixels to indent by, so the tree reads without extra glyphs.</summary>
    public double IndentWidth => Depth * 14;

    /// <summary>True for a contact / comparison; false for a gate or function block.</summary>
    public bool IsContact { get; }

    /// <summary>"NO" / "NC" / "&gt; 50" for a contact; "AND", "TON IN", "CTU CU" for a block.</summary>
    public string KindLabel { get; }

    /// <summary>The element's sentence (contacts) or label (gates and blocks).</summary>
    public string Text => string.IsNullOrWhiteSpace(element_.Text) ? KindLabel : element_.Text;

    /// <summary>"Pressure switch PS1 · sim" — what a contact reads; empty for a gate or block.</summary>
    public string TagLabel { get; }

    /// <summary>The "what to do when not true" hint, highlighted while the element is not true.</summary>
    public string? NextStepText { get; }

    public bool Blocks => !string.Equals(element_.Severity, LogicConditionSeverities.Warn, StringComparison.OrdinalIgnoreCase);

    public string SeverityLabel => Blocks ? string.Empty : "warn only";

    public void ApplyState(LogicElementStateDto? state)
    {
        string value = state?.State ?? LogicConditionStates.Unknown;
        lastState_ = value;
        lastMatches_ = state?.Matches;
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

        ShouldLine = state?.Should is { Length: > 0 } should ? should : string.Empty;
        should_text_ = ShouldLine;
        Mismatch = state?.Matches == false;
        ShouldKey = state?.Matches switch
        {
            true => LogicConditionStates.True,
            false => LogicConditionStates.False,
            null => LogicConditionStates.Unknown
        };

        ApplyProgress(state);
        UpdateActualText();
        UpdateShouldLine();
    }

    /// <summary>
    /// Live value straight from the tag cache (updated on every value delta). A boolean
    /// contact reads as <c>1</c>/<c>0</c>; a numeric one keeps its value.
    /// </summary>
    public void ApplyTag(MultiBridgeTagEntry? entry)
    {
        if (entry is null || !IsContact)
        {
            return;
        }

        isBoolean_ = entry.Digital;
        ApplyValueText(LogicTagText.Format(entry));
    }

    public void ApplyValueText(string valueText)
    {
        if (!string.IsNullOrWhiteSpace(valueText))
        {
            ValueText = valueText;
        }

        UpdateActualText();
        UpdateShouldLine();
    }

    private void ApplyProgress(LogicElementStateDto? state)
    {
        string kind = element_.Kind.ToLowerInvariant();
        if (state is null)
        {
            ShowProgress = false;
            ProgressText = string.Empty;
            return;
        }

        switch (kind)
        {
            case LogicElementKinds.Ton:
            case LogicElementKinds.Tof:
            case LogicElementKinds.Tp:
            {
                ShowProgress = state.PtMs > 0;
                string verb = kind switch
                {
                    LogicElementKinds.Tof => "off-delay",
                    LogicElementKinds.Tp => "pulse",
                    _ => "holding"
                };
                ProgressText = !ShowProgress
                    ? string.Empty
                    : lastState_ == LogicConditionStates.True
                        ? verb + " done · " + FormatSeconds(state.PtMs)
                        : verb + " " + FormatSeconds(state.ElapsedMs) + " of " + FormatSeconds(state.PtMs);
                break;
            }

            case LogicElementKinds.Ctu:
            case LogicElementKinds.Ctd:
                ShowProgress = true;
                ProgressText = "count " + state.Count + " of " + element_.Pv;
                break;

            default:
                ShowProgress = false;
                ProgressText = string.Empty;
                break;
        }
    }

    /// <summary>
    /// What the element reads: the live bit for a contact, the output bit for a gate or block,
    /// the value text for a numeric comparison, and "?" whenever it is unknown.
    /// </summary>
    private void UpdateActualText()
    {
        if (isBoolean_)
        {
            bool? raw = lastMatches_ switch
            {
                null => null,
                bool matches => element_.Op == LogicConditionOps.Off ? !matches : matches
            };
            ActualText = raw switch { true => "1", false => "0", null => "?" };
            return;
        }

        if (IsContact)
        {
            ActualText = string.IsNullOrWhiteSpace(ValueText) || ValueText == "—" ? "?" : ValueText;
            return;
        }

        ActualText = lastState_ switch
        {
            LogicConditionStates.True => "1",
            LogicConditionStates.False => "0",
            _ => "—"
        };
    }

    private void UpdateShouldLine()
    {
        ShowShould = should_text_.Length > 0;
        ShouldLine = ShowShould ? "should " + should_text_ + " · actual " + ActualText : string.Empty;
    }

    private static string FormatSeconds(int milliseconds) =>
        (milliseconds / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " s";

    /// <summary>The element's short name: the contact form, or the block with its first input.</summary>
    private static string DescribeKind(LogicElementDto element, string kind) => kind switch
    {
        LogicElementKinds.Contact => element.Op.ToLowerInvariant() switch
        {
            LogicConditionOps.On => "NO",
            LogicConditionOps.Off => "NC",
            LogicConditionOps.GreaterThan => ">",
            LogicConditionOps.LessThan => "<",
            LogicConditionOps.Equal => "=",
            _ => "contact"
        },
        LogicElementKinds.And => "AND",
        LogicElementKinds.Or => "OR",
        LogicElementKinds.Xor => "XOR",
        LogicElementKinds.Not => "NOT",
        LogicElementKinds.Ton => "TON IN",
        LogicElementKinds.Tof => "TOF IN",
        LogicElementKinds.Tp => "TP IN",
        LogicElementKinds.Ctu => "CTU CU",
        LogicElementKinds.Ctd => "CTD CD",
        LogicElementKinds.Sr => "SR S1",
        LogicElementKinds.Rs => "RS S",
        LogicElementKinds.RisingEdge => "R_TRIG IN",
        LogicElementKinds.FallingEdge => "F_TRIG IN",
        _ => "element"
    };
}

/// <summary>One ordered step of a sequence, with the element rows of its own network.</summary>
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

    public ObservableCollection<LogicElementRowViewModel> Elements { get; } = new();

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
/// The block detail screen: the block's IEC network (or the ordered step flow), action buttons
/// and notes, all updated in place from the pushed logic snapshot and value deltas.
/// </summary>
public sealed partial class LogicBlockDetailViewModel : ObservableObject
{
    private readonly LogicBlockDto block_;
    private readonly string bridgeKey_;
    private readonly MultiBridgeTagCache? tags_;
    private readonly Dictionary<Guid, LogicElementRowViewModel> elementRows_ = new();
    private readonly Dictionary<Guid, LogicStepRowViewModel> stepRows_ = new();

    [ObservableProperty] private string _stateLabel = "no data";
    [ObservableProperty] private string _stateKey = LogicBlockStates.Unknown;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private string _blockedByText = string.Empty;

    public LogicBlockDetailViewModel(
        LogicBlockDto block,
        string bridgeKey,
        LogicBlockStateDto? state = null,
        MultiBridgeTagCache? tags = null,
        IReadOnlyList<LogicNoteDto>? notes = null)
    {
        block_ = block;
        bridgeKey_ = bridgeKey;
        tags_ = tags;

        Title = block.Name;
        Description = block.Description;
        IsSequence = string.Equals(block.Kind, LogicBlockKinds.Sequence, StringComparison.OrdinalIgnoreCase);
        KindLabel = IsSequence
            ? "Sequence"
            : string.Equals(block.Kind, LogicBlockKinds.Permissive, StringComparison.OrdinalIgnoreCase) ? "Permissive" : "Interlock";

        foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(LogicNetwork.For(block)))
        {
            Elements.Add(CreateRow(element, depth));
        }

        int number = 0;
        foreach (LogicStepDto step in block.Steps)
        {
            number++;
            LogicStepRowViewModel row = new(step, number);
            stepRows_[step.Id] = row;
            foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(LogicNetwork.For(step)))
            {
                row.Elements.Add(CreateRow(element, depth));
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

    /// <summary>Interlocks and permissives show the network instead of steps.</summary>
    public bool ShowNetwork => !IsSequence;

    /// <summary>The block's network, in drawing order — a parent row above its inputs.</summary>
    public ObservableCollection<LogicElementRowViewModel> Elements { get; } = new();

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
        BlockedByText = StateKey is LogicBlockStates.Blocked or LogicBlockStates.Unknown
            ? (string.IsNullOrWhiteSpace(Reason) ? string.Empty : "Blocked by: " + Reason)
            : string.Empty;

        Dictionary<Guid, LogicElementStateDto> elementStates = new();
        foreach (LogicElementStateDto element in state?.Elements ?? Enumerable.Empty<LogicElementStateDto>())
        {
            elementStates[element.Id] = element;
        }

        foreach ((Guid id, LogicElementRowViewModel row) in elementRows_)
        {
            row.ApplyState(elementStates.TryGetValue(id, out LogicElementStateDto? elementState) ? elementState : null);
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

    /// <summary>Live value text for every contact, straight from the tag cache.</summary>
    public void ApplyValues()
    {
        if (tags_ is null)
        {
            return;
        }

        foreach (LogicElementDto element in AllElements())
        {
            if (!string.Equals(element.Kind, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (elementRows_.TryGetValue(element.Id, out LogicElementRowViewModel? row) &&
                tags_.TryGet(TagBindingKey.Create(bridgeKey_, element.SourceId, element.ItemId), out MultiBridgeTagEntry? entry))
            {
                row.ApplyTag(entry);
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

    private LogicElementRowViewModel CreateRow(LogicElementDto element, int depth)
    {
        string label = string.Equals(element.Kind, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase)
            ? TagLabel(element.SourceId, element.ItemId)
            : string.Empty;
        LogicElementRowViewModel row = new(element, depth, label);
        elementRows_[element.Id] = row;
        return row;
    }

    private string TagLabel(string sourceId, string itemId)
    {
        if (tags_ is not null &&
            tags_.TryGet(TagBindingKey.Create(bridgeKey_, sourceId, itemId), out MultiBridgeTagEntry? entry) &&
            entry is not null)
        {
            return $"{entry.DisplayName} · {entry.SourceName}";
        }

        return string.IsNullOrWhiteSpace(sourceId) ? itemId : $"{itemId} · {sourceId}";
    }

    private IEnumerable<LogicElementDto> AllElements()
    {
        foreach ((LogicElementDto element, int _) in LogicNetwork.Walk(LogicNetwork.For(block_)))
        {
            yield return element;
        }

        foreach (LogicStepDto step in block_.Steps)
        {
            foreach ((LogicElementDto element, int _) in LogicNetwork.Walk(LogicNetwork.For(step)))
            {
                yield return element;
            }
        }
    }
}
