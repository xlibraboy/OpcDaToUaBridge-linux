using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One editable element row of the IEC 61131-3 network editor. Rows nest by indentation: a row's
/// inputs are the rows under it at a deeper level, rebuilt into the tree on save. The fields shown
/// follow the selected kind — a contact reads a tag (or an internal relay when a relay is typed),
/// a coil drives an internal relay, a timer its preset, a counter its preset, a gate or function
/// block just its label. A row that names an internal relay also edits that relay's tag binding
/// (the tag its state is shown on) and its write policy: one binding per M, so every row that
/// names the same relay shows the same tag.
/// </summary>
public partial class LogicElementRowViewModel : ObservableObject
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicElementRowViewModel>? onRemove_;
    private readonly IReadOnlyDictionary<string, LogicRelayDto> relayBindings_;
    private readonly string elementSourceId_;
    private readonly string elementItemId_;
    private bool loading_;
    private bool applyingRelay_;

    public LogicElementRowViewModel(
        LogicElementDto element,
        int indent,
        LogicTagCatalog catalog,
        Action<LogicElementRowViewModel>? onRemove = null,
        IEnumerable<string>? relayNames = null,
        IReadOnlyDictionary<string, LogicRelayDto>? relayBindings = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
        relayBindings_ = relayBindings ?? new Dictionary<string, LogicRelayDto>(StringComparer.OrdinalIgnoreCase);
        RelayNames = relayNames ?? Array.Empty<string>();
        ElementId = element.Id == Guid.Empty ? Guid.NewGuid() : element.Id;
        Kind = LogicSelectOption.Find(LogicOptions.ElementKinds, element.Kind) ?? LogicOptions.ElementKinds[0];
        Text = element.Text;
        Op = LogicSelectOption.Find(LogicOptions.Ops, element.Op) ?? LogicOptions.Ops[0];
        elementSourceId_ = element.SourceId;
        elementItemId_ = element.ItemId;
        ValueText = element.Value is { } value ? LogicConditionRowViewModel.FormatNumber(value) : string.Empty;
        PtSeconds = element.PtMs > 0 ? LogicConditionRowViewModel.FormatNumber(element.PtMs / 1000.0) : string.Empty;
        PvText = element.Pv > 0 ? element.Pv.ToString(CultureInfo.InvariantCulture) : string.Empty;
        Severity = LogicSelectOption.Find(LogicOptions.Severities, element.Severity) ?? LogicOptions.Severities[0];
        Indent = Math.Clamp(indent, 0, LogicLimits.MaxElementDepth);
        RelayWrite = LogicOptions.RelayWrites[0];

        // The pickers are loaded explicitly here, not through the Relay change handler: a plain
        // contact shows its own tag, a relay row the tag its M is bound to (or empty for a new M).
        applyingRelay_ = true;
        Relay = element.Relay;
        applyingRelay_ = false;
        if (string.IsNullOrWhiteSpace(element.Relay))
        {
            LoadSourceAndItems(elementSourceId_, elementItemId_);
        }
        else
        {
            string relay = element.Relay.Trim();
            ApplyBinding(relayBindings_.TryGetValue(relay, out LogicRelayDto? binding) ? binding : null);
        }
    }

    public Guid ElementId { get; }

    /// <summary>Relay names already driven by a coil in any block, offered while typing one.</summary>
    public IEnumerable<string> RelayNames { get; }

    public IReadOnlyList<LogicSelectOption> KindOptions => LogicOptions.ElementKinds;

    public IReadOnlyList<LogicSelectOption> OpOptions => LogicOptions.Ops;

    public IReadOnlyList<LogicSelectOption> SeverityOptions => LogicOptions.Severities;

    public ObservableCollection<LogicSourceOption> Sources { get; } = new();

    public ObservableCollection<LogicTagOption> Items { get; } = new();

    [ObservableProperty]
    private LogicSelectOption _kind;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private LogicSelectOption _op;

    /// <summary>
    /// Internal relay (e.g. "M01"): typed on a contact it reads the relay instead of a tag,
    /// typed on a coil it is the relay the coil drives. Uppercased by the bridge on save.
    /// </summary>
    [ObservableProperty]
    private string _relay = string.Empty;

    [ObservableProperty]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string _ptSeconds = string.Empty;

    [ObservableProperty]
    private string _pvText = string.Empty;

    [ObservableProperty]
    private LogicSelectOption _severity;

    /// <summary>
    /// Who may write the tag this row's relay is bound to. Only shown once a tag is bound; the
    /// binding is shared by every row that names the same relay.
    /// </summary>
    [ObservableProperty]
    private LogicSelectOption _relayWrite;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IndentRowCommand))]
    [NotifyCanExecuteChangedFor(nameof(OutdentRowCommand))]
    private int _indent;

    [ObservableProperty]
    private LogicSourceOption? _selectedSource;

    [ObservableProperty]
    private LogicTagOption? _selectedTag;

    [ObservableProperty]
    private string _liveText = "—";

    [ObservableProperty]
    private bool _isLiveOk;

    [ObservableProperty]
    private bool _isLiveBad;

    [ObservableProperty]
    private bool _isLiveWarn = true;

    public string SourceId => SelectedSource?.SourceId ?? string.Empty;

    public string ItemId => SelectedTag?.ItemId ?? string.Empty;

    public bool IsContact => string.Equals(Kind?.Value, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase);

    public bool IsTimer => LogicElementKinds.IsTimer(Kind?.Value);

    public bool IsCounter => LogicElementKinds.IsCounter(Kind?.Value);

    public bool IsCoil => LogicElementKinds.IsCoil(Kind?.Value);

    /// <summary>A contact that reads an internal relay instead of a tag.</summary>
    public bool IsRelayContact => IsContact && Relay.Trim().Length > 0;

    /// <summary>The tag pickers: a contact that is not reading a relay.</summary>
    public bool ShowsTag => IsContact && !IsRelayContact;

    /// <summary>
    /// The tag pickers in every case they mean something: a tag contact reads the tag, a relay
    /// row (coil or relay contact) binds the M to the tag its state is shown on.
    /// </summary>
    public bool ShowsTagFields => ShowsTag || ShowsRelay;

    /// <summary>The relay field: a relay contact reads one, a coil drives one.</summary>
    public bool ShowsRelay => IsRelayContact || IsCoil;

    /// <summary>The write policy of a relay's tag: only once the M is bound to one.</summary>
    public bool ShowsRelayWrite => ShowsRelay && ItemId.Length > 0;

    public IReadOnlyList<LogicSelectOption> RelayWriteOptions => LogicOptions.RelayWrites;

    /// <summary>A coil is an output — "warn only" has no meaning for it.</summary>
    public bool ShowsSeverity => !IsCoil;

    /// <summary>A gate, latch or edge trigger: a label only, no tag, preset or value.</summary>
    public bool ShowsLabelOnly => !IsContact && !IsCoil && !IsTimer && !IsCounter;

    public bool NeedsValue => IsContact && LogicConditionOps.RequiresValue(Op?.Value);

    /// <summary>
    /// One entry per level enclosing this row: the row draws a rail for each, so the nesting the
    /// indentation implies is drawn, not guessed. Rebuilt whenever the row is indented.
    /// </summary>
    public IReadOnlyList<int> Guides => new int[Indent];

    partial void OnIndentChanged(int value) => OnPropertyChanged(nameof(Guides));

    partial void OnCanEditChanged(bool value)
    {
        IndentRowCommand.NotifyCanExecuteChanged();
        OutdentRowCommand.NotifyCanExecuteChanged();
        RemoveElementCommand.NotifyCanExecuteChanged();
    }

    partial void OnKindChanged(LogicSelectOption value)
    {
        OnPropertyChanged(nameof(IsContact));
        OnPropertyChanged(nameof(IsTimer));
        OnPropertyChanged(nameof(IsCounter));
        OnPropertyChanged(nameof(IsCoil));
        OnPropertyChanged(nameof(IsRelayContact));
        OnPropertyChanged(nameof(ShowsTag));
        OnPropertyChanged(nameof(ShowsTagFields));
        OnPropertyChanged(nameof(ShowsRelay));
        OnPropertyChanged(nameof(ShowsRelayWrite));
        OnPropertyChanged(nameof(ShowsSeverity));
        OnPropertyChanged(nameof(ShowsLabelOnly));
        OnPropertyChanged(nameof(NeedsValue));
    }

    partial void OnOpChanged(LogicSelectOption value) => OnPropertyChanged(nameof(NeedsValue));

    partial void OnRelayChanged(string value)
    {
        OnPropertyChanged(nameof(IsRelayContact));
        OnPropertyChanged(nameof(ShowsTag));
        OnPropertyChanged(nameof(ShowsTagFields));
        OnPropertyChanged(nameof(ShowsRelay));
        OnPropertyChanged(nameof(ShowsRelayWrite));

        // A relay contact reads a bit: a numeric comparison has nothing to compare against.
        if (value.Trim().Length > 0 && LogicConditionOps.RequiresValue(Op?.Value))
        {
            Op = LogicOptions.Ops[0];
        }

        if (applyingRelay_)
        {
            return;
        }

        string relay = value.Trim();
        if (relay.Length == 0)
        {
            // No relay any more: the pickers are the element's own tag again.
            RelayWrite = LogicOptions.RelayWrites[0];
            LoadSourceAndItems(elementSourceId_, elementItemId_);
            return;
        }

        // A relay this bridge already knows shows its binding; an unknown name is a new M being
        // authored, so the pickers start empty for the tag it will be shown on.
        ApplyBinding(relayBindings_.TryGetValue(relay, out LogicRelayDto? binding) ? binding : null);
    }

    /// <summary>
    /// Updates this row's tag pickers and write policy from <paramref name="binding"/> — used
    /// both while loading and when the panel mirrors an edit onto the other rows of the relay.
    /// </summary>
    public void ApplyBinding(LogicRelayDto? binding)
    {
        applyingRelay_ = true;
        try
        {
            if (binding is null || !LogicRelays.IsBound(binding))
            {
                RelayWrite = LogicOptions.RelayWrites[0];
                LoadSourceAndItems(string.Empty, string.Empty);
                return;
            }

            RelayWrite = LogicSelectOption.Find(LogicOptions.RelayWrites, binding.Write) ?? LogicOptions.RelayWrites[0];
            LoadSourceAndItems(binding.SourceId, binding.ItemId);
        }
        finally
        {
            applyingRelay_ = false;
        }
    }

    /// <summary>
    /// The binding this row stands for: its relay with the tag and write policy as edited here,
    /// or null while the row names no relay.
    /// </summary>
    public LogicRelayDto? BindingFromRow()
    {
        string relay = Relay.Trim().ToUpperInvariant();
        if (relay.Length == 0)
        {
            return null;
        }

        string itemId = ItemId.Trim();
        return new LogicRelayDto
        {
            Name = relay,
            SourceId = itemId.Length == 0 ? string.Empty : SourceId,
            ItemId = itemId,
            Write = LogicRelayWrites.Normalize(RelayWrite?.Value)
        };
    }

    partial void OnSelectedSourceChanged(LogicSourceOption? value)
    {
        if (!loading_)
        {
            RebuildItems(string.Empty);
        }
    }

    partial void OnSelectedTagChanged(LogicTagOption? value) => OnPropertyChanged(nameof(ShowsRelayWrite));

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

    [RelayCommand(CanExecute = nameof(CanIndent))]
    private void IndentRow() => Indent = Math.Min(Indent + 1, LogicLimits.MaxElementDepth);

    private bool CanIndent() => CanEdit && Indent < LogicLimits.MaxElementDepth;

    [RelayCommand(CanExecute = nameof(CanOutdent))]
    private void OutdentRow() => Indent = Math.Max(Indent - 1, 0);

    private bool CanOutdent() => CanEdit && Indent > 0;

    [RelayCommand(CanExecute = nameof(CanRemoveRow))]
    private void RemoveElement() => onRemove_?.Invoke(this);

    private bool CanRemoveRow() => CanEdit;

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
        foreach (LogicTagOption item in catalog_.ItemsFor(sourceId))
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
