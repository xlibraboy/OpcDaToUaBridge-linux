using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One editable condition row (the simple form). Source and tag are picked from a metadata
/// snapshot; a stored tag that has left the cache stays selectable as its own entry, so a save
/// cannot silently drop the reference.
/// </summary>
public partial class LogicConditionRowViewModel : ObservableObject
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicConditionRowViewModel>? onRemove_;
    private bool loading_;

    public LogicConditionRowViewModel(
        LogicConditionDto condition,
        LogicTagCatalog catalog,
        Action<LogicConditionRowViewModel>? onRemove = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
        ConditionId = condition.Id == Guid.Empty ? Guid.NewGuid() : condition.Id;
        Text = condition.Text;
        Op = LogicSelectOption.Find(LogicOptions.Ops, condition.Op) ?? LogicOptions.Ops[0];
        ValueText = condition.Value is { } value ? FormatNumber(value) : string.Empty;
        Group = condition.Group;
        HoldSeconds = condition.HoldMs > 0 ? FormatNumber(condition.HoldMs / 1000.0) : string.Empty;
        Severity = LogicSelectOption.Find(LogicOptions.Severities, condition.Severity) ?? LogicOptions.Severities[0];
        NextStepText = condition.NextStepText ?? string.Empty;
        LoadSourceAndItems(condition.SourceId, condition.ItemId);
    }

    public Guid ConditionId { get; }

    public IReadOnlyList<LogicSelectOption> OpOptions => LogicOptions.Ops;

    public IReadOnlyList<LogicSelectOption> SeverityOptions => LogicOptions.Severities;

    public ObservableCollection<LogicSourceOption> Sources { get; } = new();

    public ObservableCollection<LogicTagOption> Items { get; } = new();

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private LogicSelectOption _op;

    [ObservableProperty]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string _group = string.Empty;

    [ObservableProperty]
    private string _holdSeconds = string.Empty;

    [ObservableProperty]
    private LogicSelectOption _severity;

    [ObservableProperty]
    private string _nextStepText = string.Empty;

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

    /// <summary>The stored source id, "" when nothing is picked.</summary>
    public string SourceId => SelectedSource?.SourceId ?? string.Empty;

    /// <summary>The stored item id, "" when nothing is picked.</summary>
    public string ItemId => SelectedTag?.ItemId ?? string.Empty;

    /// <summary>True for the numeric operators — the value box is only meaningful then.</summary>
    public bool NeedsValue => LogicConditionOps.RequiresValue(Op?.Value);

    partial void OnOpChanged(LogicSelectOption value) => OnPropertyChanged(nameof(NeedsValue));

    partial void OnSelectedSourceChanged(LogicSourceOption? value)
    {
        if (!loading_)
        {
            RebuildItems(string.Empty);
        }
    }

    /// <summary>Applies the evaluated state to the live chip (1 / 0 / no data, or the live value).</summary>
    public void ApplyState(LogicElementStateDto? state)
    {
        LiveText = LogicText.LiveChip(state, Op?.Value);
        string key = LogicText.ConditionStateKey(state?.State);
        IsLiveOk = key == "ok";
        IsLiveBad = key == "bad";
        IsLiveWarn = key == "warn";
    }

    [RelayCommand]
    private void Remove() => onRemove_?.Invoke(this);

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

    internal static string FormatNumber(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}
