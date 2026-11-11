using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One check on the block screen's rail: every evaluated element, drawn as a hollow ring when it
/// owns the checks below it and as a filled dot when it is one reading. The check's name lives in
/// <see cref="Description"/>, and tapping it jumps to its row.
/// </summary>
public sealed partial class LogicCheckpointViewModel : ObservableObject
{
    [ObservableProperty] private string _stateKey = LogicConditionStates.Unknown;
    [ObservableProperty] private string _stateWord = "no data";
    [ObservableProperty] private string _chipWord = "NO DATA";
    [ObservableProperty] private string _usageKey = "none";
    [ObservableProperty] private int _usageCount;
    [ObservableProperty] private DateTime? _usageLastUtc;
    [ObservableProperty] private bool _hasSource;
    [ObservableProperty] private bool _isUsageOn;
    [ObservableProperty] private bool _isLatest;
    [ObservableProperty] private bool _isNever;

    public LogicCheckpointViewModel(
        Guid id,
        string name,
        bool isParent,
        int depth,
        int childCount,
        int sequence,
        string sourceLabel,
        bool hasSource)
    {
        Id = id;
        Name = name;
        IsParent = isParent;
        Depth = depth;
        ChildCount = childCount;
        Sequence = sequence;
        SourceLabel = sourceLabel;
        HasSource = hasSource;
    }

    public Guid Id { get; }

    /// <summary>The check's sentence, or its kind when it has none ("root AND").</summary>
    public string Name { get; }

    /// <summary>True when this check owns the checks below it (a gate or function block).</summary>
    public bool IsParent { get; }

    /// <summary>Depth in the evaluated network — the usage roll-up follows it.</summary>
    public int Depth { get; }

    /// <summary>Direct inputs of a parent; 0 for a leaf.</summary>
    public int ChildCount { get; }

    /// <summary>1-based position in the complete checkpoint sequence.</summary>
    public int Sequence { get; }

    /// <summary>"parent of 1" / "leaf · Sim 03 · UA Sim" — the sheet's second line.</summary>
    public string SourceLabel { get; }

    public string Hint => IsParent ? "Go to this parent" : "Go to this element";

    public bool IsSourceLess => !HasSource;

    /// <summary>The 4px dot is only drawn while Usage is on and the check has a mark.</summary>
    public bool ShowUsageDot => IsUsageOn && UsageKey != "never";

    public bool ShowUsageLine => IsUsageOn;
    public bool ShowLatest => IsUsageOn && IsLatest;
    public bool ShowNever => IsUsageOn && IsNever;

    /// <summary>"24×" for a tag-backed check; "last activated" (or "not activated yet") without a source.</summary>
    public string UsageCountText => HasSource
        ? UsageCount + "×"
        : UsageLastUtc is null ? "not activated yet" : "last activated";

    /// <summary>"· last 5m ago" — or "· 3h ago" for a source-less check.</summary>
    public string UsageLastText => UsageLastUtc is null
        ? (HasSource ? "never" : string.Empty)
        : (HasSource ? "· last " + Ago(UsageLastUtc.Value) : "· " + Ago(UsageLastUtc.Value));

    public string LastActiveAgo => UsageLastUtc is null ? "never" : Ago(UsageLastUtc.Value);

    /// <summary>"Name — not fulfilled. 24 activations in 7 days, last 5m ago. Go to this element."</summary>
    public string Description => Name + " — " + StateWord + "."
        + (IsUsageOn && UsageDescription.Length > 0 ? " " + UsageDescription + "." : string.Empty)
        + " " + Hint + ".";

    private string UsageDescription => !HasSource
        ? (UsageLastUtc is null ? string.Empty : "last activated " + Ago(UsageLastUtc.Value))
        : UsageCount + (UsageCount == 1 ? " activation" : " activations") + " in 7 days"
            + (UsageLastUtc is null ? string.Empty : ", last " + Ago(UsageLastUtc.Value));

    partial void OnStateWordChanged(string value) => OnPropertyChanged(nameof(Description));

    partial void OnUsageKeyChanged(string value) => OnPropertyChanged(nameof(ShowUsageDot));

    partial void OnUsageCountChanged(int value)
    {
        OnPropertyChanged(nameof(UsageCountText));
        OnPropertyChanged(nameof(Description));
    }

    partial void OnUsageLastUtcChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(UsageLastText));
        OnPropertyChanged(nameof(LastActiveAgo));
        OnPropertyChanged(nameof(Description));
    }

    partial void OnHasSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSourceLess));
        OnPropertyChanged(nameof(UsageCountText));
        OnPropertyChanged(nameof(UsageLastText));
        OnPropertyChanged(nameof(Description));
    }

    partial void OnIsUsageOnChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUsageDot));
        OnPropertyChanged(nameof(ShowUsageLine));
        OnPropertyChanged(nameof(ShowLatest));
        OnPropertyChanged(nameof(ShowNever));
        OnPropertyChanged(nameof(Description));
    }

    partial void OnIsLatestChanged(bool value) => OnPropertyChanged(nameof(ShowLatest));
    partial void OnIsNeverChanged(bool value) => OnPropertyChanged(nameof(ShowNever));

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

    /// <summary>Stores one check's own activation history; a parent is rolled up separately.</summary>
    public void ApplyUsage(LogicCheckpointUsageDto? usage, bool hasSource)
    {
        UsageCount = usage?.Count ?? 0;
        UsageLastUtc = usage?.LastActiveUtc;
        HasSource = hasSource;
        UsageKey = Heat(hasSource, UsageCount);
    }

    /// <summary>Replaces a parent's own history with its source-backed direct children's.</summary>
    public void SetAggregateUsage(int count, DateTime? last, bool sourced)
    {
        UsageCount = sourced ? count : 0;
        UsageLastUtc = last;
        HasSource = sourced;
        UsageKey = Heat(sourced, UsageCount);
    }

    public void SetChipWord(string word) => ChipWord = word;
    public void SetUsageOn(bool on) => IsUsageOn = on;
    public void SetLatest(bool latest) => IsLatest = latest;
    public void SetNever(bool never) => IsNever = never;

    private static string Heat(bool hasSource, int count) => !hasSource
        ? "none"
        : count >= 100 ? "hot" : count >= 10 ? "warm" : count > 0 ? "cool" : "never";

    private static string Ago(DateTime value)
    {
        double seconds = Math.Max(0, (DateTime.UtcNow - value.ToUniversalTime()).TotalSeconds);
        if (seconds < 60)
        {
            return "just now";
        }

        if (seconds < 3600)
        {
            return (int)(seconds / 60) + "m ago";
        }

        return seconds < 86400 ? (int)(seconds / 3600) + "h ago" : (int)(seconds / 86400) + "d ago";
    }
}

/// <summary>One chip in the fullsheet's type filter, with its total.</summary>
public sealed partial class LogicCheckpointFilterViewModel : ObservableObject
{
    public LogicCheckpointFilterViewModel(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isActive;
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
    private MultiBridgeTagEntry? tagEntry_;
    private string? cachedTagText_;
    private bool cachedTagTextSet_;
    private bool cachedTagDigital_;
    private string? builtShouldText_;
    private string? builtActualText_;

    /// <summary>The mapping's operator word (Closed, Run, Off) for the row's STATE chip.</summary>
    [ObservableProperty] private string _stateWord = string.Empty;

    [ObservableProperty] private string _stateKey = LogicConditionStates.Unknown;
    [ObservableProperty] private string _actualText = "—";
    [ObservableProperty] private string _valueText = string.Empty;
    [ObservableProperty] private bool _showNextStep;

    /// <summary>"should 1 · actual 0" — the reading a contact expects against the live one.</summary>
    [ObservableProperty] private string _shouldLine = string.Empty;

    /// <summary>True for a contact whose requirement can be stated.</summary>
    [ObservableProperty] private bool _showShould;

    /// <summary>True when the row has both a comparison and a mapped operator word to chip.</summary>
    public bool HasStateWord => ShowShould && StateWord.Length > 0;

    partial void OnStateWordChanged(string value) => OnPropertyChanged(nameof(HasStateWord));

    partial void OnShowShouldChanged(bool value) => OnPropertyChanged(nameof(HasStateWord));

    /// <summary>True while the live reading does not satisfy a contact.</summary>
    [ObservableProperty] private bool _mismatch;

    /// <summary>State key the should/actual line colours by: "true" matched, "false" mismatched.</summary>
    [ObservableProperty] private string _shouldKey = LogicConditionStates.Unknown;

    /// <summary>"holding 1.5 s of 3 s" / "count 2 of 5" — a timer's or counter's progress.</summary>
    [ObservableProperty] private string _progressText = string.Empty;

    [ObservableProperty] private bool _showProgress;

    /// <summary>The block this row belongs to (set by the shared row builder).</summary>
    public Guid BlockId { get; set; }

    public string BlockName { get; set; } = string.Empty;

    public string BlockKind { get; set; } = string.Empty;

    /// <summary>The sequence step this row belongs to; null for a block-level element.</summary>
    public Guid? StepId { get; set; }

    /// <summary>"Step 2 · Open valve"; empty for a block-level element.</summary>
    public string StepLabel { get; set; } = string.Empty;

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

    /// <summary>True for a nested row, the only ones that draw rails.</summary>
    public bool HasDepth => Depth > 0;

    /// <summary>True for a contact / comparison; false for a gate or function block.</summary>
    public bool IsContact { get; }

    /// <summary>"NO" / "NC" / "&gt; 50" for a contact; "AND", "TON IN", "CTU CU" for a block.</summary>
    public string KindLabel { get; }

    /// <summary>The element's sentence (contacts) or label (gates and blocks).</summary>
    public string Text => string.IsNullOrWhiteSpace(element_.Text) ? KindLabel : element_.Text;

    /// <summary>"Pressure switch PS1 · sim" — what a contact reads; empty for a gate or block.</summary>
    public string TagLabel { get; }

    /// <summary>True when there is a tag line to draw.</summary>
    public bool HasTag => TagLabel.Length > 0;

    /// <summary>True when this row belongs to a sequence step.</summary>
    public bool HasStep => StepLabel.Length > 0;

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

        should_text_ = state?.Should is { Length: > 0 } should ? should : string.Empty;
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
        RefreshStateWord();
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

        tagEntry_ = entry;
        string? text = entry is null || entry.Value is null ? null : LogicTagText.Format(entry);

        // A values batch carries every tag that moved, but most rows are untouched by it.
        // Leave their reading and derived texts exactly as they are instead of re-formatting
        // and re-raising the same strings ten times a second.
        if (cachedTagTextSet_ &&
            string.Equals(cachedTagText_, text, StringComparison.Ordinal) &&
            cachedTagDigital_ == (entry?.Digital ?? false) &&
            string.Equals(ValueText, text ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        cachedTagTextSet_ = true;
        cachedTagText_ = text;
        cachedTagDigital_ = entry?.Digital ?? false;

        if (text is null)
        {
            // No entry, or an entry whose reading was cleared with the link: the row reads
            // nothing instead of the value that was on screen when the bridge went away.
            ValueText = string.Empty;
            UpdateActualText();
            UpdateShouldLine();
            RefreshStateWord();
            return;
        }

        isBoolean_ = entry!.Digital;
        ApplyValueText(text);
        RefreshStateWord();
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
        if (!ShowShould)
        {
            if (ShouldLine.Length > 0)
            {
                ShouldLine = string.Empty;
            }

            return;
        }

        // The line is a string the list shows on a row; build it only when one of its two
        // readings changed, not on every push that re-applies the same state.
        if (string.Equals(builtShouldText_, should_text_, StringComparison.Ordinal) &&
            string.Equals(builtActualText_, ActualText, StringComparison.Ordinal))
        {
            return;
        }

        builtShouldText_ = should_text_;
        builtActualText_ = ActualText;
        ShouldLine = "should " + should_text_ + " · actual " + ActualText;
    }

    /// <summary>
    /// The mapped operator word rides only a row that states a comparison, so a coil or a gate
    /// never adds a word the plant did not map. A numeric reading keeps the numbers alone.
    /// </summary>
    private void RefreshStateWord()
    {
        StateWord = ShowShould ? LogicTagText.StateWord(tagEntry_) : string.Empty;
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

/// <summary>One operator note on the block, or one still waiting in the sync outbox.</summary>
public sealed class LogicNoteRowViewModel
{
    public LogicNoteRowViewModel(LogicNoteDto note)
    {
        Text = note.Text;
        Author = note.Author;
        WhenText = note.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>A note the outbox has not delivered yet: it reads as pending, never as saved.</summary>
    public LogicNoteRowViewModel(OutboxItem pending)
    {
        Text = pending.NoteText ?? string.Empty;
        Author = "queued";
        WhenText = pending.QueuedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        IsPending = true;
    }

    public string Text { get; }

    public string Author { get; }

    public string WhenText { get; }

    public bool IsPending { get; }

    /// <summary>"operator1 · 2026-10-08 12:30:05" (or "pending sync · …") for the note list.</summary>
    public string Meta => IsPending ? "pending sync · " + WhenText : Author + " · " + WhenText;
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
    private readonly IReadOnlyList<LogicBlockDto> definitions_;
    private readonly IReadOnlyList<LogicAreaTagsDto> allAreaTags_;
    private readonly object usageLock_ = new();
    private LogicTagUsageIndex? usage_;
    private IReadOnlyDictionary<Guid, LogicBlockStateDto> usageStates_ = new Dictionary<Guid, LogicBlockStateDto>();
    private readonly Dictionary<Guid, LogicElementRowViewModel> elementRows_ = new();
    private readonly Dictionary<Guid, LogicStepRowViewModel> stepRows_ = new();
    private readonly Dictionary<TagBindingKey, List<LogicElementRowViewModel>> rowsByTag_ =
        new(TagBindingKeyComparer.Instance);

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

    /// <summary>The rail's Usage toggle, shared by the rail and the fullsheet.</summary>
    [ObservableProperty] private bool _isUsageOn;

    [ObservableProperty] private bool _isCheckpointSheetOpen;
    [ObservableProperty] private string _checkpointSheetSubtitle = string.Empty;
    [ObservableProperty] private string _checkpointSheetTopText = string.Empty;
    [ObservableProperty] private string _checkpointSheetTopCount = string.Empty;
    [ObservableProperty] private string _checkpointSheetTopKey = "none";
    [ObservableProperty] private string _checkpointSheetNoSourceText = string.Empty;
    [ObservableProperty] private string _checkpointSheetNoSourceLast = string.Empty;
    [ObservableProperty] private bool _showCheckpointSheetTop;
    [ObservableProperty] private bool _showCheckpointSheetNoSource;

    /// <summary>The rows behind the sheet, filtered in the complete checkpoint order.</summary>
    public ObservableCollection<LogicCheckpointViewModel> CheckpointSheetRows { get; } = new();

    /// <summary>All / Parents / Leaves / Unused — the sheet's type chips.</summary>
    public ObservableCollection<LogicCheckpointFilterViewModel> CheckpointFilters { get; } = new()
    {
        new LogicCheckpointFilterViewModel("all", "All"),
        new LogicCheckpointFilterViewModel("parents", "Parents"),
        new LogicCheckpointFilterViewModel("leaves", "Leaves"),
        new LogicCheckpointFilterViewModel("unused", "Unused")
    };

    private string selected_checkpoint_filter_ = "all";

    partial void OnIsUsageOnChanged(bool value)
    {
        foreach (LogicCheckpointViewModel checkpoint in Checkpoints)
        {
            checkpoint.SetUsageOn(value);
        }
    }

    public void ToggleCheckpointUsage() => IsUsageOn = !IsUsageOn;

    public void OpenCheckpointSheet()
    {
        selected_checkpoint_filter_ = "all";
        UpdateCheckpointFilters();
        RebuildCheckpointSheetRows();
        IsCheckpointSheetOpen = true;
    }

    public void CloseCheckpointSheet() => IsCheckpointSheetOpen = false;

    public void SelectCheckpointFilter(string key)
    {
        selected_checkpoint_filter_ = key;
        UpdateCheckpointFilters();
        RebuildCheckpointSheetRows();
    }

    public void RebuildCheckpointSheetRows()
    {
        CheckpointSheetRows.Clear();
        IEnumerable<LogicCheckpointViewModel> rows = selected_checkpoint_filter_ switch
        {
            "parents" => Checkpoints.Where(checkpoint => checkpoint.IsParent),
            "leaves" => Checkpoints.Where(checkpoint => !checkpoint.IsParent),
            "unused" => Checkpoints.Where(checkpoint =>
                !checkpoint.IsParent && checkpoint.HasSource && checkpoint.UsageCount == 0),
            _ => Checkpoints
        };

        foreach (LogicCheckpointViewModel checkpoint in rows)
        {
            CheckpointSheetRows.Add(checkpoint);
        }
    }

    private void UpdateCheckpointFilters()
    {
        int parents = Checkpoints.Count(checkpoint => checkpoint.IsParent);
        int unused = Checkpoints.Count(checkpoint =>
            !checkpoint.IsParent && checkpoint.HasSource && checkpoint.UsageCount == 0);
        foreach (LogicCheckpointFilterViewModel filter in CheckpointFilters)
        {
            filter.Count = filter.Key switch
            {
                "parents" => parents,
                "leaves" => Checkpoints.Count - parents,
                "unused" => unused,
                _ => Checkpoints.Count
            };
            filter.IsActive = filter.Key == selected_checkpoint_filter_;
        }

        CheckpointSheetSubtitle = Title + " · " + Checkpoints.Count + " checks";
    }

    /// <summary>
    /// The rail's second channel: a check's own activations, then a parent's source-backed direct
    /// children. The latest source-backed leaf gets LATEST; a source-backed check never activated
    /// gets NEVER; a check with no tag source stays "none" (a hollow usage dot).
    /// </summary>
    private void UpdateCheckpointUsage()
    {
        foreach (LogicCheckpointViewModel checkpoint in Checkpoints)
        {
            checkpoint.SetLatest(false);
            checkpoint.SetNever(false);
        }

        for (int i = Checkpoints.Count - 1; i >= 0; i--)
        {
            LogicCheckpointViewModel parent = Checkpoints[i];
            if (!parent.IsParent)
            {
                continue;
            }

            int count = 0;
            DateTime? last = null;
            bool sourced = parent.HasSource;
            int children = 0;
            for (int j = i + 1; j < Checkpoints.Count && Checkpoints[j].Depth > parent.Depth; j++)
            {
                LogicCheckpointViewModel child = Checkpoints[j];
                if (child.Depth != parent.Depth + 1)
                {
                    continue;
                }

                if (child.HasSource)
                {
                    count += child.UsageCount;
                }

                if (child.UsageLastUtc is { } childLast && (last is null || childLast > last))
                {
                    last = childLast;
                }

                sourced |= child.HasSource;
                children++;
            }

            if (children > 0)
            {
                parent.SetAggregateUsage(count, last, sourced);
            }
        }

        LogicCheckpointViewModel? latest = null;
        foreach (LogicCheckpointViewModel checkpoint in Checkpoints)
        {
            // NEVER belongs to a leaf whose own tag source has no activation; a parent's 0 is
            // a roll-up and cannot say which child was never used.
            checkpoint.SetNever(!checkpoint.IsParent && checkpoint.HasSource && checkpoint.UsageLastUtc is null);
            if (!checkpoint.IsParent && checkpoint.HasSource && checkpoint.UsageLastUtc is { } last
                && (latest is null || last > latest.UsageLastUtc))
            {
                latest = checkpoint;
            }
        }

        latest?.SetLatest(true);
        UpdateCheckpointFilters();
        UpdateCheckpointSheetSummary();
        if (IsCheckpointSheetOpen && selected_checkpoint_filter_ == "unused")
        {
            // The unused set is the one filter whose membership can change with an activation.
            RebuildCheckpointSheetRows();
        }
    }

    private void UpdateCheckpointSheetSummary()
    {
        LogicCheckpointViewModel? top = Checkpoints
            .Where(checkpoint => !checkpoint.IsParent && checkpoint.HasSource)
            .OrderByDescending(checkpoint => checkpoint.UsageCount)
            .FirstOrDefault();
        ShowCheckpointSheetTop = top is not null;
        CheckpointSheetTopText = top is null ? string.Empty : "Most activated · " + top.Name;
        CheckpointSheetTopCount = top is null ? string.Empty : top.UsageCount + "×";
        CheckpointSheetTopKey = top?.UsageKey ?? "none";

        LogicCheckpointViewModel? noSource = Checkpoints
            .Where(checkpoint => !checkpoint.IsParent && !checkpoint.HasSource)
            .OrderByDescending(checkpoint => checkpoint.UsageLastUtc)
            .FirstOrDefault();
        ShowCheckpointSheetNoSource = noSource is not null;
        CheckpointSheetNoSourceText = noSource is null ? string.Empty : "No source · " + noSource.Name;
        CheckpointSheetNoSourceLast = noSource?.LastActiveAgo ?? string.Empty;
    }

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

        definitions_ = definitions ?? new[] { block };
        allAreaTags_ = allAreaTags
            ?? (areaTags is null ? Array.Empty<LogicAreaTagsDto>() : new[] { areaTags });

        Title = block.Name;
        Description = block.Description;
        IsSequence = string.Equals(block.Kind, LogicBlockKinds.Sequence, StringComparison.OrdinalIgnoreCase);
        KindLabel = IsSequence
            ? "Sequence"
            : string.Equals(block.Kind, LogicBlockKinds.Permissive, StringComparison.OrdinalIgnoreCase) ? "Permissive" : "Interlock";
        string context = !string.IsNullOrWhiteSpace(block.Area)
            ? block.Area!.Trim()
            : (block.Group ?? string.Empty).Trim();
        Subtitle = context.Length > 0 ? KindLabel + " · " + context : KindLabel;

        foreach (LogicElementRowViewModel element in LogicElementRows.Walk(
            block, null, 0, LogicNetwork.For(block), areaTags, tags, bridgeKey))
        {
            Elements.Add(element);
            elementRows_[element.Id] = element;
        }

        int number = 0;
        foreach (LogicStepDto step in block.Steps)
        {
            number++;
            LogicStepRowViewModel row = new(step, number);
            stepRows_[step.Id] = row;
            foreach (LogicElementRowViewModel element in LogicElementRows.Walk(
                block, step, number, LogicNetwork.For(step), areaTags, tags, bridgeKey))
            {
                row.Elements.Add(element);
                elementRows_[element.Id] = element;
            }

            Steps.Add(row);
        }

        IndexTagRows();
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

    /// <summary>"Interlock · Line 1" — the app bar's second line.</summary>
    public string Subtitle { get; }

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

    /// <summary>True when the block carries authored actions; the section is hidden otherwise.</summary>
    public bool HasActions => Actions.Count > 0;

    /// <summary>
    /// True while the screen is the no-login offline view: notes stay readable, but nothing
    /// can be written until the bridge is back and the operator has signed in.
    /// </summary>
    [ObservableProperty] private bool _isReadOnly;

    /// <summary>The action section shows only when there is something to run and writing is allowed.</summary>
    public bool ShowActions => HasActions && !IsReadOnly;

    /// <summary>The note composer hides in the read-only offline view; the notes list stays.</summary>
    public bool ShowNoteComposer => !IsReadOnly;

    partial void OnIsReadOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowActions));
        OnPropertyChanged(nameof(ShowNoteComposer));
    }

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
            LogicElementStateDto? checkpointState = elementStates.TryGetValue(checkpoint.Id, out LogicElementStateDto? found)
                ? found
                : null;
            checkpoint.ApplyState(checkpointState);

            LogicElementRowViewModel? row = elementRows_.TryGetValue(checkpoint.Id, out LogicElementRowViewModel? elementRow)
                ? elementRow
                : null;
            bool hasSource = !checkpoint.IsParent && row?.HasTagReference == true;
            checkpoint.ApplyUsage(checkpointState?.Usage, hasSource);

            string mappedWord = (row?.StateWord ?? string.Empty).Trim();
            checkpoint.SetChipWord(!checkpoint.IsParent && mappedWord.Length > 0
                && mappedWord != "1" && mappedWord != "0"
                ? mappedWord.ToUpperInvariant()
                : checkpoint.StateWord.ToUpperInvariant());
        }

        UpdateCheckpointUsage();
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

    /// <summary>
    /// The item the list scrolls to for one checkpoint: the element's own row, or — in a
    /// sequence, where the item source is the steps — the step that holds it.
    /// </summary>
    public object? FindScrollTarget(Guid elementId)
    {
        if (!elementRows_.TryGetValue(elementId, out LogicElementRowViewModel? row))
        {
            return null;
        }

        if (IsSequence)
        {
            foreach (LogicStepRowViewModel step in Steps)
            {
                if (step.Elements.Any(element => element.Id == elementId))
                {
                    return step;
                }
            }
        }

        return row;
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

    /// <summary>
    /// Applies one live values batch to only the rows that read those tags. The page stays
    /// current without re-reading every contact on the screen for each push.
    /// </summary>
    public void ApplyTagDeltas(IReadOnlyList<HmiValueDelta> deltas)
    {
        if (tags_ is null)
        {
            return;
        }

        foreach (HmiValueDelta delta in deltas)
        {
            TagBindingKey key = TagBindingKey.Create(bridgeKey_, delta.SourceId, delta.ItemId);
            if (!rowsByTag_.TryGetValue(key, out List<LogicElementRowViewModel>? rows))
            {
                continue;
            }

            tags_.TryGet(key, out MultiBridgeTagEntry? entry);
            foreach (LogicElementRowViewModel row in rows)
            {
                row.ApplyTag(entry);
            }
        }

        if (IsTagPanelOpen && SelectedTag is not null)
        {
            SelectedTag.ApplyEntry(FindEntry(SelectedTag.Reference));
        }
    }

    private void IndexTagRows()
    {
        foreach (LogicElementRowViewModel row in elementRows_.Values)
        {
            if (!row.IsContact || row.TagReference is not { IsBound: true } reference)
            {
                continue;
            }

            TagBindingKey key = TagBindingKey.Create(bridgeKey_, reference.SourceId, reference.ItemId);
            if (!rowsByTag_.TryGetValue(key, out List<LogicElementRowViewModel>? indexed))
            {
                indexed = new List<LogicElementRowViewModel>();
                rowsByTag_[key] = indexed;
            }

            indexed.Add(row);
        }
    }

    /// <summary>
    /// Rebuilds the note list from the bridge's notes and the outbox's undelivered ones. A
    /// queued note whose client id already came back from the bridge is not shown twice.
    /// </summary>
    public void SetNotes(IReadOnlyList<LogicNoteDto> notes, IEnumerable<OutboxItem>? pending = null)
    {
        Notes.Clear();
        HashSet<Guid> saved = notes.Select(note => note.Id).ToHashSet();
        foreach (OutboxItem item in pending ?? Enumerable.Empty<OutboxItem>())
        {
            if (item.Kind == OutboxKind.Note && !saved.Contains(item.Id))
            {
                Notes.Add(new LogicNoteRowViewModel(item));
            }
        }

        foreach (LogicNoteDto note in notes)
        {
            Notes.Add(new LogicNoteRowViewModel(note));
        }
    }

    /// <summary>
    /// Builds the plant-wide tag usage index away from the page-open path. The page calls this
    /// on a worker after it is visible; a tag tap before it finishes builds it on demand.
    /// </summary>
    public void PrepareTagUsage()
    {
        if (usage_ is not null)
        {
            return;
        }

        lock (usageLock_)
        {
            usage_ ??= LogicTagUsageIndex.Build(definitions_, allAreaTags_);
        }
    }

    /// <summary>Opens the tag inspector for a row that names a tag.</summary>
    public void OpenTagPanel(LogicElementRowViewModel? row)
    {
        if (row?.TagReference is not { } reference)
        {
            return;
        }

        // The tag sheet is the only consumer of the usage index. It is prebuilt in the
        // background after the page is visible, so opening the sheet is normally instant.
        PrepareTagUsage();
        SelectedTag = new LogicTagInspectorViewModel(
            reference,
            areaTags_,
            usage_!,
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
    /// The rail marks every evaluated element in the network's own order. A gate or function
    /// block that owns checks below it is a parent (a hollow ring); a contact or coil is one
    /// reading (a filled dot). Coils publish the result but still sit on the rail, as in the web
    /// Result board, so no check is missed from the met/total count.
    /// </summary>
    private void BuildCheckpoints()
    {
        HashSet<Guid> seen = new();
        int sequence = 0;
        AddCheckpoints(LogicNetwork.For(block_), seen, ref sequence);
        foreach (LogicStepDto step in block_.Steps)
        {
            AddCheckpoints(LogicNetwork.For(step), seen, ref sequence);
        }
    }

    private void AddCheckpoints(IEnumerable<LogicElementDto> roots, HashSet<Guid> seen, ref int sequence)
    {
        foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(roots))
        {
            if (!seen.Add(element.Id))
            {
                continue;
            }

            sequence++;
            bool parent = element.Inputs.Count > 0 &&
                !string.Equals(element.Kind, LogicElementKinds.Coil, StringComparison.OrdinalIgnoreCase);
            LogicElementRowViewModel? row = elementRows_.TryGetValue(element.Id, out LogicElementRowViewModel? elementRow)
                ? elementRow
                : null;
            string tagLabel = (row?.TagLabel ?? string.Empty).Trim();
            bool hasSource = row?.HasTagReference == true;
            string sourceLabel = parent
                ? "parent of " + element.Inputs.Count
                : tagLabel.Length > 0 ? "leaf · " + tagLabel : "leaf · no tag source";
            Checkpoints.Add(new LogicCheckpointViewModel(
                element.Id,
                CheckpointName(element, depth),
                parent,
                depth,
                element.Inputs.Count,
                sequence,
                sourceLabel,
                hasSource));
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
    private string ResolvedLabel(string? tag, string? sourceId, string? itemId) =>
        LogicElementRows.ResolvedLabel(block_, areaTags_, tags_, bridgeKey_, tag, sourceId, itemId);

    private string TagLabel(string sourceId, string itemId) =>
        LogicElementRows.TagLabel(tags_, bridgeKey_, sourceId, itemId);

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
