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
/// block just its label.
/// </summary>
public partial class LogicElementRowViewModel : ObservableObject
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicElementRowViewModel>? onRemove_;
    private bool loading_;

    public LogicElementRowViewModel(
        LogicElementDto element,
        int indent,
        LogicTagCatalog catalog,
        Action<LogicElementRowViewModel>? onRemove = null,
        IEnumerable<string>? relayNames = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
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
        LoadSourceAndItems(element.SourceId, element.ItemId);
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

    /// <summary>The relay field: a relay contact reads one, a coil drives one.</summary>
    public bool ShowsRelay => IsRelayContact || IsCoil;

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
        OnPropertyChanged(nameof(ShowsRelay));
        OnPropertyChanged(nameof(ShowsSeverity));
        OnPropertyChanged(nameof(ShowsLabelOnly));
        OnPropertyChanged(nameof(NeedsValue));
    }

    partial void OnOpChanged(LogicSelectOption value) => OnPropertyChanged(nameof(NeedsValue));

    partial void OnRelayChanged(string value)
    {
        OnPropertyChanged(nameof(IsRelayContact));
        OnPropertyChanged(nameof(ShowsTag));
        OnPropertyChanged(nameof(ShowsRelay));

        // A relay contact reads a bit: a numeric comparison has nothing to compare against.
        if (value.Trim().Length > 0 && LogicConditionOps.RequiresValue(Op?.Value))
        {
            Op = LogicOptions.Ops[0];
        }
    }

    partial void OnSelectedSourceChanged(LogicSourceOption? value)
    {
        if (!loading_)
        {
            RebuildItems(string.Empty);
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
