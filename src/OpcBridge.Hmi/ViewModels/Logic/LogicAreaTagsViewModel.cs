using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// The Logic page's Tags tab: the area's tags as the operator edits them — the named tags every
/// block of that area references, with the bridge tag each is bound to. One list is shared by
/// every block of the area, so a save here moves every one of them; an entry left unassigned is
/// created but not yet bound, which is exactly the "author the block now, assign the tags later"
/// flow.
/// </summary>
public sealed partial class LogicAreaTagsViewModel : ObservableObject
{
    private readonly HashSet<string> knownAreas_ = new(StringComparer.OrdinalIgnoreCase);
    private LogicTagCatalog catalog_ = LogicTagCatalog.Empty;
    private IReadOnlyList<LogicAreaTagsDto> lists_ = Array.Empty<LogicAreaTagsDto>();
    private IReadOnlyList<string> usedNames_ = Array.Empty<string>();
    private string savedJson_ = "null";
    private string loadedName_ = string.Empty;
    private bool loading_;

    public ObservableCollection<LogicTagEntryViewModel> Entries { get; } = new();

    /// <summary>Every area the bridge or the loaded blocks name, for the area field's suggestions.</summary>
    public ObservableCollection<string> AreaNames { get; } = new();

    /// <summary>Set by the panel: deletes this area's tags from the bridge (refused while blocks use them).</summary>
    public Func<Task>? DeleteSourceRequested { get; set; }

    /// <summary>Set by the panel: asks before an entry the block reads is removed.</summary>
    public Func<string, Task<bool>>? ConfirmRemoveEntry { get; set; }

    /// <summary>The area's name (e.g. "DRY END"); blank means the block has no shared tags.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _canEdit = true;

    [ObservableProperty]
    private bool _isKnownArea;

    [ObservableProperty]
    private int _usedByCount;

    public bool HasEntries => Entries.Count > 0;

    public bool IsNameBlank => Name.Trim().Length == 0;

    /// <summary>True while any loaded block carries this area's name.</summary>
    public bool HasUsedBy => UsedByCount > 0;

    /// <summary>The tab's empty state (nothing referenced, nothing created yet).</summary>
    public string EmptyHint => LogicText.AreaTagsEmpty;

    public string NameHint => LogicText.AreaTagsHint(IsNameBlank, IsKnownArea, Name.Trim());

    public string UsedByText => UsedByCount == 0
        ? string.Empty
        : UsedByCount == 1
            ? "used by 1 block"
            : $"used by {UsedByCount} blocks";

    /// <summary>The area's tags for a save; blank names are dropped by the bridge's store.</summary>
    public LogicAreaTagsDto ToDto() => new()
    {
        Name = Name.Trim(),
        Tags = Entries
            .Select(entry => entry.ToDto())
            .Where(tag => tag.Name.Length > 0)
            .ToList()
    };

    /// <summary>
    /// Loads one block's area: its stored tags when the bridge has them, plus an entry for
    /// every name the block references — a reference the area's tags do not hold is shown
    /// (unassigned) rather than hidden, so saving the list defines it and the block stops reading
    /// unknown.
    /// </summary>
    public void Load(
        LogicBlockDto block,
        IReadOnlyList<LogicAreaTagsDto> lists,
        IReadOnlyList<LogicBlockDto> allBlocks,
        LogicTagCatalog catalog)
    {
        loading_ = true;
        try
        {
            catalog_ = catalog;
            DetachEntries();
            Entries.Clear();

            // The block's area, else the legacy name it stores or its interlock group — the same
            // chain the bridge's store resolves, so the field shows exactly what will be used.
            string areaName = LogicAreaTags.AreaNameFor(block);

            AreaNames.Clear();
            knownAreas_.Clear();
            foreach (LogicAreaTagsDto list in lists)
            {
                string name = list.Name?.Trim() ?? string.Empty;
                if (name.Length > 0 && knownAreas_.Add(name))
                {
                    AreaNames.Add(name);
                }
            }

            // An area a block carries but no list exists for yet is still offered: authoring a
            // new area's tags starts by typing its name once, here.
            foreach (LogicBlockDto loaded in allBlocks)
            {
                string area = loaded.Area?.Trim() ?? string.Empty;
                if (area.Length > 0 && knownAreas_.Add(area))
                {
                    AreaNames.Add(area);
                }
            }

            loadedName_ = areaName;
            lists_ = lists;
            usedNames_ = LogicAreaTags.UsedNames(block);
            Name = areaName;

            LogicAreaTagsDto? stored = LogicAreaTags.Find(lists, areaName);
            foreach (LogicTagDto tag in stored?.Tags ?? Enumerable.Empty<LogicTagDto>())
            {
                AddEntry(tag);
            }

            foreach (string used in usedNames_)
            {
                if (FindByName(used) is null)
                {
                    AddEntry(new LogicTagDto { Name = used });
                }
            }

            // The loaded draft is the baseline: entries created for a reference the bridge's
            // list does not hold yet are the work to do, not an unsaved edit the moment the
            // block opens.
            MarkSaved(ToDto());
            RefreshUsed(usedNames_);
            RefreshUsedCount(allBlocks);
        }
        finally
        {
            loading_ = false;
        }

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsNameBlank));
        OnPropertyChanged(nameof(NameHint));
    }

    /// <summary>The saved state for the dirty compare: the bridge's own list, or the empty draft.</summary>
    public void MarkSaved(LogicAreaTagsDto stored)
    {
        savedJson_ = Serialize(stored);
        RecomputeDirty();
    }

    /// <summary>A new empty entry (unassigned), named so it is addressable before its tag exists.</summary>
    public LogicTagEntryViewModel NewTag()
    {
        int number = 1;
        while (FindByName("TAG" + number) is not null)
        {
            number++;
        }

        LogicTagEntryViewModel entry = AddEntry(new LogicTagDto { Name = "TAG" + number });

        // Ready to bind: the first source is chosen, so the TAG cell lists its mapped tags straight
        // away instead of an empty box. The name and the source both stay the operator's to change.
        entry.SelectedSource = entry.Sources.FirstOrDefault();
        RecomputeDirty();
        return entry;
    }

    /// <summary>Creates an entry for a tag a row picked, or reuses the entry already carrying it.</summary>
    public LogicTagEntryViewModel EnsureEntry(string name, string sourceId, string itemId)
    {
        LogicTagEntryViewModel? existing = Entries.FirstOrDefault(entry =>
            entry.IsAssigned
            && string.Equals(entry.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        LogicTagEntryViewModel created = AddEntry(new LogicTagDto { Name = name, SourceId = sourceId, ItemId = itemId });
        RecomputeDirty();
        return created;
    }

    /// <summary>
    /// Keeps a row's reference resolvable when the area changed: an entry the adopted list does not
    /// hold is added back with its binding, and a same-named entry still unassigned picks the
    /// binding up — so re-scoping never turns a working row into "not assigned". An entry the list
    /// already carries stays as it is: the area's own spelling and binding are the authority.
    /// </summary>
    public LogicTagEntryViewModel EnsureReference(string name, string sourceId, string itemId)
    {
        LogicTagEntryViewModel? existing = FindByName(name);
        if (existing is not null)
        {
            if (!existing.IsAssigned && sourceId.Length > 0 && itemId.Length > 0)
            {
                existing.Assign(sourceId, itemId);
            }

            return existing;
        }

        LogicTagEntryViewModel created = AddEntry(new LogicTagDto { Name = name, SourceId = sourceId, ItemId = itemId });
        RecomputeDirty();
        return created;
    }

    public LogicTagEntryViewModel? FindByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string wanted = name.Trim();
        return Entries.FirstOrDefault(entry => string.Equals(entry.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Refreshes every entry's live chip from the bridge's tag cache.</summary>
    public void RefreshLive(string bridgeId, MultiBridgeTagCache cache)
    {
        foreach (LogicTagEntryViewModel entry in Entries)
        {
            MultiBridgeTagEntry? live = null;
            if (entry.IsAssigned)
            {
                cache.TryGet(TagBindingKey.Create(bridgeId, entry.SourceId, entry.ItemId), out live);
            }

            entry.ApplyLive(live);
        }
    }

    /// <summary>Marks the entries the block being edited references right now.</summary>
    public void RefreshUsed(IEnumerable<string> usedNames)
    {
        HashSet<string> used = new(usedNames, StringComparer.OrdinalIgnoreCase);
        foreach (LogicTagEntryViewModel entry in Entries)
        {
            entry.UsedByThisBlock = used.Contains(entry.Name.Trim());
        }
    }

    public void SetCanEdit(bool canEdit)
    {
        CanEdit = canEdit;
        foreach (LogicTagEntryViewModel entry in Entries)
        {
            entry.CanEdit = canEdit;
        }

        DeleteSourceCommand.NotifyCanExecuteChanged();
        NewTagEntryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>How many loaded blocks read this area's name — the sharing is never implicit.</summary>
    public void RefreshUsedCount(IReadOnlyList<LogicBlockDto> allBlocks)
    {
        string name = Name.Trim();
        UsedByCount = name.Length == 0
            ? 0
            : allBlocks.Count(block =>
                string.Equals(LogicAreaTags.AreaNameFor(block), name, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnNameChanged(string value)
    {
        IsKnownArea = knownAreas_.Contains(value.Trim());
        OnPropertyChanged(nameof(IsNameBlank));
        OnPropertyChanged(nameof(NameHint));
        AdoptNamedArea();
        RecomputeDirty();
    }

    /// <summary>
    /// Switching the area shows that area's tags: the entries are re-read from its list, so typing
    /// "DRY END" lands on DRY END's tags. The panel re-anchors the rows onto the adopted list right
    /// after, so a reference the new list does not hold is added back with its binding. A name the
    /// bridge does not hold yet keeps the draft — that is the "author a new area's tags" case, and
    /// Save creates the list with the entries as they stand.
    /// </summary>
    private void AdoptNamedArea()
    {
        if (loading_)
        {
            return;
        }

        string name = Name.Trim();
        if (string.Equals(name, loadedName_, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LogicAreaTagsDto? stored = LogicAreaTags.Find(lists_, name);
        if (stored is null)
        {
            return;
        }

        loadedName_ = name;
        DetachEntries();
        Entries.Clear();
        foreach (LogicTagDto tag in stored.Tags)
        {
            AddEntry(tag);
        }

        OnPropertyChanged(nameof(HasEntries));
    }

    partial void OnUsedByCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUsedBy));
        OnPropertyChanged(nameof(UsedByText));
    }

    [RelayCommand(CanExecute = nameof(CanEditSource))]
    private void NewTagEntry() => NewTag();

    [RelayCommand(CanExecute = nameof(CanEditSource))]
    private Task DeleteSource() => DeleteSourceRequested?.Invoke() ?? Task.CompletedTask;

    private bool CanEditSource() => CanEdit;

    private LogicTagEntryViewModel AddEntry(LogicTagDto tag)
    {
        var entry = new LogicTagEntryViewModel(tag, catalog_, entry => _ = RemoveTagAsync(entry));
        entry.PropertyChanged += OnEntryChanged;
        Entries.Add(entry);
        OnPropertyChanged(nameof(HasEntries));
        return entry;
    }

    /// <summary>
    /// Removes an entry. One the block reads asks first: the rows reference it, and without it
    /// they have nothing to read (a save would be refused until another tag is picked).
    /// </summary>
    private async Task RemoveTagAsync(LogicTagEntryViewModel entry)
    {
        if (entry.UsedByThisBlock && ConfirmRemoveEntry is { } confirm
            && !await confirm(entry.Name.Trim()).ConfigureAwait(true))
        {
            return;
        }

        RemoveEntry(entry);
    }

    /// <summary>Removes one entry (the row's remove button; the block's references then read unknown).</summary>
    public void RemoveEntry(LogicTagEntryViewModel entry)
    {
        entry.PropertyChanged -= OnEntryChanged;
        Entries.Remove(entry);
        OnPropertyChanged(nameof(HasEntries));
        RecomputeDirty();
    }

    private void DetachEntries()
    {
        foreach (LogicTagEntryViewModel entry in Entries)
        {
            entry.PropertyChanged -= OnEntryChanged;
        }
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(LogicTagEntryViewModel.LiveText), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(LogicTagEntryViewModel.IsLiveOk), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(LogicTagEntryViewModel.IsLiveBad), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(LogicTagEntryViewModel.IsLiveWarn), StringComparison.Ordinal))
        {
            // A live reading is not an edit.
            return;
        }

        RecomputeDirty();
    }

    private void RecomputeDirty()
    {
        bool dirty = !string.Equals(Serialize(ToDto()), savedJson_, StringComparison.Ordinal);
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
        }
    }

    private static string Serialize(LogicAreaTagsDto list) => JsonSerializer.Serialize(list);
}
