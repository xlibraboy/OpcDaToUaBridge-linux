using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One editable element row of the IEC 61131-3 network editor. Rows nest by indentation: a row's
/// inputs are the rows under it at a deeper level, rebuilt into the tree on save. The fields shown
/// follow the selected kind — a contact reads one of its area's named tags (the Tags tab's
/// entries) or an internal relay when a relay is typed, a coil drives an internal relay, a timer
/// its preset, a counter its preset, a gate or function block just its label. A stored direct
/// (source, tag) pair is carried through a save until the area's list holds it, and reads as its
/// own line while it is not in that list.
/// </summary>
public partial class LogicElementRowViewModel : ObservableObject, ILogicOrderRow
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicElementRowViewModel>? onRemove_;
    private readonly LogicRowList<LogicElementRowViewModel>? rowList_;
    private readonly string storedSourceId_;
    private readonly string storedItemId_;

    public LogicElementRowViewModel(
        LogicElementDto element,
        int indent,
        LogicTagCatalog catalog,
        Action<LogicElementRowViewModel>? onRemove = null,
        IEnumerable<string>? relayNames = null,
        IEnumerable<LogicTagEntryViewModel>? tagEntries = null,
        LogicRowList<LogicElementRowViewModel>? rowList = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
        rowList_ = rowList;
        storedSourceId_ = element.SourceId ?? string.Empty;
        storedItemId_ = element.ItemId ?? string.Empty;
        RelayNames = relayNames ?? Array.Empty<string>();
        ElementId = element.Id == Guid.Empty ? Guid.NewGuid() : element.Id;
        Kind = LogicSelectOption.Find(LogicOptions.ElementKinds, element.Kind) ?? LogicOptions.ElementKinds[0];
        Text = element.Text;
        Op = LogicSelectOption.Find(LogicOptions.Ops, element.Op) ?? LogicOptions.Ops[0];
        Relay = element.Relay;
        ValueText = element.Value is { } value ? LogicConditionRowViewModel.FormatNumber(value) : string.Empty;
        PtSeconds = element.PtMs > 0 ? LogicConditionRowViewModel.FormatNumber(element.PtMs / 1000.0) : string.Empty;
        PvText = element.Pv > 0 ? element.Pv.ToString(CultureInfo.InvariantCulture) : string.Empty;
        Severity = LogicSelectOption.Find(LogicOptions.Severities, element.Severity) ?? LogicOptions.Severities[0];
        Indent = Math.Clamp(indent, 0, LogicLimits.MaxElementDepth);
        TagEntries = tagEntries ?? Array.Empty<LogicTagEntryViewModel>();
        SelectedBlockTag = FindTagEntry(element.Tag);
    }

    public Guid ElementId { get; }

    /// <summary>Relay names already driven by a coil in any block, offered while typing one.</summary>
    public IEnumerable<string> RelayNames { get; }

    public IReadOnlyList<LogicSelectOption> KindOptions => LogicOptions.ElementKinds;

    public IReadOnlyList<LogicSelectOption> OpOptions => LogicOptions.Ops;

    public IReadOnlyList<LogicSelectOption> SeverityOptions => LogicOptions.Severities;

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IndentRowCommand))]
    [NotifyCanExecuteChangedFor(nameof(OutdentRowCommand))]
    private int _indent;

    [ObservableProperty]
    private string _liveText = "—";

    [ObservableProperty]
    private bool _isLiveOk;

    [ObservableProperty]
    private bool _isLiveBad;

    [ObservableProperty]
    private bool _isLiveWarn = true;

    /// <summary>
    /// The direct pair the contact was stored with, "" when it is not a legacy row. It is not
    /// edited here any more; it is carried through a save while the area's tags do not hold it yet.
    /// </summary>
    public string SourceId => storedSourceId_;

    public string ItemId => storedItemId_;

    public bool IsContact => string.Equals(Kind?.Value, LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase);

    public bool IsTimer => LogicElementKinds.IsTimer(Kind?.Value);

    public bool IsCounter => LogicElementKinds.IsCounter(Kind?.Value);

    public bool IsCoil => LogicElementKinds.IsCoil(Kind?.Value);

    /// <summary>A contact that reads an internal relay instead of a tag.</summary>
    public bool IsRelayContact => IsContact && Relay.Trim().Length > 0;

    /// <summary>The area's named tags a tag contact may read — the ones the Tags tab assigns.</summary>
    public IEnumerable<LogicTagEntryViewModel> TagEntries { get; }

    [ObservableProperty]
    private LogicTagEntryViewModel? _selectedBlockTag;

    /// <summary>The stored area-tags reference, "" while the contact carries no name.</summary>
    public string Tag => SelectedBlockTag?.Name.Trim() ?? string.Empty;

    public bool HasBlockTag => Tag.Length > 0;

    /// <summary>A tag contact: a contact that is not reading an internal relay.</summary>
    public bool IsTagContact => IsContact && !IsRelayContact;

    /// <summary>The named-tag picker: a contact (a relay contact reads a coil's relay instead).</summary>
    public bool ShowsBlockTag => IsContact;

    /// <summary>A legacy tag contact waiting for the area's tags to hold it: shown, never edited here.</summary>
    public bool HasStoredTag => IsTagContact && !HasBlockTag && (SourceId.Length > 0 || ItemId.Length > 0);

    /// <summary>What a legacy contact reads, while the area's list has no entry for it.</summary>
    public string StoredTagText => LogicText.StoredTagHint(catalog_.TagLabel(SourceId, ItemId));

    /// <summary>The relay field: a relay contact reads one, a coil drives one.</summary>
    public bool ShowsRelay => IsRelayContact || IsCoil;

    /// <summary>A coil is an output — "warn only" has no meaning for it.</summary>
    public bool ShowsSeverity => !IsCoil;

    /// <summary>A gate, latch or edge trigger: a label only, no tag, preset or value.</summary>
    public bool ShowsLabelOnly => !IsContact && !IsCoil && !IsTimer && !IsCounter;

    public bool NeedsValue => IsContact && LogicConditionOps.RequiresValue(Op?.Value);

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
        ClearBlockTagCommand.NotifyCanExecuteChanged();
        NotifyOrderChanged();
    }

    partial void OnKindChanged(LogicSelectOption value)
    {
        OnPropertyChanged(nameof(IsContact));
        OnPropertyChanged(nameof(IsTimer));
        OnPropertyChanged(nameof(IsCounter));
        OnPropertyChanged(nameof(IsCoil));
        OnPropertyChanged(nameof(IsRelayContact));
        OnPropertyChanged(nameof(IsTagContact));
        OnPropertyChanged(nameof(ShowsBlockTag));
        OnPropertyChanged(nameof(HasStoredTag));
        OnPropertyChanged(nameof(ShowsRelay));
        OnPropertyChanged(nameof(ShowsSeverity));
        OnPropertyChanged(nameof(ShowsLabelOnly));
        OnPropertyChanged(nameof(NeedsValue));
    }

    partial void OnOpChanged(LogicSelectOption value) => OnPropertyChanged(nameof(NeedsValue));

    partial void OnRelayChanged(string value)
    {
        OnPropertyChanged(nameof(IsRelayContact));
        OnPropertyChanged(nameof(IsTagContact));
        OnPropertyChanged(nameof(HasStoredTag));
        OnPropertyChanged(nameof(ShowsRelay));

        // A relay contact reads a bit: a numeric comparison has nothing to compare against.
        if (value.Trim().Length > 0 && LogicConditionOps.RequiresValue(Op?.Value))
        {
            Op = LogicOptions.Ops[0];
        }
    }

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
    private void IndentRow()
    {
        Indent = Math.Min(Indent + 1, LogicLimits.MaxElementDepth);
        rowList_?.NotifyChanged();
    }

    private bool CanIndent() => CanEdit && Indent < LogicLimits.MaxElementDepth;

    [RelayCommand(CanExecute = nameof(CanOutdent))]
    private void OutdentRow()
    {
        Indent = Math.Max(Indent - 1, 0);
        rowList_?.NotifyChanged();
    }

    private bool CanOutdent() => CanEdit && Indent > 0;

    [RelayCommand(CanExecute = nameof(CanRemoveRow))]
    private void RemoveElement() => onRemove_?.Invoke(this);

    private bool CanRemoveRow() => CanEdit;

    /// <summary>A fresh element above this row, at its own nesting level.</summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertAbove() => rowList_?.InsertBeside(this, above: true);

    /// <summary>A fresh element below this row's whole group, at the same nesting level.</summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertBelow() => rowList_?.InsertBeside(this, above: false);

    /// <summary>Moves this row — and the rows nested under it — past the sibling above it.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveRowUp))]
    private void MoveUp() => rowList_?.MoveUp(this);

    [RelayCommand(CanExecute = nameof(CanMoveRowDown))]
    private void MoveDown() => rowList_?.MoveDown(this);

    private bool CanInsert() => CanEdit && rowList_?.CanInsert(this) == true;

    private bool CanMoveRowUp() => CanEdit && rowList_?.CanMoveUp(this) == true;

    private bool CanMoveRowDown() => CanEdit && rowList_?.CanMoveDown(this) == true;

    /// <summary>Keeps the row's order buttons in step with its place in the tree.</summary>
    public void NotifyOrderChanged()
    {
        InsertAboveCommand.NotifyCanExecuteChanged();
        InsertBelowCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
