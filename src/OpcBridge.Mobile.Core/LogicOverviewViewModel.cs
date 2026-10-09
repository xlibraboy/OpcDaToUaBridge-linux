using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One block on the overview screen: name, kind, the big state word and why it is blocked.
/// The view colours by <see cref="StateKey"/> and still shows the word, so the state reads
/// without colour too.
/// </summary>
public sealed partial class LogicBlockCardViewModel : ObservableObject
{
    private readonly LogicBlockDto block_;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _stateLabel = "no data";
    [ObservableProperty] private string _stateKey = "unknown";
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private string _progressText = string.Empty;

    public LogicBlockCardViewModel(LogicBlockDto block)
    {
        block_ = block;
        ApplyDefinition(block);
    }

    public Guid Id => block_.Id;

    /// <summary>The interlock group the block was authored under ("" = ungrouped).</summary>
    public string Group { get; private set; } = string.Empty;

    /// <summary>The heading this block sits under ("Ungrouped" when it carries no group).</summary>
    public string GroupLabel => Group.Length == 0 ? "Ungrouped" : Group;

    public string KindLabel => block_.Kind switch
    {
        LogicBlockKinds.Sequence => "Sequence",
        LogicBlockKinds.Permissive => "Permissive",
        _ => "Interlock"
    };

    public bool Enabled => block_.Enabled;

    /// <summary>The block's tags (authored in the dashboard), shown as chips on the card.</summary>
    public ObservableCollection<string> Tags { get; } = new();

    public void ApplyDefinition(LogicBlockDto block)
    {
        Name = block.Name;
        Description = block.Description;
        Group = (block.Group ?? string.Empty).Trim();
        SyncTags(block.Tags);
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(GroupLabel));
    }

    public void ApplyState(LogicBlockStateDto? state)
    {
        if (state is null)
        {
            StateLabel = "no data";
            StateKey = LogicBlockStates.Unknown;
            Reason = "reading…";
            ProgressText = string.Empty;
            return;
        }

        StateKey = state.State;
        StateLabel = state.State switch
        {
            LogicBlockStates.Ready => "READY",
            LogicBlockStates.Blocked => "BLOCKED",
            LogicBlockStates.Disabled => "DISABLED",
            _ => "NO DATA"
        };
        Reason = string.IsNullOrWhiteSpace(state.Reason)
            ? (state.State == LogicBlockStates.Ready ? "all conditions met" : null)
            : state.Reason;

        ProgressText = block_.Kind == LogicBlockKinds.Sequence && block_.Steps.Count > 0
            ? BuildProgress(state)
            : string.Empty;
    }

    private string BuildProgress(LogicBlockStateDto state)
    {
        int total = block_.Steps.Count;
        int done = state.Steps.Count(step => string.Equals(step.State, LogicStepStates.Done, StringComparison.OrdinalIgnoreCase));
        LogicStepStateDto? current = state.Steps.FirstOrDefault(step =>
            string.Equals(step.State, LogicStepStates.Current, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step.State, LogicStepStates.Unknown, StringComparison.OrdinalIgnoreCase));
        string currentName = current is null
            ? (done == total ? "complete" : string.Empty)
            : block_.Steps.FirstOrDefault(s => s.Id == current.Id)?.Name ?? string.Empty;
        string progress = $"{done}/{total} steps";
        return currentName.Length > 0 ? progress + " · " + currentName : progress;
    }

    /// <summary>
    /// Keeps the chip list in step with the definition; an unchanged list is left alone so a
    /// periodic definitions refresh cannot churn the chips under the operator's finger.
    /// </summary>
    private void SyncTags(IReadOnlyList<string>? tags)
    {
        IReadOnlyList<string> wanted = tags ?? Array.Empty<string>();
        if (Tags.Count == wanted.Count && Tags.SequenceEqual(wanted, StringComparer.Ordinal))
        {
            return;
        }

        Tags.Clear();
        foreach (string tag in wanted)
        {
            Tags.Add(tag);
        }
    }
}

/// <summary>
/// One search/filter chip on the overview: the state row's single choice ("All", "Ready", …)
/// or one tag the operator can switch on and off (several tags mean "any of these").
/// </summary>
public sealed partial class LogicFilterChipViewModel : ObservableObject
{
    public LogicFilterChipViewModel(string key, string label)
    {
        Key = key;
        Label = label;
    }

    /// <summary>"" for the state row's "All"; otherwise a block state key or the tag text.</summary>
    public string Key { get; }

    public string Label { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// One heading on the overview: the blocks that share an interlock group (or the ungrouped
/// ones), collapsible so a machine's up/down interlocks stay compact. Members are re-parented,
/// never recreated, and collapsing only empties the collection the list binds.
/// </summary>
public sealed class LogicBlockGroupViewModel : ObservableCollection<LogicBlockCardViewModel>
{
    private readonly List<LogicBlockCardViewModel> members_ = new();
    private RelayCommand? toggle_command_;
    private bool expanded_ = true;
    private string summary_ = string.Empty;
    private bool show_header_ = true;

    public LogicBlockGroupViewModel(string group, string name)
    {
        Group = group;
        Name = name;
    }

    /// <summary>The authored group label ("" for the ungrouped blocks).</summary>
    public string Group { get; }

    /// <summary>The heading the list shows ("Ungrouped" for blocks authored without a group).</summary>
    public string Name { get; }

    /// <summary>"▾" / "▸" — the heading reports its own state without colour.</summary>
    public string Chevron => expanded_ ? "▾" : "▸";

    /// <summary>Collapses or expands the group; the heading keeps its summary either way.</summary>
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

    /// <summary>
    /// True while the heading is worth a row: a named group always, the catch-all only once
    /// some other block carries a group.
    /// </summary>
    public bool ShowHeader
    {
        get => show_header_;
        internal set
        {
            if (show_header_ != value)
            {
                show_header_ = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowHeader)));
            }
        }
    }

    /// <summary>"3 blocks · 1 blocked" — what the heading keeps visible while collapsed.</summary>
    public string Summary
    {
        get => summary_;
        internal set
        {
            if (!string.Equals(summary_, value, StringComparison.Ordinal))
            {
                summary_ = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Summary)));
            }
        }
    }

    /// <summary>The group's blocks in list order; the bound items are the visible subset.</summary>
    internal IReadOnlyList<LogicBlockCardViewModel> Members => members_;

    /// <summary>
    /// Points the group at its members. An unchanged membership leaves the bound items alone,
    /// so a state push never rebuilds the rows the operator is looking at.
    /// </summary>
    internal void SetMembers(IReadOnlyList<LogicBlockCardViewModel> members)
    {
        bool same = members_.Count == members.Count;
        if (same)
        {
            for (int i = 0; i < members.Count; i++)
            {
                if (!ReferenceEquals(members_[i], members[i]))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same)
        {
            return;
        }

        members_.Clear();
        members_.AddRange(members);
        SyncItems();
    }

    private void SyncItems()
    {
        if (expanded_)
        {
            if (Count == members_.Count)
            {
                bool same = true;
                for (int i = 0; i < members_.Count; i++)
                {
                    if (!ReferenceEquals(this[i], members_[i]))
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
            foreach (LogicBlockCardViewModel member in members_)
            {
                Add(member);
            }
        }
        else if (Count > 0)
        {
            Clear();
        }
    }
}

/// <summary>
/// The overview: every block as a card, updated in place from pushed snapshots so the list
/// never rebuilds under the operator's finger. A search box, a tag chip row and a single-choice
/// state row narrow <see cref="Visible"/>; filtering never replaces the cards themselves, and a
/// state push only re-syncs the visible set when it actually changed. The visible blocks are
/// shown under their interlock group's collapsible heading.
/// </summary>
public sealed partial class LogicOverviewViewModel : ObservableObject
{
    private const string AllStatesKey = "";

    private static readonly string NoBlocksText =
        "No logic blocks here. Add a bridge under Settings ▸ Bridges, or author blocks in the dashboard under Tags ▸ Logic.";

    private const string NoMatchText = "No blocks match the search or filter.";

    /// <summary>Heading for the blocks authored without a group.</summary>
    private const string UngroupedName = "Ungrouped";

    private readonly Dictionary<Guid, LogicBlockCardViewModel> cards_ = new();

    /// <summary>Which group headings the operator collapsed, by group label.</summary>
    private readonly Dictionary<string, bool> collapsed_ = new(StringComparer.OrdinalIgnoreCase);

    private bool anyGrouped_;

    public LogicOverviewViewModel()
    {
        StateFilters.Add(new LogicFilterChipViewModel(AllStatesKey, "All") { IsSelected = true });
        StateFilters.Add(new LogicFilterChipViewModel(LogicBlockStates.Ready, "Ready"));
        StateFilters.Add(new LogicFilterChipViewModel(LogicBlockStates.Blocked, "Blocked"));
        StateFilters.Add(new LogicFilterChipViewModel(LogicBlockStates.Unknown, "No data"));
        StateFilters.Add(new LogicFilterChipViewModel(LogicBlockStates.Disabled, "Disabled"));
    }

    /// <summary>Every block, in authored order — the state updates run over this list.</summary>
    public ObservableCollection<LogicBlockCardViewModel> Blocks { get; } = new();

    /// <summary>The blocks the search and filters leave visible (all of them when none are set).</summary>
    public ObservableCollection<LogicBlockCardViewModel> Visible { get; } = new();

    /// <summary>
    /// The visible blocks under their collapsible group headings — the grouped view the list
    /// binds. Group instances survive state pushes; only membership changes re-parent cards.
    /// </summary>
    public ObservableCollection<LogicBlockGroupViewModel> VisibleGroups { get; } = new();

    /// <summary>One chip per tag on any block; several can be on at once (any-of match).</summary>
    public ObservableCollection<LogicFilterChipViewModel> TagFilters { get; } = new();

    /// <summary>The single-choice state row: All / Ready / Blocked / No data / Disabled.</summary>
    public ObservableCollection<LogicFilterChipViewModel> StateFilters { get; } = new();

    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>Free text; every whitespace-separated term must match the name, description or a tag.</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>"3 of 12 blocks" while a search or a filter is on, else empty.</summary>
    [ObservableProperty] private string _filterText = string.Empty;

    /// <summary>True while a search or a filter narrows the list.</summary>
    [ObservableProperty] private bool _hasFilter;

    /// <summary>What the empty list means: nothing authored, or a filter that matched no block.</summary>
    [ObservableProperty] private string _emptyMessage = NoBlocksText;

    /// <summary>The definitions the detail screen is built from (latest applied).</summary>
    public IReadOnlyList<LogicBlockDto> Definitions { get; private set; } = Array.Empty<LogicBlockDto>();

    /// <summary>The latest evaluated state (latest applied).</summary>
    public LogicStateSnapshot? State { get; private set; }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Switches one tag chip on or off; several tags mean "any of these".</summary>
    public void ToggleTagFilter(LogicFilterChipViewModel chip)
    {
        chip.IsSelected = !chip.IsSelected;
        ApplyFilter();
    }

    /// <summary>Selects one state chip (single choice; "All" clears the state filter).</summary>
    public void SelectStateFilter(LogicFilterChipViewModel chip)
    {
        foreach (LogicFilterChipViewModel candidate in StateFilters)
        {
            candidate.IsSelected = ReferenceEquals(candidate, chip);
        }

        ApplyFilter();
    }

    public void ApplyDefinitions(IEnumerable<LogicBlockDto> blocks)
    {
        List<LogicBlockDto> sorted = blocks
            .OrderBy(block => block.Order)
            .ThenBy(block => block.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Definitions = sorted;

        bool sameOrder = sorted.Count == Blocks.Count;
        if (sameOrder)
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Id != Blocks[i].Id)
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        if (!sameOrder)
        {
            cards_.Clear();
            Blocks.Clear();
            foreach (LogicBlockDto block in sorted)
            {
                LogicBlockCardViewModel card = new(block);
                cards_[block.Id] = card;
                Blocks.Add(card);
            }
        }
        else
        {
            foreach (LogicBlockDto block in sorted)
            {
                cards_[block.Id].ApplyDefinition(block);
            }
        }

        RebuildTagFilters(sorted);
        anyGrouped_ = sorted.Any(block => !string.IsNullOrWhiteSpace(block.Group));
        ApplyState(State);
    }

    public void ApplyState(LogicStateSnapshot? snapshot)
    {
        State = snapshot;
        Dictionary<Guid, LogicBlockStateDto> byId = new();
        foreach (LogicBlockStateDto block in snapshot?.Blocks ?? Enumerable.Empty<LogicBlockStateDto>())
        {
            byId[block.Id] = block;
        }

        foreach (LogicBlockCardViewModel card in Blocks)
        {
            card.ApplyState(byId.TryGetValue(card.Id, out LogicBlockStateDto? state) ? state : null);
        }

        ApplyFilter();
        ApplySummary();
    }

    private void ApplySummary()
    {
        if (Blocks.Count == 0)
        {
            Summary = "no logic blocks configured";
            return;
        }

        int ready = Blocks.Count(card => string.Equals(card.StateKey, LogicBlockStates.Ready, StringComparison.OrdinalIgnoreCase));
        int blocked = Blocks.Count(card => string.Equals(card.StateKey, LogicBlockStates.Blocked, StringComparison.OrdinalIgnoreCase));
        int unknown = Blocks.Count(card => string.Equals(card.StateKey, LogicBlockStates.Unknown, StringComparison.OrdinalIgnoreCase));
        Summary = $"{ready} ready · {blocked} blocked" + (unknown > 0 ? $" · {unknown} no data" : string.Empty);
    }

    /// <summary>
    /// Rebuilds <see cref="Visible"/> from the search, the ticked tags (any-of) and the state
    /// chip. The collection instance is never replaced and an unchanged set is left alone, so
    /// pushing state does not reset the list while the operator is looking at it.
    /// </summary>
    private void ApplyFilter()
    {
        string[] terms = SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> tags = TagFilters.Where(chip => chip.IsSelected).Select(chip => chip.Key).ToList();
        string stateKey = StateFilters.FirstOrDefault(chip => chip.IsSelected)?.Key ?? AllStatesKey;

        List<LogicBlockCardViewModel> visible = new();
        foreach (LogicBlockCardViewModel card in Blocks)
        {
            if (Matches(card, terms, tags, stateKey))
            {
                visible.Add(card);
            }
        }

        SyncVisible(visible);
        SyncGroups();

        HasFilter = terms.Length > 0 || tags.Count > 0 || stateKey.Length > 0;
        FilterText = HasFilter ? $"{Visible.Count} of {Blocks.Count} blocks" : string.Empty;
        EmptyMessage = Blocks.Count == 0 ? NoBlocksText : NoMatchText;
    }

    /// <summary>
    /// Rebuilds the group headings from <see cref="Visible"/>: named groups alphabetically, the
    /// ungrouped catch-all last. Headings match case-insensitively and take the spelling of their
    /// first block in list order. Existing headings keep their instance, their collapse state and
    /// their cards; only a changed membership re-parents anything.
    /// </summary>
    private void SyncGroups()
    {
        Dictionary<string, List<LogicBlockCardViewModel>> byGroup = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = new();
        foreach (LogicBlockCardViewModel card in Visible)
        {
            if (!byGroup.TryGetValue(card.Group, out List<LogicBlockCardViewModel>? members))
            {
                members = new List<LogicBlockCardViewModel>();
                byGroup[card.Group] = members;
                order.Add(card.Group);
            }

            members.Add(card);
        }

        // Named groups alphabetically, the catch-all last. A stable sort keeps the spelling the
        // first block used when labels differ only by case.
        order = order
            .OrderBy(label => label.Length == 0 ? 1 : 0)
            .ThenBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool sameNames = VisibleGroups.Count == order.Count;
        if (sameNames)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (!string.Equals(VisibleGroups[i].Group, order[i], StringComparison.OrdinalIgnoreCase))
                {
                    sameNames = false;
                    break;
                }
            }
        }

        if (!sameNames)
        {
            Dictionary<string, LogicBlockGroupViewModel> existing = new(StringComparer.OrdinalIgnoreCase);
            foreach (LogicBlockGroupViewModel group in VisibleGroups)
            {
                collapsed_[group.Group] = !group.IsExpanded;
                existing[group.Group] = group;
            }

            List<LogicBlockGroupViewModel> rebuilt = new();
            foreach (string label in order)
            {
                if (!existing.TryGetValue(label, out LogicBlockGroupViewModel? group))
                {
                    group = new LogicBlockGroupViewModel(label, label.Length == 0 ? UngroupedName : label)
                    {
                        // A heading the operator collapsed stays collapsed across pushes and edits.
                        IsExpanded = !collapsed_.TryGetValue(label, out bool collapsed) || !collapsed
                    };
                }

                rebuilt.Add(group);
            }

            VisibleGroups.Clear();
            foreach (LogicBlockGroupViewModel group in rebuilt)
            {
                VisibleGroups.Add(group);
            }
        }

        foreach (string label in order)
        {
            LogicBlockGroupViewModel? group = VisibleGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Group, label, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                continue;
            }

            group.ShowHeader = label.Length > 0 || anyGrouped_;
            group.SetMembers(byGroup[label]);
            group.Summary = BuildGroupSummary(byGroup[label]);
        }
    }

    /// <summary>"3 blocks · 1 blocked · 1 no data" — the heading's own state while collapsed.</summary>
    private static string BuildGroupSummary(IReadOnlyList<LogicBlockCardViewModel> members)
    {
        string text = members.Count + (members.Count == 1 ? " block" : " blocks");
        int blocked = members.Count(card => string.Equals(card.StateKey, LogicBlockStates.Blocked, StringComparison.OrdinalIgnoreCase));
        int unknown = members.Count(card => string.Equals(card.StateKey, LogicBlockStates.Unknown, StringComparison.OrdinalIgnoreCase));
        if (blocked > 0)
        {
            text += " · " + blocked + " blocked";
        }

        if (unknown > 0)
        {
            text += " · " + unknown + " no data";
        }

        return text;
    }

    private static bool Matches(LogicBlockCardViewModel card, IReadOnlyList<string> terms, IReadOnlyList<string> tags, string stateKey)
    {
        if (stateKey.Length > 0 && !string.Equals(card.StateKey, stateKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (tags.Count > 0 && !tags.Any(tag => card.Tags.Any(cardTag => string.Equals(cardTag, tag, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        foreach (string term in terms)
        {
            if (!card.Name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !card.Description.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !card.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private void SyncVisible(IReadOnlyList<LogicBlockCardViewModel> visible)
    {
        if (Visible.Count == visible.Count)
        {
            bool same = true;
            for (int i = 0; i < visible.Count; i++)
            {
                if (!ReferenceEquals(Visible[i], visible[i]))
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

        Visible.Clear();
        foreach (LogicBlockCardViewModel card in visible)
        {
            Visible.Add(card);
        }
    }

    /// <summary>
    /// One chip per distinct tag (case-insensitive), sorted; chips already ticked stay ticked
    /// and an unchanged tag set is left alone.
    /// </summary>
    private void RebuildTagFilters(IReadOnlyList<LogicBlockDto> blocks)
    {
        List<string> tags = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicBlockDto block in blocks)
        {
            foreach (string tag in block.Tags ?? Enumerable.Empty<string>())
            {
                string trimmed = tag?.Trim() ?? string.Empty;
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    tags.Add(trimmed);
                }
            }
        }

        tags.Sort(StringComparer.OrdinalIgnoreCase);

        bool same = tags.Count == TagFilters.Count;
        if (same)
        {
            for (int i = 0; i < tags.Count; i++)
            {
                if (!string.Equals(tags[i], TagFilters[i].Key, StringComparison.OrdinalIgnoreCase))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same)
        {
            return;
        }

        HashSet<string> selected = new(
            TagFilters.Where(chip => chip.IsSelected).Select(chip => chip.Key),
            StringComparer.OrdinalIgnoreCase);
        TagFilters.Clear();
        foreach (string tag in tags)
        {
            TagFilters.Add(new LogicFilterChipViewModel(tag, tag) { IsSelected = selected.Contains(tag) });
        }
    }
}
