using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One checkpoint of the block screen: a parent of the network — the gate or function block that
/// owns the checks below it — with its live state. The dot is all that is drawn; the parent's name
/// lives in <see cref="Description"/>, and tapping the dot jumps to its row.
/// </summary>
public sealed partial class LogicCheckpointViewModel : ObservableObject
{
    [ObservableProperty] private string _stateKey = LogicConditionStates.Unknown;
    [ObservableProperty] private string _stateWord = "no data";

    public LogicCheckpointViewModel(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; }

    /// <summary>The parent's sentence, or its kind when it has none ("root AND").</summary>
    public string Name { get; }

    /// <summary>"Name — not fulfilled. Go to this parent." — the dot's accessible label.</summary>
    public string Description => Name + " — " + StateWord + ". Go to this parent.";

    partial void OnStateWordChanged(string value) => OnPropertyChanged(nameof(Description));

    public void ApplyState(LogicElementStateDto? state)
    {
        StateKey = state?.State ?? LogicConditionStates.Unknown;
        StateWord = state?.State switch
        {
            LogicConditionStates.True => "fulfilled",
            LogicConditionStates.False => "not fulfilled",
            _ => "no data"
        };
    }
}

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

    public LogicElementRowViewModel(
        LogicElementDto element,
        int depth,
        string tagLabel,
        LogicTagReference? tagReference = null)
    {
        element_ = element;
        Depth = depth;
        TagLabel = tagLabel;
        TagReference = tagReference;
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

    /// <summary>The tag behind the row's label, or null for a relay/gate with no tag source.</summary>
    public LogicTagReference? TagReference { get; }

    /// <summary>True when the label opens the source/usage inspector.</summary>
    public bool HasTagReference => TagReference is not null;

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
        if (state is null)
        {
            // A row that reads a tag gets its value text from the tag cache in ApplyValues; any
            // other row (a coil, a relay contact) has only the evaluation to read from, so a
            // missing state must clear the text instead of leaving the last one on screen.
            if (!IsContact || TagReference is null)
            {
                ValueText = string.Empty;
            }
        }
        else if (!string.IsNullOrWhiteSpace(state.ValueText))
        {
            ValueText = state.ValueText;
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
        if (!IsContact)
        {
            return;
        }

        if (entry is null || entry.Value is null)
        {
            // No entry, or an entry whose reading was cleared with the link: the row reads
            // nothing instead of the value that was on screen when the bridge went away.
            ValueText = string.Empty;
            UpdateActualText();
            UpdateShouldLine();
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
    internal static string DescribeKind(LogicElementDto element, string kind) => kind switch
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
        LogicElementKinds.Coil => string.IsNullOrWhiteSpace(element.Relay) ? "COIL" : "COIL " + element.Relay,
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
        if (state is null)
        {
            // No evaluation at all (the link is down): "Pending" would read like a real step
            // state, so the row says it has no data.
            StateKey = LogicStepStates.Unknown;
            StateLabel = "No data";
            Reason = null;
            return;
        }

        StateKey = state.State;
        StateLabel = state.State switch
        {
            LogicStepStates.Done => "Done",
            LogicStepStates.Current => "Current",
            LogicStepStates.Unknown => "No data",
            _ => "Pending"
        };
        Reason = state.Reason;
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
    private readonly LogicAreaTagsDto? areaTags_;
    private readonly LogicTagUsageIndex usage_;
    private IReadOnlyDictionary<Guid, LogicBlockStateDto> usageStates_ = new Dictionary<Guid, LogicBlockStateDto>();
    private readonly Dictionary<Guid, LogicElementRowViewModel> elementRows_ = new();
    private readonly Dictionary<Guid, LogicStepRowViewModel> stepRows_ = new();

    [ObservableProperty] private string _stateLabel = "no data";
    [ObservableProperty] private string _stateKey = LogicBlockStates.Unknown;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private string _blockedByText = string.Empty;

    /// <summary>"1/2 steps · Open valve" — a sequence's progress in the screen's header.</summary>
    [ObservableProperty] private string _progressText = string.Empty;

    /// <summary>True while the block is a sequence with a progress line to show.</summary>
    public bool ShowProgress => ProgressText.Length > 0;

    partial void OnProgressTextChanged(string value) => OnPropertyChanged(nameof(ShowProgress));

    /// <summary>The output-tag feedback pill: shown while the block names an OUTPUT tag.</summary>
    [ObservableProperty] private bool _hasOutput;
    [ObservableProperty] private string _outputText = string.Empty;
    [ObservableProperty] private string _outputKey = LogicOutputStates.None;
    [ObservableProperty] private string _outputDescription = string.Empty;

    /// <summary>"2/3" — the met count of the checkpoint rail, coloured by <see cref="CheckpointSummaryKey"/>.</summary>
    [ObservableProperty] private string _checkpointSummary = string.Empty;
    [ObservableProperty] private string _checkpointSummaryKey = LogicConditionStates.Unknown;
    [ObservableProperty] private string _checkpointSummaryDescription = string.Empty;

    /// <summary>True while the row's tag inspector sheet is open over the screen.</summary>
    [ObservableProperty] private bool _isTagPanelOpen;

    /// <summary>The tag the inspector is showing; null while the sheet is closed.</summary>
    [ObservableProperty] private LogicTagInspectorViewModel? _selectedTag;

    public LogicBlockDetailViewModel(
        LogicBlockDto block,
        string bridgeKey,
        LogicBlockStateDto? state = null,
        MultiBridgeTagCache? tags = null,
        IReadOnlyList<LogicNoteDto>? notes = null,
        LogicAreaTagsDto? areaTags = null,
        IReadOnlyList<LogicBlockDto>? definitions = null,
        IReadOnlyList<LogicAreaTagsDto>? allAreaTags = null)
    {
        block_ = block;
        bridgeKey_ = bridgeKey;
        tags_ = tags;
        areaTags_ = areaTags;

        IReadOnlyList<LogicAreaTagsDto> areaLists = allAreaTags
            ?? (areaTags is null ? Array.Empty<LogicAreaTagsDto>() : new[] { areaTags });
        usage_ = LogicTagUsageIndex.Build(definitions ?? new[] { block }, areaLists);

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

        BuildCheckpoints();

        foreach (LogicActionDto action in block.Actions)
        {
            Actions.Add(new LogicActionButtonViewModel(action, ResolvedLabel(action.Tag, action.SourceId, action.ItemId)));
        }

        foreach (LogicStepDto step in block.Steps)
        {
            foreach (LogicActionDto action in step.Actions)
            {
                Actions.Add(new LogicActionButtonViewModel(
                    action,
                    $"{step.Name}: {ResolvedLabel(action.Tag, action.SourceId, action.ItemId)}"));
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

    /// <summary>The network's parents in network order — the screen's checkpoint dots.</summary>
    public ObservableCollection<LogicCheckpointViewModel> Checkpoints { get; } = new();

    /// <summary>True while the network has a parent group to report.</summary>
    public bool HasCheckpoints => Checkpoints.Count > 0;

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

        foreach (LogicCheckpointViewModel checkpoint in Checkpoints)
        {
            checkpoint.ApplyState(elementStates.TryGetValue(checkpoint.Id, out LogicElementStateDto? checkpointState)
                ? checkpointState
                : null);
        }

        UpdateCheckpointSummary();
        ApplyOutput(state);
        // A lost evaluation clears the progress line: "0/N steps" would read as a real count
        // rather than as "the step flow is unknown".
        ProgressText = state is null ? string.Empty : SequenceProgress(state);

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
            if (!IsTagContact(element))
            {
                // Gates, function blocks, coils and relay contacts read a relay or nothing —
                // only a tag contact has a tag cache entry to look up.
                continue;
            }

            bool resolved = TryResolveTag(
                element.Tag,
                element.SourceId,
                element.ItemId,
                out string sourceId,
                out string itemId);
            if (elementRows_.TryGetValue(element.Id, out LogicElementRowViewModel? row))
            {
                if (resolved &&
                    tags_.TryGet(TagBindingKey.Create(bridgeKey_, sourceId, itemId), out MultiBridgeTagEntry? entry))
                {
                    row.ApplyTag(entry);
                }
                else
                {
                    // A named tag that is not assigned, or an entry the link has cleared:
                    // the row reads nothing, never the last value.
                    row.ApplyTag(null);
                }
            }
        }

        if (SelectedTag is not null)
        {
            SelectedTag.ApplyEntry(FindEntry(SelectedTag.Reference));
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

    /// <summary>Opens the tag inspector for a row that names a tag.</summary>
    public void OpenTagPanel(LogicElementRowViewModel? row)
    {
        if (row?.TagReference is not { } reference)
        {
            return;
        }

        SelectedTag = new LogicTagInspectorViewModel(
            reference,
            areaTags_,
            usage_,
            tags_,
            bridgeKey_,
            usageStates_);
        IsTagPanelOpen = true;
    }

    public void CloseTagPanel()
    {
        IsTagPanelOpen = false;
        SelectedTag = null;
    }

    /// <summary>Keeps the inspector's block cards in step with the pushed evaluation.</summary>
    public void ApplyUsageStates(LogicStateSnapshot? snapshot)
    {
        Dictionary<Guid, LogicBlockStateDto> states = new();
        foreach (LogicBlockStateDto block in snapshot?.Blocks ?? Enumerable.Empty<LogicBlockStateDto>())
        {
            states[block.Id] = block;
        }

        usageStates_ = states;
        SelectedTag?.ApplyStates(usageStates_);
    }

    private MultiBridgeTagEntry? FindEntry(LogicTagReference reference)
    {
        if (tags_ is null || !reference.IsBound)
        {
            return null;
        }

        return tags_.TryGet(
            TagBindingKey.Create(bridgeKey_, reference.SourceId, reference.ItemId),
            out MultiBridgeTagEntry? entry)
            ? entry
            : null;
    }

    /// <summary>
    /// The screen's checkpoints are the network's parents — the gates and function blocks that
    /// own the checks below them — in the network's own order. A leaf contact is already named in
    /// the block's reason, and a coil publishes the result rather than checking it.
    /// </summary>
    private void BuildCheckpoints()
    {
        HashSet<Guid> seen = new();
        AddCheckpoints(LogicNetwork.For(block_), seen);
        foreach (LogicStepDto step in block_.Steps)
        {
            AddCheckpoints(LogicNetwork.For(step), seen);
        }
    }

    private void AddCheckpoints(IEnumerable<LogicElementDto> roots, HashSet<Guid> seen)
    {
        foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(roots))
        {
            if (element.Inputs.Count == 0 ||
                string.Equals(element.Kind, LogicElementKinds.Coil, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(element.Id))
            {
                continue;
            }

            Checkpoints.Add(new LogicCheckpointViewModel(element.Id, CheckpointName(element, depth)));
        }
    }

    private static string CheckpointName(LogicElementDto element, int depth)
    {
        string text = (element.Text ?? string.Empty).Trim();
        if (text.Length > 0)
        {
            return text;
        }

        string kind = LogicElementRowViewModel.DescribeKind(element, element.Kind.ToLowerInvariant());
        return depth == 0 ? "root " + kind : kind;
    }

    private void UpdateCheckpointSummary()
    {
        if (Checkpoints.Count == 0)
        {
            CheckpointSummary = string.Empty;
            CheckpointSummaryKey = LogicConditionStates.Unknown;
            CheckpointSummaryDescription = string.Empty;
            return;
        }

        int met = Checkpoints.Count(checkpoint =>
            string.Equals(checkpoint.StateKey, LogicConditionStates.True, StringComparison.OrdinalIgnoreCase));
        int open = Checkpoints.Count(checkpoint =>
            string.Equals(checkpoint.StateKey, LogicConditionStates.False, StringComparison.OrdinalIgnoreCase));
        int unknown = Checkpoints.Count - met - open;
        CheckpointSummary = met + "/" + Checkpoints.Count;
        CheckpointSummaryKey = open > 0
            ? LogicConditionStates.False
            : unknown > 0 ? LogicConditionStates.Unknown : LogicConditionStates.True;
        CheckpointSummaryDescription = met + " of " + Checkpoints.Count + " checkpoints fulfilled"
            + (open > 0 ? " · " + open + " not fulfilled" : string.Empty)
            + (unknown > 0 ? " · " + unknown + " without data" : string.Empty);
    }

    /// <summary>
    /// The result header's feedback pill: the block's output tag and whether the live output
    /// agrees with the interlock result. Hidden while the block names no output tag.
    /// </summary>
    private void ApplyOutput(LogicBlockStateDto? state)
    {
        LogicBlockOutputStateDto? output = state?.Output;
        string tag = !string.IsNullOrWhiteSpace(output?.Tag)
            ? output!.Tag.Trim()
            : (block_.OutputTag ?? string.Empty).Trim();
        HasOutput = tag.Length > 0;
        OutputKey = output is null
            ? LogicOutputStates.Unknown
            : string.IsNullOrWhiteSpace(output.State) ? LogicOutputStates.None : output.State;
        OutputText = HasOutput ? "OUTPUT " + tag + " · " + OutputWord(output) : string.Empty;
        OutputDescription = HasOutput ? OutputDescriptionText(tag, output) : string.Empty;
    }

    private static string OutputWord(LogicBlockOutputStateDto? output) => output?.State switch
    {
        LogicOutputStates.Match => "VERIFIED",
        LogicOutputStates.Mismatch => "MISMATCH",
        LogicOutputStates.Unknown => "UNVERIFIED",
        _ => "—"
    };

    private static string OutputDescriptionText(string tag, LogicBlockOutputStateDto? output)
    {
        List<string> parts = new() { tag, OutputWord(output) };
        if (!string.IsNullOrWhiteSpace(output?.Reason))
        {
            parts.Add(output!.Reason!.Trim());
        }

        string text = string.Join(" · ", parts);
        if (!string.IsNullOrWhiteSpace(output?.ValueText) && output!.ValueText != "—")
        {
            text += " · " + output.ValueText;
        }

        return text;
    }

    /// <summary>"1/2 steps · Open valve", or "complete" — a sequence's progress in the header.</summary>
    private string SequenceProgress(LogicBlockStateDto? state)
    {
        if (!IsSequence || block_.Steps.Count == 0)
        {
            return string.Empty;
        }

        Dictionary<Guid, string> words = new();
        foreach (LogicStepStateDto step in state?.Steps ?? Enumerable.Empty<LogicStepStateDto>())
        {
            words[step.Id] = step.State;
        }

        int done = 0;
        string current = string.Empty;
        for (int i = 0; i < block_.Steps.Count; i++)
        {
            LogicStepDto step = block_.Steps[i];
            string word = words.TryGetValue(step.Id, out string? value) ? value : string.Empty;
            if (string.Equals(word, LogicStepStates.Done, StringComparison.OrdinalIgnoreCase))
            {
                done++;
            }
            else if (current.Length == 0 &&
                (string.Equals(word, LogicStepStates.Current, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(word, LogicStepStates.Unknown, StringComparison.OrdinalIgnoreCase)))
            {
                current = string.IsNullOrWhiteSpace(step.Name) ? "Step " + (i + 1) : step.Name;
            }
        }

        if (done == block_.Steps.Count)
        {
            return "complete";
        }

        return done + "/" + block_.Steps.Count + " steps"
            + (current.Length > 0 ? " · " + current : string.Empty);
    }

    private LogicElementRowViewModel CreateRow(LogicElementDto element, int depth)
    {
        LogicTagReference? reference = LogicTagReferences.ForElement(block_, element, areaTags_);
        string label;
        if (!string.IsNullOrWhiteSpace(element.Relay))
        {
            // A relay contact and a coil both name their relay where a tag contact names its tag.
            label = "relay " + element.Relay;
        }
        else if (reference is { Named: true })
        {
            // A named entry, or a coil naming the block's OUTPUT tag through its area's list.
            label = ResolvedLabel(reference.Name, null, null);
        }
        else if (reference is not null)
        {
            label = ResolvedLabel(null, reference.SourceId, reference.ItemId);
        }
        else
        {
            label = string.Empty;
        }

        LogicElementRowViewModel row = new(element, depth, label, reference);
        elementRows_[element.Id] = row;
        return row;
    }

    /// <summary>A tag contact reads a tag; anything else (a relay, a gate) has nothing to look up.</summary>
    private static bool IsTagContact(LogicElementDto element) =>
        string.Equals(element.Kind, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(element.Relay);

    /// <summary>
    /// The tag a row or an action button reads or writes: a named area tag resolves through the
    /// block's area's tags, a direct reference is itself. False when a named tag is unassigned or
    /// not in the area's tags — the caller shows why instead of touching the wrong tag.
    /// </summary>
    public bool TryResolveTag(
        string? tag,
        string? sourceId,
        string? itemId,
        out string resolvedSourceId,
        out string resolvedItemId)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            return LogicAreaTags.TryResolve(areaTags_, tag, out resolvedSourceId, out resolvedItemId);
        }

        resolvedSourceId = sourceId?.Trim() ?? string.Empty;
        resolvedItemId = itemId?.Trim() ?? string.Empty;
        return resolvedSourceId.Length > 0 && resolvedItemId.Length > 0;
    }

    /// <summary>Why a named tag reads or writes nothing, in the operator's words.</summary>
    public string UnresolvedTagText(string? tag) =>
        LogicAreaTags.UnresolvedText(tag, areaTags_, LogicAreaTags.AreaNameFor(block_));

    /// <summary>A tag's label: the direct tag, or the named one with what it is bound to.</summary>
    private string ResolvedLabel(string? tag, string? sourceId, string? itemId)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            string name = tag.Trim();
            return TryResolveTag(tag, null, null, out string resolvedSourceId, out string resolvedItemId)
                ? name + " · " + TagLabel(resolvedSourceId, resolvedItemId)
                : UnresolvedTagText(tag);
        }

        return TagLabel(sourceId ?? string.Empty, itemId ?? string.Empty);
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
