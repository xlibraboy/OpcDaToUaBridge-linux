using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One block-level action button (label, tag, value, confirm). The phone renders these as
/// operator buttons; the HMI page authors them only — the write itself stays on the phone.
/// Only writeable tags are offered.
/// </summary>
public partial class LogicActionRowViewModel : ObservableObject
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicActionRowViewModel>? onRemove_;
    private bool loading_;

    public LogicActionRowViewModel(
        LogicActionDto action,
        LogicTagCatalog catalog,
        Action<LogicActionRowViewModel>? onRemove = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
        Label = action.Label;
        Value = action.Value;
        Confirm = action.Confirm;
        LoadSourceAndItems(action.SourceId, action.ItemId);
    }

    public ObservableCollection<LogicSourceOption> Sources { get; } = new();

    public ObservableCollection<LogicTagOption> Items { get; } = new();

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _confirm = true;

    [ObservableProperty]
    private LogicSourceOption? _selectedSource;

    [ObservableProperty]
    private LogicTagOption? _selectedTag;

    public string SourceId => SelectedSource?.SourceId ?? string.Empty;

    public string ItemId => SelectedTag?.ItemId ?? string.Empty;

    partial void OnSelectedSourceChanged(LogicSourceOption? value)
    {
        if (!loading_)
        {
            RebuildItems(string.Empty);
        }
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
        foreach (LogicTagOption item in catalog_.ItemsFor(sourceId, writeableOnly: true))
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
