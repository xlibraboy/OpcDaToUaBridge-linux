using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// One entry of an area's tags as the Tags tab edits it: the reference name a block's
/// rows carry (e.g. "START_PB") and the bridge tag it is bound to. An entry may be created empty
/// and assigned later — its readers evaluate as unknown until then, never as a silent false — so
/// "not assigned" is a normal, visible state rather than an error.
/// </summary>
public partial class LogicTagEntryViewModel : ObservableObject
{
    private readonly LogicTagCatalog catalog_;
    private readonly Action<LogicTagEntryViewModel>? onRemove_;
    private bool loading_;

    public LogicTagEntryViewModel(
        LogicTagDto tag,
        LogicTagCatalog catalog,
        Action<LogicTagEntryViewModel>? onRemove = null)
    {
        catalog_ = catalog;
        onRemove_ = onRemove;
        Name = tag.Name;
        LoadSourceAndItems(tag.SourceId, tag.ItemId);
    }

    public ObservableCollection<LogicSourceOption> Sources { get; } = new();

    public ObservableCollection<LogicTagOption> Items { get; } = new();

    /// <summary>The reference name rows carry; matched case-insensitively by the bridge.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private LogicSourceOption? _selectedSource;

    [ObservableProperty]
    private LogicTagOption? _selectedTag;

    /// <summary>Live value chip, refreshed with the page's state poll.</summary>
    [ObservableProperty]
    private string _liveText = "—";

    [ObservableProperty]
    private bool _isLiveOk;

    [ObservableProperty]
    private bool _isLiveBad;

    [ObservableProperty]
    private bool _isLiveWarn = true;

    /// <summary>True while the block being edited references this entry.</summary>
    [ObservableProperty]
    private bool _usedByThisBlock;

    [ObservableProperty]
    private bool _canEdit = true;

    public string SourceId => SelectedSource?.SourceId ?? string.Empty;

    public string ItemId => SelectedTag?.ItemId ?? string.Empty;

    /// <summary>A named, unassigned entry is the "create the empty tags, assign later" state.</summary>
    public bool IsAssigned => ItemId.Length > 0;

    public string TagText => IsAssigned
        ? (string.IsNullOrWhiteSpace(SelectedTag?.Detail) ? ItemId : SelectedTag!.Detail)
        : LogicText.TagNotAssigned;

    /// <summary>The stored entry for a save; a blank name is dropped by the bridge's store.</summary>
    public LogicTagDto ToDto() => new()
    {
        Name = Name.Trim(),
        SourceId = SourceId,
        ItemId = ItemId
    };

    /// <summary>Applies the live cache reading (null = not on the bridge, or not assigned yet).</summary>
    public void ApplyLive(MultiBridgeTagEntry? entry)
    {
        LiveText = LogicText.TagLiveChip(entry);
        bool bad = entry is { IsGood: false };
        bool hasReading = entry is { Value: not null };
        IsLiveOk = !bad && hasReading;
        IsLiveBad = bad;
        IsLiveWarn = !IsLiveOk && !IsLiveBad;
    }

    partial void OnCanEditChanged(bool value) => RemoveCommand.NotifyCanExecuteChanged();

    partial void OnSelectedSourceChanged(LogicSourceOption? value)
    {
        if (!loading_)
        {
            RebuildItems(string.Empty);
        }
    }

    partial void OnSelectedTagChanged(LogicTagOption? value) => OnPropertyChanged(nameof(TagText));

    [RelayCommand(CanExecute = nameof(CanRemoveEntry))]
    private void Remove() => onRemove_?.Invoke(this);

    private bool CanRemoveEntry() => CanEdit;

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
                source => string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                ?? (Sources.Count > 0 && itemId.Length > 0 ? Sources[0] : null);
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
