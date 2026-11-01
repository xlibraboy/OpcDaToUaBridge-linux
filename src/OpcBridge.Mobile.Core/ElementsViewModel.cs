using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One block's elements under a collapsible heading on the Elements screen. The heading keeps
/// the block's state and element count while its rows are folded, like the Logic list's groups.
/// </summary>
public sealed class ElementBlockGroupViewModel : ObservableCollection<LogicElementRowViewModel>
{
    private readonly List<LogicElementRowViewModel> members_ = new();
    private List<LogicElementRowViewModel> visible_ = new();
    private RelayCommand? toggle_command_;
    private bool expanded_ = true;
    private string state_label_ = "no data";
    private string state_key_ = LogicBlockStates.Unknown;
    private string summary_ = string.Empty;

    public ElementBlockGroupViewModel(LogicBlockDto block)
    {
        BlockId = block.Id;
        Name = block.Name;
        KindLabel = KindOf(block);
        GroupLabel = (block.Group ?? string.Empty).Trim();
        Area = (block.Area ?? string.Empty).Trim();
    }

    public Guid BlockId { get; }

    public string Name { get; }

    /// <summary>"Interlock" / "Permissive" / "Sequence".</summary>
    public string KindLabel { get; }

    public string GroupLabel { get; }

    public string Area { get; }

    public bool HasArea => Area.Length > 0;

    public bool HasGroup => GroupLabel.Length > 0;

    /// <summary>"▾" / "▸" — the heading reports its own state without colour.</summary>
    public string Chevron => expanded_ ? "▾" : "▸";

    public IRelayCommand ToggleCommand => toggle_command_ ??= new RelayCommand(() => IsExpanded = !IsExpanded);

    public bool IsExpanded
    {
        get => expanded_;
        set
        {
            if (expanded_ == value)
            {
                return;
            }

            expanded_ = value;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsExpanded)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Chevron)));
            SyncItems();
        }
    }

    /// <summary>"3 elements · 1 blocked" — what the heading shows while collapsed.</summary>
    public string Summary
    {
        get => summary_;
        private set
        {
            if (!string.Equals(summary_, value, StringComparison.Ordinal))
            {
                summary_ = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Summary)));
            }
        }
    }

    public string StateLabel
    {
        get => state_label_;
        private set
        {
            if (!string.Equals(state_label_, value, StringComparison.Ordinal))
            {
                state_label_ = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(StateLabel)));
            }
        }
    }

    public string StateKey
    {
        get => state_key_;
        private set
        {
            if (!string.Equals(state_key_, value, StringComparison.Ordinal))
            {
                state_key_ = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(StateKey)));
            }
        }
    }

    /// <summary>The full row set; the bound items are its filtered/visible subset.</summary>
    internal IReadOnlyList<LogicElementRowViewModel> Members => members_;

    internal void SetMembers(IReadOnlyList<LogicElementRowViewModel> members)
    {
        members_.Clear();
        members_.AddRange(members);
        visible_ = members_.ToList();
        SyncItems();
        UpdateSummary();
    }

    /// <summary>Binds only the rows the search and kind filter leave visible.</summary>
    internal void SetVisible(IReadOnlyList<LogicElementRowViewModel> visible)
    {
        visible_ = visible.ToList();
        SyncItems();
    }

    public void ApplyState(LogicBlockStateDto? state)
    {
        StateKey = state?.State ?? LogicBlockStates.Unknown;
        StateLabel = StateKey switch
        {
            LogicBlockStates.Ready => "READY",
            LogicBlockStates.Blocked => "BLOCKED",
            LogicBlockStates.Disabled => "DISABLED",
            _ => "NO DATA"
        };

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int notMet = members_.Count(row =>
            string.Equals(row.StateKey, LogicConditionStates.False, StringComparison.OrdinalIgnoreCase));
        string text = members_.Count + (members_.Count == 1 ? " element" : " elements");
        Summary = notMet > 0 ? text + " · " + notMet + " not met" : text;
    }

    private void SyncItems()
    {
        if (!expanded_)
        {
            if (Count > 0)
            {
                Clear();
            }

            return;
        }

        if (Count == visible_.Count)
        {
            bool same = true;
            for (int i = 0; i < visible_.Count; i++)
            {
                if (!ReferenceEquals(this[i], visible_[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        Clear();
        foreach (LogicElementRowViewModel row in visible_)
        {
            Add(row);
        }
    }

    private static string KindOf(LogicBlockDto block) => block.Kind switch
    {
        LogicBlockKinds.Sequence => "Sequence",
        LogicBlockKinds.Permissive => "Permissive",
        _ => "Interlock"
    };
}

/// <summary>
/// Every element of every block in one list, grouped by block. It is fed either the live
/// snapshot or the persisted last-known one, so the same rows read on the floor with the link
/// down. Rows are stable across state pushes; only a changed definitions set rebuilds them.
/// </summary>
public sealed partial class ElementsViewModel : ObservableObject
{
    private const string AllKindsKey = "";

    private readonly Dictionary<Guid, LogicBlockDto> blocks_ = new();
    private readonly Dictionary<Guid, bool> collapsed_ = new();
    private readonly List<ElementBlockGroupViewModel> orderedGroups_ = new();

    private IReadOnlyList<LogicBlockDto>? definitions_;
    private IReadOnlyList<LogicAreaTagsDto> areaTags_ = Array.Empty<LogicAreaTagsDto>();

    public ElementsViewModel()
    {
        KindFilters.Add(new LogicFilterChipViewModel(AllKindsKey, "All") { IsSelected = true });
        KindFilters.Add(new LogicFilterChipViewModel(LogicBlockKinds.Interlock, "Interlocks"));
        KindFilters.Add(new LogicFilterChipViewModel(LogicBlockKinds.Permissive, "Permissives"));
        KindFilters.Add(new LogicFilterChipViewModel(LogicBlockKinds.Sequence, "Sequences"));
    }

    /// <summary>The groups the CollectionView binds (only those with a visible row while filtering).</summary>
    public ObservableCollection<ElementBlockGroupViewModel> VisibleGroups { get; } = new();

    /// <summary>The single-choice kind filter row: All / Interlocks / Permissives / Sequences.</summary>
    public ObservableCollection<LogicFilterChipViewModel> KindFilters { get; } = new();

    /// <summary>Every row, in block order, before search and kind filtering.</summary>
    public IReadOnlyList<LogicElementRowViewModel> AllRows =>
        orderedGroups_.SelectMany(group => group.Members).ToList();

    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>"live · synced 12:31:05" or "last known · 2026-11-02 01:55:22".</summary>
    [ObservableProperty] private string _sourceText = string.Empty;

    /// <summary>True while the rows are the persisted last-known snapshot, not live data.</summary>
    [ObservableProperty] private bool _showingLastKnown;

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>"12 of 42 elements" while a search or kind filter is on, else empty.</summary>
    [ObservableProperty] private string _filterText = string.Empty;

    [ObservableProperty] private bool _hasFilter;

    [ObservableProperty] private string _emptyMessage = "No elements.";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Selects one kind chip (single choice; "All" clears the kind filter).</summary>
    public void SelectKindFilter(LogicFilterChipViewModel chip)
    {
        foreach (LogicFilterChipViewModel candidate in KindFilters)
        {
            candidate.IsSelected = ReferenceEquals(candidate, chip);
        }

        ApplyFilter();
    }

    /// <summary>
    /// Points the list at a definitions set. The rows are rebuilt only when the set actually
    /// changed; a state push leaves the bound rows (and the operator's scroll position) alone.
    /// </summary>
    public void ApplyDefinitions(
        IReadOnlyList<LogicBlockDto> blocks,
        IReadOnlyList<LogicAreaTagsDto>? areaTags,
        MultiBridgeTagCache? tags,
        string bridgeKey)
    {
        areaTags_ = areaTags ?? Array.Empty<LogicAreaTagsDto>();

        if (ReferenceEquals(definitions_, blocks))
        {
            return;
        }

        definitions_ = blocks;
        foreach (ElementBlockGroupViewModel group in orderedGroups_)
        {
            collapsed_[group.BlockId] = !group.IsExpanded;
        }

        blocks_.Clear();
        orderedGroups_.Clear();

        foreach (LogicBlockDto block in blocks
            .OrderBy(block => block.Order)
            .ThenBy(block => block.Name, StringComparer.OrdinalIgnoreCase))
        {
            blocks_[block.Id] = block;
            ElementBlockGroupViewModel group = new(block)
            {
                IsExpanded = !collapsed_.TryGetValue(block.Id, out bool collapsed) || !collapsed
            };
            LogicAreaTagsDto? area = AreaTagsFor(block);
            List<LogicElementRowViewModel> members = LogicElementRows.Build(block, area, tags, bridgeKey);
            group.SetMembers(members);
            orderedGroups_.Add(group);
        }

        ApplyFilter(forceVisible: true);
        Summary = BuildSummary();
    }

    /// <summary>
    /// Pushes one evaluated snapshot into the rows. <paramref name="lastKnown"/> selects the
    /// persisted data path and marks every row so the screen never implies it is live.
    /// </summary>
    public void ApplyState(
        LogicStateSnapshot? state,
        MultiBridgeTagCache? tags,
        string bridgeKey,
        bool lastKnown,
        DateTime? syncedUtc)
    {
        ShowingLastKnown = lastKnown;
        SourceText = lastKnown
            ? "last known" + (syncedUtc is { } known
                ? " · " + known.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : string.Empty)
            : "live" + (syncedUtc is { } synced
                ? " · synced " + synced.ToLocalTime().ToString("HH:mm:ss")
                : string.Empty);

        Dictionary<Guid, LogicBlockStateDto> blockStates = new();
        foreach (LogicBlockStateDto block in state?.Blocks ?? Enumerable.Empty<LogicBlockStateDto>())
        {
            blockStates[block.Id] = block;
        }

        foreach (ElementBlockGroupViewModel group in orderedGroups_)
        {
            blockStates.TryGetValue(group.BlockId, out LogicBlockStateDto? blockState);

            Dictionary<Guid, LogicElementStateDto> elementStates = new();
            foreach (LogicElementStateDto element in blockState?.Elements ?? Enumerable.Empty<LogicElementStateDto>())
            {
                elementStates[element.Id] = element;
            }

            foreach (LogicElementRowViewModel row in group.Members)
            {
                elementStates.TryGetValue(row.Id, out LogicElementStateDto? elementState);
                row.ApplyState(elementState);

                MultiBridgeTagEntry? entry = null;
                bool hasEntry = row.TagReference is { IsBound: true } reference &&
                    tags is not null &&
                    tags.TryGet(
                        TagBindingKey.Create(bridgeKey, reference.SourceId, reference.ItemId),
                        out entry);

                // Contacts read the cache; non-contacts ignore it. A cleared entry clears the
                // row, while a legacy last-known snapshot without tag entries keeps the value
                // text its evaluation carried.
                if (hasEntry || elementState is null)
                {
                    row.ApplyTag(entry);
                }

                row.IsLastKnown = lastKnown;
                row.SetLastRead(entry?.TimestampUtc ?? elementState?.TimestampUtc);
            }

            group.ApplyState(blockState);
        }

        ApplyFilter(forceVisible: false);
    }

    public void Clear()
    {
        definitions_ = null;
        blocks_.Clear();
        orderedGroups_.Clear();
        VisibleGroups.Clear();
        Summary = string.Empty;
        SourceText = string.Empty;
        ShowingLastKnown = false;
        FilterText = string.Empty;
        HasFilter = false;
        EmptyMessage = "No elements.";
    }

    private LogicAreaTagsDto? AreaTagsFor(LogicBlockDto block) =>
        LogicAreaTags.Find(areaTags_, LogicAreaTags.AreaNameFor(block));

    private void ApplyFilter() => ApplyFilter(forceVisible: false);

    private void ApplyFilter(bool forceVisible)
    {
        string[] terms = SearchText.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string kind = KindFilters.FirstOrDefault(chip => chip.IsSelected)?.Key ?? AllKindsKey;

        List<ElementBlockGroupViewModel> visible = new();
        int visibleRows = 0;
        foreach (ElementBlockGroupViewModel group in orderedGroups_)
        {
            LogicBlockDto block = blocks_[group.BlockId];
            bool kindMatches = kind.Length == 0 ||
                string.Equals(block.Kind, kind, StringComparison.OrdinalIgnoreCase);
            List<LogicElementRowViewModel> members = kindMatches
                ? group.Members.Where(row => Matches(row, terms)).ToList()
                : new List<LogicElementRowViewModel>();
            if (members.Count == 0)
            {
                group.SetVisible(Array.Empty<LogicElementRowViewModel>());
                continue;
            }

            group.SetVisible(members);
            visible.Add(group);
            visibleRows += members.Count;
        }

        SyncVisibleGroups(visible, forceVisible);
        int totalRows = orderedGroups_.Sum(group => group.Members.Count);
        HasFilter = terms.Length > 0 || kind.Length > 0;
        FilterText = HasFilter ? $"{visibleRows} of {totalRows} elements" : string.Empty;
        EmptyMessage = totalRows == 0
            ? "No elements on this bridge yet — author logic blocks in the OpcBridge HMI."
            : "No elements match the search or filter.";
    }

    private static bool Matches(LogicElementRowViewModel row, IReadOnlyList<string> terms)
    {
        foreach (string term in terms)
        {
            if (!row.Text.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !row.KindLabel.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !row.TagLabel.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !row.StepLabel.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !row.BlockName.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void SyncVisibleGroups(
        IReadOnlyList<ElementBlockGroupViewModel> visible,
        bool forceVisible)
    {
        if (!forceVisible && VisibleGroups.Count == visible.Count)
        {
            bool same = true;
            for (int i = 0; i < visible.Count; i++)
            {
                if (!ReferenceEquals(VisibleGroups[i], visible[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        VisibleGroups.Clear();
        foreach (ElementBlockGroupViewModel group in visible)
        {
            VisibleGroups.Add(group);
        }
    }

    private string BuildSummary()
    {
        int elements = orderedGroups_.Sum(group => group.Members.Count);
        return orderedGroups_.Count + (orderedGroups_.Count == 1 ? " block" : " blocks")
            + " · " + elements + (elements == 1 ? " element" : " elements");
    }
}
