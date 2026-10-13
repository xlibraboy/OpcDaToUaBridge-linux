using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// The Logic page's Tags tab: the machine's tags source as the operator edits it — the named
/// tags every block of that machine references, with the bridge tag each is bound to. One source
/// is shared by every block carrying its name, so a save here moves every one of them; an entry
/// left unassigned is created but not yet bound, which is exactly the "author the block now,
/// assign the tags later" flow.
/// </summary>
public sealed partial class LogicTagSourceViewModel : ObservableObject
{
    private readonly HashSet<string> knownSources_ = new(StringComparer.OrdinalIgnoreCase);
    private LogicTagCatalog catalog_ = LogicTagCatalog.Empty;
    private IReadOnlyList<LogicTagSourceDto> sources_ = Array.Empty<LogicTagSourceDto>();
    private IReadOnlyList<string> usedNames_ = Array.Empty<string>();
    private string savedJson_ = "null";
    private string group_ = string.Empty;
    private string loadedSourceName_ = string.Empty;
    private bool loading_;

    public ObservableCollection<LogicTagEntryViewModel> Entries { get; } = new();

    /// <summary>Every tags source the bridge already holds, for the name field's suggestions.</summary>
    public ObservableCollection<string> SourceNames { get; } = new();

    /// <summary>Set by the panel: converts this block's direct tags into entries of this source.</summary>
    public Func<Task>? MoveBlockTagsRequested { get; set; }

    /// <summary>Set by the panel: deletes this source from the bridge (refused while blocks use it).</summary>
    public Func<Task>? DeleteSourceRequested { get; set; }

    /// <summary>Set by the panel: asks before an entry the block reads is removed.</summary>
    public Func<string, Task<bool>>? ConfirmRemoveEntry { get; set; }

    /// <summary>The machine's name (e.g. "DRYEND"); blank means the block has no shared tags.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _canEdit = true;

    [ObservableProperty]
    private bool _isKnownSource;

    [ObservableProperty]
    private int _usedByCount;

    public bool HasEntries => Entries.Count > 0;

    public bool IsNameBlank => Name.Trim().Length == 0;

    /// <summary>True while any loaded block carries this machine's name.</summary>
    public bool HasUsedBy => UsedByCount > 0;

    /// <summary>The tab's empty state (nothing referenced, nothing created yet).</summary>
    public string EmptyHint => LogicText.TagSourceEmpty;

    public string NameHint => LogicText.TagSourceHint(IsNameBlank, IsKnownSource, Name.Trim());

    public string UsedByText => UsedByCount == 0
        ? string.Empty
        : UsedByCount == 1
            ? "used by 1 block"
            : $"used by {UsedByCount} blocks";

    /// <summary>The machine's tags for a save; blank names are dropped by the bridge's store.</summary>
    public LogicTagSourceDto ToDto() => new()
    {
        Name = Name.Trim(),
        Tags = Entries
            .Select(entry => entry.ToDto())
            .Where(tag => tag.Name.Length > 0)
            .ToList()
    };

    /// <summary>
    /// Loads one block's machine: its stored source when the bridge has one, plus an entry for
    /// every name the block references — a reference the source does not hold is shown (unassigned)
    /// rather than hidden, so saving the source defines it and the block stops reading unknown.
    /// </summary>
    public void Load(
        LogicBlockDto block,
        IReadOnlyList<LogicTagSourceDto> sources,
        IReadOnlyList<LogicBlockDto> allBlocks,
        LogicTagCatalog catalog)
    {
        loading_ = true;
        try
        {
            catalog_ = catalog;
            DetachEntries();
            Entries.Clear();

            string sourceName = block.TagSource?.Trim() ?? string.Empty;
            if (sourceName.Length == 0)
            {
                // The store falls back to the block's group, so the field shows what will be used.
                sourceName = block.Group?.Trim() ?? string.Empty;
            }

            SourceNames.Clear();
            knownSources_.Clear();
            foreach (LogicTagSourceDto source in sources)
            {
                string name = source.Name?.Trim() ?? string.Empty;
                if (name.Length > 0 && knownSources_.Add(name) && !SourceNames.Contains(name))
                {
                    SourceNames.Add(name);
                }
            }

            group_ = block.Group?.Trim() ?? string.Empty;
            loadedSourceName_ = sourceName;
            sources_ = sources;
            usedNames_ = LogicTagSources.UsedNames(block);
            Name = sourceName;

            LogicTagSourceDto? stored = LogicTagSources.FindSource(sources, sourceName);
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
            // source does not hold yet are the work to do, not an unsaved edit the moment the
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

    /// <summary>
    /// The block's group changed: while the machine's name was still the group (or empty), it
    /// follows it — filing a block under "DRYEND" puts it on that machine's tags without a second
    /// place to type it. A name the author set apart from the group is never overwritten.
    /// </summary>
    public bool FollowsGroup(string? group)
    {
        string current = group?.Trim() ?? string.Empty;
        string name = Name.Trim();
        bool follows = name.Length == 0 || string.Equals(name, group_, StringComparison.OrdinalIgnoreCase);
        group_ = current;
        return follows;
    }

    /// <summary>The saved state for the dirty compare: the bridge's own source, or the empty draft.</summary>
    public void MarkSaved(LogicTagSourceDto stored)
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
        RecomputeDirty();
        return entry;
    }

    /// <summary>Creates an entry for a direct tag the block used to read, or reuses the one carrying it.</summary>
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

        MoveBlockTagsCommand.NotifyCanExecuteChanged();
        DeleteSourceCommand.NotifyCanExecuteChanged();
        NewTagEntryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>How many loaded blocks carry this machine's name — the sharing is never implicit.</summary>
    public void RefreshUsedCount(IReadOnlyList<LogicBlockDto> allBlocks)
    {
        string name = Name.Trim();
        UsedByCount = name.Length == 0
            ? 0
            : allBlocks.Count(block =>
            {
                string blockSource = block.TagSource?.Trim() ?? string.Empty;
                if (blockSource.Length == 0)
                {
                    blockSource = block.Group?.Trim() ?? string.Empty;
                }

                return string.Equals(blockSource, name, StringComparison.OrdinalIgnoreCase);
            });
    }

    partial void OnNameChanged(string value)
    {
        IsKnownSource = knownSources_.Contains(value.Trim());
        OnPropertyChanged(nameof(IsNameBlank));
        OnPropertyChanged(nameof(NameHint));
        AdoptNamedSource();
        RecomputeDirty();
    }

    /// <summary>
    /// Switching the machine shows that machine's tags: the entries are re-read from its source
    /// (with an entry for every name this block references), so filing a block under "DRYEND"
    /// lands on DRYEND's tags. A name the bridge does not hold yet keeps the draft — that is the
    /// "author a new machine's tags" case.
    /// </summary>
    private void AdoptNamedSource()
    {
        if (loading_)
        {
            return;
        }

        string name = Name.Trim();
        if (string.Equals(name, loadedSourceName_, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LogicTagSourceDto? stored = LogicTagSources.FindSource(sources_, name);
        if (stored is null)
        {
            return;
        }

        loadedSourceName_ = name;
        DetachEntries();
        Entries.Clear();
        foreach (LogicTagDto tag in stored.Tags)
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
    private Task MoveBlockTags() => MoveBlockTagsRequested?.Invoke() ?? Task.CompletedTask;

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

    private static string Serialize(LogicTagSourceDto source) => JsonSerializer.Serialize(source);
}
