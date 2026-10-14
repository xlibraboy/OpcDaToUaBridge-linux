using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// The Logic page: authors interlock / permissive / sequence blocks on a selected bridge and
/// shows the bridge's evaluated state — the whole plant-logic surface, natively in the runtime.
/// Definitions come from <c>GET /api/logic</c>, the evaluated state from the hub's <c>logic</c>
/// push plus a visible-page poll; every snapshot is applied to the existing rows in place so a
/// live tick never disturbs what the operator is editing.
/// </summary>
public sealed partial class LogicPanelViewModel : ObservableObject, IDisposable
{
    private readonly BridgeConnectionManager connections_;
    private readonly Func<Func<Task>, Task> post_;
    private readonly Action<string> report_;
    private readonly Dictionary<string, BridgeCache> caches_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer pollTimer_;
    private readonly DispatcherTimer freshnessTimer_;
    private readonly SemaphoreSlim loadGate_ = new(1, 1);
    private int polling_;
    private bool pageVisible_;
    private bool disposed_;
    private bool applyingState_;
    private bool buildingRows_;
    private bool editingNewBlock_;
    private bool reloadingDefinitions_;
    private bool suppressBridgeLoad_;
    private bool forceReloadDraft_;
    private bool selectFirstAfterReload_;
    private LogicTagCatalog catalog_ = LogicTagCatalog.Empty;
    private LogicBlockDto? storedBlock_;
    private Guid? pendingSelectId_;

    public LogicPanelViewModel(
        BridgeConnectionManager connections,
        Func<Func<Task>, Task> post,
        Action<string> report)
    {
        connections_ = connections;
        post_ = post;
        report_ = report;
        KindOptions = LogicOptions.BlockKinds;
        AreaTags.PropertyChanged += OnAreaTagsChanged;
        AreaTags.DeleteSourceRequested = DeleteAreaTagsAsync;
        AreaTags.ConfirmRemoveEntry = async name =>
            ConfirmAsync is null
            || await ConfirmAsync(
                "Remove tag",
                $"Remove \"{name}\"? This block reads it — its rows need another tag (or a direct one) before they can be saved.",
                "Remove").ConfigureAwait(true);
        pollTimer_ = new System.Threading.Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
        freshnessTimer_ = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        freshnessTimer_.Tick += (_, _) => RefreshFreshness();
    }

    /// <summary>Set by the page: asks the operator to confirm a destructive step. Null = proceed.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Set by the page: asks for a new block's name, interlock group and AREA — the area first,
    /// because it is the scope every tag picked afterwards is shared by. Null (no UI attached)
    /// starts an empty draft instead.
    /// </summary>
    public Func<Window?, LogicNewBlockRequest, Task<LogicNewBlockDraft?>>? NewBlockPrompt { get; set; }

    // ---- Bridge selector ----

    public ObservableCollection<LogicBridgeOption> Bridges { get; } = new();

    [ObservableProperty]
    private LogicBridgeOption? _selectedBridge;

    public bool IsEditingEnabled => SelectedBridge is { IsConnected: true };

    public string BridgeStatusText => SelectedBridge is null
        ? "No bridge"
        : $"{SelectedBridge.Label} · {SelectedBridge.StatusText}";

    public string EditingHint => IsEditingEnabled
        ? string.Empty
        : "Editing is disabled while the bridge is unreachable — showing the last known blocks.";

    public bool IsBridgeOk => SelectedBridge is { IsConnected: true };

    public bool IsBridgeWarn => SelectedBridge?.State == BridgeLinkState.Reconnecting;

    public bool IsBridgeBad => SelectedBridge is null || SelectedBridge.State == BridgeLinkState.Failed;

    partial void OnSelectedBridgeChanged(LogicBridgeOption? value)
    {
        // The read state and the freshness stamp belong to the bridge being shown, never the last one.
        stateUpdatedAt_ = null;
        IsBlocksLoading = false;
        BlocksError = string.Empty;
        NotifyBridgeDerived();
        if (!suppressBridgeLoad_)
        {
            _ = LoadSelectedBridgeAsync();
        }
    }

    // ---- Editor / Tags / Result tabs ----

    [ObservableProperty]
    private bool _isEditorTab = true;

    [ObservableProperty]
    private bool _isTagsTab;

    [ObservableProperty]
    private bool _isResultTab;

    /// <summary>
    /// The selected block's area's named tags: one list shared by every block of the area, so a
    /// tag is assigned once here instead of in each block's rows.
    /// </summary>
    public LogicAreaTagsViewModel AreaTags { get; } = new();

    partial void OnIsEditorTabChanged(bool value)
    {
        if (value && IsResultTab)
        {
            IsResultTab = false;
        }

        if (value && IsTagsTab)
        {
            IsTagsTab = false;
        }
    }

    partial void OnIsTagsTabChanged(bool value)
    {
        if (value && IsEditorTab)
        {
            IsEditorTab = false;
        }

        if (value && IsResultTab)
        {
            IsResultTab = false;
        }

        if (value)
        {
            RefreshAreaTags();
        }
    }

    partial void OnIsResultTabChanged(bool value)
    {
        if (value && IsEditorTab)
        {
            IsEditorTab = false;
        }

        if (value && IsTagsTab)
        {
            IsTagsTab = false;
        }
    }

    [ObservableProperty]
    private string _editorMessage = string.Empty;

    // ---- Block list ----

    public ObservableCollection<LogicAreaViewModel> BlockAreas { get; } = new();

    [ObservableProperty]
    private bool _hasBlocks;

    /// <summary>True while the selected bridge's definitions are being read.</summary>
    [ObservableProperty]
    private bool _isBlocksLoading;

    /// <summary>The last definitions read's error, empty when the last read succeeded.</summary>
    [ObservableProperty]
    private string _blocksError = string.Empty;

    [ObservableProperty]
    private string _summaryText = "no logic blocks configured";

    [ObservableProperty]
    private LogicBlockRowViewModel? _selectedBlock;

    [ObservableProperty]
    private bool _hasSelectedBlock;

    public string EmptyBlocksMessage => LogicText.BlockListEmptyMessage(
        hasBridge: SelectedBridge is not null,
        connected: IsEditingEnabled,
        loading: IsBlocksLoading,
        error: BlocksError);

    partial void OnSelectedBlockChanged(LogicBlockRowViewModel? value)
    {
        HasSelectedBlock = value is not null;
        foreach (LogicBlockRowViewModel row in AllBlockRows())
        {
            row.IsSelected = ReferenceEquals(row, value);
        }

        if (value is null)
        {
            return;
        }

        bool force = forceReloadDraft_;
        forceReloadDraft_ = false;
        if (!force && storedBlock_ is { } stored && stored.Id == value.BlockId)
        {
            // The same block, a fresh row instance: keep whatever the operator is editing.
            storedBlock_ = value.Block;
            DirtyChanged();
            UpdateResultDetail();
            return;
        }

        LoadDraft(value.Block, value.Block);
    }

    // ---- Editor fields ----

    public IReadOnlyList<LogicSelectOption> KindOptions { get; }

    [ObservableProperty]
    private string _blockName = string.Empty;

    [ObservableProperty]
    private LogicSelectOption? _selectedKind;

    [ObservableProperty]
    private bool _blockEnabled = true;

    [ObservableProperty]
    private string _blockGroup = string.Empty;

    [ObservableProperty]
    private string _blockArea = string.Empty;

    [ObservableProperty]
    private string _blockDescription = string.Empty;

    [ObservableProperty]
    private string _blockTags = string.Empty;

    public ObservableCollection<LogicConditionRowViewModel> Conditions { get; } = new();

    public ObservableCollection<LogicElementRowViewModel> Elements { get; } = new();

    /// <summary>
    /// The internal relays a coil already drives in any loaded block, offered by every row's
    /// relay field while one is typed — the whole point is wiring relays across blocks, so a
    /// relay authored in one block must be nameable in the next.
    /// </summary>
    public ObservableCollection<string> RelayNames { get; } = new();

    public ObservableCollection<LogicStepRowViewModel> Steps { get; } = new();

    public ObservableCollection<LogicActionRowViewModel> Actions { get; } = new();

    public bool IsSequence =>
        string.Equals(SelectedKind?.Value, LogicBlockKinds.Sequence, StringComparison.OrdinalIgnoreCase);

    public bool ShowsNetwork => !IsSequence && Elements.Count > 0;

    public bool ShowsConditions => !IsSequence && Elements.Count == 0;

    public bool ShowsSteps => IsSequence;

    /// <summary>Row-count flags for the editor's empty-state hints.</summary>
    public bool HasConditions => Conditions.Count > 0;

    public bool HasElements => Elements.Count > 0;

    public bool HasSteps => Steps.Count > 0;

    public bool HasActions => Actions.Count > 0;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>True while the editor holds changes the bridge has not saved — the tab strip marks it.</summary>
    public bool HasUnsavedChanges => IsDirty;

    partial void OnIsDirtyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnSelectedKindChanged(LogicSelectOption? value)
    {
        RefreshEditorMode();
        DirtyChanged();
    }

    partial void OnBlockNameChanged(string value) => DirtyChanged();

    partial void OnBlockEnabledChanged(bool value) => DirtyChanged();

    partial void OnBlockGroupChanged(string value)
    {
        // The group is a display heading only — a block's tags follow its AREA, never its group,
        // so filing a block under "Primary Arm" does not name a tags scope anymore.
        DirtyChanged();
    }

    partial void OnBlockAreaChanged(string value)
    {
        if (!buildingRows_)
        {
            // The area is the tag-sharing key: typing one re-scopes the Tags tab to that area's
            // tags (clearing it empties the scope). A draft load sets both without the sync.
            AreaTags.Name = value.Trim();
            RefreshAreaFallbackHint();

            // The area is assigned first: a direct tag the block still carries joins that area's
            // tags right away, so the area's other blocks find it — nothing to move by hand.
            if (IsEditingEnabled && !AreaTags.IsNameBlank)
            {
                int shared = ConvertDirectRowsIntoArea();
                if (shared > 0)
                {
                    AreaTags.RefreshUsed(ReferencedTagNames());
                    EditorMessage = "✓ " + LogicText.AreaTagsMoved(shared, value.Trim());
                }
            }
        }

        DirtyChanged();
    }

    /// <summary>True while the Tags tab shows a legacy scope: the block has no area of its own.</summary>
    public bool HasAreaFallback =>
        BlockArea.Trim().Length == 0 && AreaTags.Name.Trim().Length > 0;

    /// <summary>Names the legacy scope a block without an area reads its tags through.</summary>
    public string AreaFallbackHint =>
        HasAreaFallback ? LogicText.AreaTagsFallbackHint(AreaTags.Name.Trim()) : string.Empty;

    private void RefreshAreaFallbackHint()
    {
        OnPropertyChanged(nameof(HasAreaFallback));
        OnPropertyChanged(nameof(AreaFallbackHint));
    }

    partial void OnBlockDescriptionChanged(string value) => DirtyChanged();

    partial void OnBlockTagsChanged(string value) => DirtyChanged();

    // ---- Result tab ----

    public ObservableCollection<LogicResultElementRowViewModel> ResultElements { get; } = new();

    public ObservableCollection<LogicResultStepRowViewModel> ResultSteps { get; } = new();

    [ObservableProperty]
    private string _resultTitle = "No block selected";

    [ObservableProperty]
    private string _resultKindLabel = string.Empty;

    [ObservableProperty]
    private bool _resultStateVisible;

    [ObservableProperty]
    private string _resultStateWord = string.Empty;

    [ObservableProperty]
    private bool _isResultOk;

    [ObservableProperty]
    private bool _isResultBad;

    [ObservableProperty]
    private bool _isResultWarn;

    [ObservableProperty]
    private bool _isResultMuted;

    [ObservableProperty]
    private string _resultDescription = string.Empty;

    [ObservableProperty]
    private string _resultReason = string.Empty;

    [ObservableProperty]
    private bool _isReasonBad;

    [ObservableProperty]
    private bool _isReasonOk;

    [ObservableProperty]
    private bool _resultShowsNetwork;

    [ObservableProperty]
    private bool _resultShowsSteps;

    [ObservableProperty]
    private bool _hasResultRows;

    /// <summary>
    /// The empty detail pane names the one thing that is true right now: pick a listed block, save
    /// the draft, connect the bridge, wait for the read, or — when the read failed — say so instead
    /// of claiming there are no blocks.
    /// </summary>
    private (string Title, string Message) ResultEmptyState => LogicText.ResultEmptyState(
        hasStoredBlock: storedBlock_ is not null,
        editingNewBlock: editingNewBlock_,
        hasBridge: SelectedBridge is not null,
        connected: IsEditingEnabled,
        loading: IsBlocksLoading,
        error: BlocksError,
        hasBlocks: HasBlocks);

    public string ResultEmptyTitle => ResultEmptyState.Title;

    public string ResultEmptyMessage => ResultEmptyState.Message;

    /// <summary>The pane names a block only when there is one — or a draft — to name.</summary>
    public bool ResultTitleVisible => HasSelectedBlock || editingNewBlock_;

    /// <summary>When the last snapshot was applied — a frozen clock is a stale plant.</summary>
    [ObservableProperty]
    private string _resultFreshness = "no state yet";

    /// <summary>True while the pane shows the last known state for a link that is down.</summary>
    public bool IsResultStale =>
        SelectedBridge?.State is BridgeLinkState.Reconnecting or BridgeLinkState.Failed;

    private DateTimeOffset? stateUpdatedAt_;

    /// <summary>
    /// Recomputes the freshness line: the link's own state wins (a dead feed never claims a recent
    /// update), and otherwise the snapshot's age counts up, so a quiet bridge reads as quiet.
    /// </summary>
    private void RefreshFreshness() =>
        ResultFreshness = LogicText.Freshness(SelectedBridge?.State, stateUpdatedAt_, DateTimeOffset.Now);

    public bool HasResultDescription => ResultDescription.Length > 0;

    public bool HasResultReason => ResultReason.Length > 0;

    partial void OnResultDescriptionChanged(string value) => OnPropertyChanged(nameof(HasResultDescription));

    partial void OnResultReasonChanged(string value) => OnPropertyChanged(nameof(HasResultReason));

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task NewBlock() => StartNewBlockAsync();

    /// <summary>
    /// Starts a new block: the dialog asks for the name, the interlock group and the AREA, so the
    /// area — the tag-sharing scope — is assigned before anything is authored. False when the
    /// operator cancelled.
    /// </summary>
    public async Task<bool> StartNewBlockAsync(Window? owner = null)
    {
        LogicNewBlockDraft? draft = null;
        if (NewBlockPrompt is { } prompt)
        {
            draft = await prompt(owner, BuildNewBlockRequest()).ConfigureAwait(true);
            if (draft is null)
            {
                return false;
            }
        }

        LogicBlockDto block = LogicEditorCodec.NewDraftBlock(CurrentBlockCount);
        if (draft is not null)
        {
            block.Name = draft.Name.Trim();
            block.Group = draft.Group.Trim();
            block.Area = draft.Area.Trim();
        }

        SelectedBlock = null;
        editingNewBlock_ = true;
        LoadDraft(block, null);
        EditorMessage = string.Empty;
        return true;
    }

    /// <summary>The dialog's suggestions (the groups and areas in use) and its pre-filled values.</summary>
    private LogicNewBlockRequest BuildNewBlockRequest()
    {
        List<string> groups = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicBlockDto block in SelectedBridge is { } bridge
            ? Cache(bridge.BridgeId).Blocks ?? new List<LogicBlockDto>()
            : new List<LogicBlockDto>())
        {
            string group = block.Group?.Trim() ?? string.Empty;
            if (group.Length > 0 && seen.Add(group))
            {
                groups.Add(group);
            }
        }

        string currentGroup = BlockGroup.Trim();
        if (currentGroup.Length > 0 && seen.Add(currentGroup))
        {
            groups.Add(currentGroup);
        }

        return new LogicNewBlockRequest(groups, AreaTags.AreaNames.ToList(), currentGroup, BlockArea.Trim());
    }

    /// <summary>
    /// Selects a listed block from the Result tab's list (and the Blocks window). Switching to a
    /// different block while the editor holds unsaved changes asks first — the draft is dropped by
    /// the load, and the app confirms this loss for "Back to conditions" and Delete.
    /// </summary>
    [RelayCommand]
    private async Task SelectBlockAsync(LogicBlockRowViewModel? row) => await TrySelectBlockAsync(row).ConfigureAwait(true);

    /// <summary>False when the operator chose to keep the draft instead of loading <paramref name="row"/>.</summary>
    public async Task<bool> TrySelectBlockAsync(LogicBlockRowViewModel? row)
    {
        if (row is null || ReferenceEquals(row, SelectedBlock))
        {
            return true;
        }

        if (IsDirty && ConfirmAsync is { } confirm)
        {
            string name = storedBlock_ is { } stored
                ? string.IsNullOrWhiteSpace(stored.Name) ? "(unnamed)" : stored.Name
                : "the new block";
            if (!await confirm(
                "Discard unsaved changes",
                $"Discard the unsaved changes to \"{name}\"? Anything not saved is lost.",
                "Discard").ConfigureAwait(true))
            {
                return false;
            }
        }

        SelectedBlock = row;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!IsEditingEnabled || SelectedBridge is not { } bridge)
        {
            return;
        }

        if (!connections_.TryGetSession(bridge.BridgeId, out BridgeConnectionManager.BridgeSession? session)
            || session is null)
        {
            EditorMessage = "✗ Bridge not connected.";
            return;
        }

        // A block with an area shares its tags: a direct tag it still carries joins the area's
        // tags as part of this save — the area is the point, not a separate move step.
        int shared = ConvertDirectRowsIntoArea();
        if (shared > 0)
        {
            AreaTags.RefreshUsed(ReferencedTagNames());
        }

        bool tagsDirty = AreaTags.IsDirty;
        bool blockDirty = ComputeBlockDirty();
        if (tagsDirty && AreaTags.IsNameBlank)
        {
            EditorMessage = "✗ " + LogicText.AreaNameRequired;
            return;
        }

        EditorMessage = "Saving…";

        // The area's tags go first: a block saved against them is consistent the moment it lands.
        if (tagsDirty)
        {
            LogicAreaTagsDto saved = AreaTags.ToDto();
            HmiLogicAreaTagsResult tagsResult = await session.Api
                .SaveLogicAreaTagsAsync(saved, CancellationToken.None)
                .ConfigureAwait(true);
            if (!tagsResult.Ok)
            {
                EditorMessage = "✗ " + (tagsResult.Error ?? "Area tags save failed.");
                report_?.Invoke($"Area tags save failed: {tagsResult.Error}");
                return;
            }

            AreaTags.MarkSaved(tagsResult.TagSource ?? saved);
        }

        LogicBlockDto block = BuildDraft();
        if (blockDirty)
        {
            HmiLogicSaveResult result = await session.Api.SaveLogicBlockAsync(block, CancellationToken.None)
                .ConfigureAwait(true);
            if (!result.Ok)
            {
                EditorMessage = "✗ " + (result.Error ?? "Save failed.");
                report_?.Invoke($"Logic save failed: {result.Error}");
                return;
            }

            pendingSelectId_ = result.Block?.Id;
        }

        await ReloadDefinitionsAsync(bridge.BridgeId).ConfigureAwait(true);
        await RefreshStateAsync().ConfigureAwait(true);
        EditorMessage = blockDirty ? "✓ Saved." : "✓ Area tags saved.";
        if (shared > 0)
        {
            EditorMessage += " " + LogicText.AreaTagsShared(shared, BlockArea.Trim());
        }

        report_?.Invoke(blockDirty
            ? $"Logic block '{block.Name}' saved"
            : $"Area tags '{AreaTags.Name.Trim()}' saved");
    }

    private bool CanSave() => IsEditingEnabled && IsDirty;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteSelected() => DeleteBlockAsync(SelectedBlock ?? null);

    private bool CanDelete() => IsEditingEnabled && storedBlock_ is not null;

    /// <summary>Deletes one listed block (also the Blocks window's per-row delete).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task DeleteBlock(LogicBlockRowViewModel? row) => DeleteBlockAsync(row);

    private bool CanEdit() => IsEditingEnabled;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddCondition()
    {
        AddConditionRow(new LogicConditionDto
        {
            Id = Guid.NewGuid(),
            Op = LogicConditionOps.On,
            Severity = LogicConditionSeverities.Block
        });
        DirtyChanged();
        RefreshEditorMode();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddElement()
    {
        AddElementRow(
            new LogicElementDto
            {
                Id = Guid.NewGuid(),
                Kind = LogicElementKinds.Contact,
                Op = LogicConditionOps.On,
                Severity = LogicConditionSeverities.Block
            },
            0);
        DirtyChanged();
        RefreshEditorMode();
    }

    [RelayCommand(CanExecute = nameof(CanAddStep))]
    private void AddStep()
    {
        AddStepRow(new LogicStepDto
        {
            Id = Guid.NewGuid(),
            Conditions =
            {
                new LogicConditionDto
                {
                    Id = Guid.NewGuid(),
                    Op = LogicConditionOps.On,
                    Severity = LogicConditionSeverities.Block
                }
            }
        });
        RenumberSteps();
        DirtyChanged();
        RefreshEditorMode();
    }

    private bool CanAddStep() => CanEdit() && IsSequence;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddAction()
    {
        AddActionRow(new LogicActionDto { Confirm = true });
        DirtyChanged();
        RefreshEditorMode();
    }

    /// <summary>Rewrites the flat condition list as the equivalent IEC network (the bridge expands it the same way).</summary>
    [RelayCommand(CanExecute = nameof(CanConvertToNetwork))]
    private void ConvertToNetwork()
    {
        List<LogicConditionDto> conditions = Conditions.Select(LogicEditorCodec.ConditionFromRow).ToList();
        List<LogicElementDto> network = LogicNetwork.Expand(conditions);
        if (network.Count == 0)
        {
            EditorMessage = "Nothing to convert — add a condition first.";
            return;
        }

        Elements.Clear();
        foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(network))
        {
            AddElementRow(element, depth);
        }

        RefreshEditorMode();
        DirtyChanged();
        EditorMessage = "✓ Conditions rewritten as a network — Save to keep it.";
    }

    private bool CanConvertToNetwork() => IsEditingEnabled && !IsSequence && Elements.Count == 0 && Conditions.Count > 0;

    [RelayCommand(CanExecute = nameof(CanBackToConditions))]
    private async Task BackToConditionsAsync()
    {
        if (ConfirmAsync is { } confirm
            && !await confirm(
                "Back to conditions",
                "Drop the network and go back to the flat condition list? Anything not saved is lost.",
                "Drop network").ConfigureAwait(true))
        {
            return;
        }

        Elements.Clear();
        if (Conditions.Count == 0)
        {
            AddConditionRow(new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                Op = LogicConditionOps.On,
                Severity = LogicConditionSeverities.Block
            });
        }

        RefreshEditorMode();
        DirtyChanged();
        EditorMessage = "✓ Network dropped — Save to keep the flat list.";
    }

    private bool CanBackToConditions() => IsEditingEnabled && !IsSequence && Elements.Count > 0;

    // ---- Lifecycle (driven by MainViewModel) ----

    /// <summary>Called when the connect/disconnect path changed the live sessions.</summary>
    public void OnConnectionChanged() => RefreshBridges();

    /// <summary>Called when a bridge's link state changed.</summary>
    public void OnLinkStatusesChanged() => RefreshBridges();

    /// <summary>Called when a bridge's post-reconnect resync landed: refetch definitions and state.</summary>
    public void OnBridgeReconnected(string bridgeId)
    {
        if (IsSelected(bridgeId))
        {
            _ = ReloadSelectedBridgeAsync();
        }
    }

    /// <summary>Called when a bridge reported a mapping change: the evaluated state is stale.</summary>
    public void OnMappingsChanged(string bridgeId)
    {
        if (IsSelected(bridgeId))
        {
            _ = RefreshStateAsync();
        }
    }

    /// <summary>Applies a pushed evaluated state; rows are updated in place, never rebuilt.</summary>
    public void ApplyState(string bridgeId, LogicStateSnapshot snapshot)
    {
        BridgeCache cache = Cache(bridgeId);
        cache.State = snapshot;
        cache.Index();
        if (!IsSelected(bridgeId))
        {
            return;
        }

        if (snapshot.Version != cache.Version && !reloadingDefinitions_)
        {
            // Another client changed the blocks: refetch the definitions behind the state.
            _ = ReloadDefinitionsAsync(bridgeId);
        }

        stateUpdatedAt_ = DateTimeOffset.Now;
        RefreshFreshness();
        applyingState_ = true;
        try
        {
            ApplyBlockStates(cache);
            SummaryText = BuildSummary(cache);
            ApplyEditorLive(cache);
            ApplyResultState(cache);
            RefreshAreaTags();
        }
        finally
        {
            applyingState_ = false;
        }
    }

    /// <summary>Starts/stops the 1 s state poll; the poll only runs while the page is visible.</summary>
    public void SetPageVisible(bool visible)
    {
        pageVisible_ = visible;
        if (visible)
        {
            pollTimer_.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            freshnessTimer_.Start();
            _ = RefreshStateAsync();
        }
        else
        {
            pollTimer_.Change(Timeout.Infinite, Timeout.Infinite);
            freshnessTimer_.Stop();
        }
    }

    /// <summary>
    /// Loads the selected bridge's definitions (cached) and state. Serialized behind the load
    /// gate so a selection change and an explicit load cannot interleave their list rebuilds.
    /// </summary>
    public async Task LoadSelectedBridgeAsync()
    {
        await loadGate_.WaitAsync().ConfigureAwait(true);
        try
        {
            await LoadSelectedBridgeCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            loadGate_.Release();
        }
    }

    private async Task LoadSelectedBridgeCoreAsync()
    {
        if (SelectedBridge is not { } bridge)
        {
            ClearEditor();
            return;
        }

        BridgeCache cache = Cache(bridge.BridgeId);
        if (cache.Blocks is null)
        {
            await FetchDefinitionsAsync(bridge.BridgeId, cache).ConfigureAwait(true);
        }
        else
        {
            ApplyLoadedBlocks(bridge.BridgeId, cache);
        }

        await RefreshStateAsync().ConfigureAwait(true);
    }

    /// <summary>Refetches the selected bridge's definitions and state (post-reconnect).</summary>
    public async Task ReloadSelectedBridgeAsync()
    {
        await loadGate_.WaitAsync().ConfigureAwait(true);
        try
        {
            if (SelectedBridge is not { } bridge)
            {
                await LoadSelectedBridgeCoreAsync().ConfigureAwait(true);
                return;
            }

            await FetchDefinitionsAsync(bridge.BridgeId, Cache(bridge.BridgeId)).ConfigureAwait(true);
            await RefreshStateAsync().ConfigureAwait(true);
        }
        finally
        {
            loadGate_.Release();
        }
    }

    /// <summary>One state refresh from the selected bridge, applied on the UI thread.</summary>
    public async Task RefreshStateAsync()
    {
        if (disposed_ || SelectedBridge is not { } bridge)
        {
            return;
        }

        if (!connections_.TryGetSession(bridge.BridgeId, out BridgeConnectionManager.BridgeSession? session)
            || session is null)
        {
            return;
        }

        LogicStateSnapshot snapshot;
        try
        {
            snapshot = await session.Api.GetLogicStateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The page keeps the last known state; the link strip already reports the outage.
            return;
        }

        await post_(() =>
        {
            ApplyState(bridge.BridgeId, snapshot);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        disposed_ = true;
        pollTimer_.Dispose();
        freshnessTimer_.Stop();
    }

    // ---- Internals ----

    private async Task PollAsync()
    {
        if (disposed_ || !pageVisible_ || Interlocked.Exchange(ref polling_, 1) == 1)
        {
            return;
        }

        try
        {
            await RefreshStateAsync().ConfigureAwait(false);
        }
        catch
        {
            // transient; the next tick tries again
        }
        finally
        {
            Interlocked.Exchange(ref polling_, 0);
        }
    }

    private void RefreshBridges()
    {
        string? selectedId = SelectedBridge?.BridgeId;
        List<LogicBridgeOption> options = connections_.LinkStatuses
            .Select(status => new LogicBridgeOption(status.BridgeId, status.State))
            .ToList();

        if (!Bridges.SequenceEqual(options))
        {
            Bridges.Clear();
            foreach (LogicBridgeOption option in options)
            {
                Bridges.Add(option);
            }
        }

        LogicBridgeOption? selection = selectedId is null
            ? null
            : Bridges.FirstOrDefault(option => string.Equals(option.BridgeId, selectedId, StringComparison.OrdinalIgnoreCase));
        selection ??= Bridges.FirstOrDefault(option => option.IsConnected) ?? Bridges.FirstOrDefault();

        bool sameBridge = selection is not null
            && SelectedBridge is { } current
            && string.Equals(current.BridgeId, selection.BridgeId, StringComparison.OrdinalIgnoreCase);
        suppressBridgeLoad_ = sameBridge;
        try
        {
            SelectedBridge = selection;
        }
        finally
        {
            suppressBridgeLoad_ = false;
        }

        if (sameBridge)
        {
            NotifyBridgeDerived();
        }
    }

    /// <summary>
    /// Everything derived from the selected bridge's link state or the block list: the pill and the
    /// hint, the freshness line, and every command and row guard that is only as live as the link.
    /// </summary>
    private void NotifyBridgeDerived()
    {
        OnPropertyChanged(nameof(IsEditingEnabled));
        OnPropertyChanged(nameof(BridgeStatusText));
        OnPropertyChanged(nameof(EditingHint));
        OnPropertyChanged(nameof(IsBridgeOk));
        OnPropertyChanged(nameof(IsBridgeWarn));
        OnPropertyChanged(nameof(IsBridgeBad));
        OnPropertyChanged(nameof(IsResultStale));
        OnPropertyChanged(nameof(EmptyBlocksMessage));
        OnPropertyChanged(nameof(ResultEmptyTitle));
        OnPropertyChanged(nameof(ResultEmptyMessage));
        RefreshFreshness();
        RefreshEditorMode();
        NotifyCommands();
    }

    private async Task FetchDefinitionsAsync(string bridgeId, BridgeCache cache)
    {
        if (!connections_.TryGetSession(bridgeId, out BridgeConnectionManager.BridgeSession? session)
            || session is null)
        {
            // Nothing was read: the cache stays empty (Blocks null) so a later load retries, and the
            // empty state reads "unreachable" instead of remembering a failure as "no blocks".
            ApplyLoadedBlocks(bridgeId, cache);
            return;
        }

        if (IsSelected(bridgeId))
        {
            IsBlocksLoading = true;
        }

        try
        {
            HmiLogicDefinitions definitions = await session.Api.GetLogicDefinitionsAsync(CancellationToken.None)
                .ConfigureAwait(true);
            cache.Blocks = definitions.Blocks.ToList();
            cache.AreaTags = (definitions.TagSources ?? Array.Empty<LogicAreaTagsDto>()).ToList();
            cache.Version = definitions.Version;
            if (IsSelected(bridgeId))
            {
                BlocksError = string.Empty;
            }
        }
        catch (Exception ex)
        {
            // Keep the list as last read (or empty when it never was) and report the read's error.
            if (IsSelected(bridgeId))
            {
                BlocksError = ex.Message;
            }

            report_?.Invoke("Logic definitions: " + ex.Message);
        }
        finally
        {
            if (IsSelected(bridgeId))
            {
                IsBlocksLoading = false;
            }

            ApplyLoadedBlocks(bridgeId, cache);
        }
    }

    private async Task ReloadDefinitionsAsync(string bridgeId)
    {
        if (reloadingDefinitions_)
        {
            return;
        }

        if (!connections_.TryGetSession(bridgeId, out BridgeConnectionManager.BridgeSession? session)
            || session is null)
        {
            return;
        }

        reloadingDefinitions_ = true;
        try
        {
            HmiLogicDefinitions definitions = await session.Api.GetLogicDefinitionsAsync(CancellationToken.None)
                .ConfigureAwait(true);
            BridgeCache cache = Cache(bridgeId);
            cache.Blocks = definitions.Blocks.ToList();
            cache.AreaTags = (definitions.TagSources ?? Array.Empty<LogicAreaTagsDto>()).ToList();
            cache.Version = definitions.Version;
            if (IsSelected(bridgeId))
            {
                ApplyLoadedBlocks(bridgeId, cache);
            }
        }
        catch (Exception ex)
        {
            report_?.Invoke("Logic definitions: " + ex.Message);
        }
        finally
        {
            reloadingDefinitions_ = false;
        }
    }

    private void ApplyLoadedBlocks(string bridgeId, BridgeCache cache)
    {
        if (!IsSelected(bridgeId))
        {
            return;
        }

        RebuildBlockList(cache);
        RebuildRelayNames(cache);
        RestoreSelection();
        SummaryText = BuildSummary(cache);
        ApplyBlockStates(cache);
        ApplyEditorLive(cache);
        ApplyResultState(cache);
        RefreshAreaTags();
    }

    /// <summary>Collects the relays every loaded block's coils drive, in block and row order.</summary>
    private void RebuildRelayNames(BridgeCache cache)
    {
        RelayNames.Clear();
        foreach (LogicBlockDto block in cache.Blocks ?? new List<LogicBlockDto>())
        {
            if (block.Elements.Count == 0)
            {
                continue;
            }

            foreach ((LogicElementDto element, int _) in LogicNetwork.Walk(block.Elements))
            {
                if (LogicElementKinds.IsCoil(element.Kind)
                    && !string.IsNullOrWhiteSpace(element.Relay)
                    && !RelayNames.Contains(element.Relay, StringComparer.OrdinalIgnoreCase))
                {
                    RelayNames.Add(element.Relay);
                }
            }
        }
    }

    private void RebuildBlockList(BridgeCache cache)
    {
        List<LogicBlockDto> blocks = cache.Blocks ?? new List<LogicBlockDto>();
        var rows = new List<LogicBlockRowViewModel>(blocks.Count);
        foreach (LogicBlockDto block in blocks)
        {
            var row = new LogicBlockRowViewModel(block);
            row.ApplyState(cache.BlockState(block.Id));
            rows.Add(row);
        }

        // Areas first — the main section whose tags the blocks share — the blocks without one
        // last; inside an area the interlock groups read exactly as before.
        bool anyArea = rows.Any(row => row.Area.Length > 0);
        List<LogicAreaViewModel> areas = rows
            .GroupBy(row => row.Area, StringComparer.OrdinalIgnoreCase)
            .OrderBy(area => area.Key.Length == 0 ? 1 : 0)
            .ThenBy(area => area.Key, StringComparer.OrdinalIgnoreCase)
            .Select(area => new LogicAreaViewModel(
                area.Key.Length == 0 ? "No area" : area.Key,
                showHeading: anyArea,
                BuildGroups(area)))
            .ToList();

        BlockAreas.Clear();
        foreach (LogicAreaViewModel area in areas)
        {
            BlockAreas.Add(area);
        }

        HasBlocks = rows.Count > 0;
        foreach (LogicAreaViewModel area in BlockAreas)
        {
            foreach (LogicGroupViewModel group in area.Groups)
            {
                group.UpdateSummary();
            }
        }

        static List<LogicGroupViewModel> BuildGroups(IEnumerable<LogicBlockRowViewModel> areaRows)
        {
            List<LogicBlockRowViewModel> members = areaRows.ToList();
            bool anyGrouped = members.Any(row => row.Group.Length > 0);
            return anyGrouped
                ? members
                    .GroupBy(row => row.Group, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key.Length == 0 ? 0 : 1)
                    .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new LogicGroupViewModel(
                        group.Key.Length == 0 ? "Ungrouped" : group.Key,
                        showHeading: true,
                        group.OrderBy(row => row.Block.Order).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)))
                    .ToList()
                : new List<LogicGroupViewModel> { new(string.Empty, showHeading: false, members) };
        }
    }

    /// <summary>
    /// Re-selects the block the editor was on after a list rebuild, keeping unsaved edits when it
    /// is the same block and reloading the draft when the save/delete path asked for it.
    /// </summary>
    private void RestoreSelection()
    {
        Guid? wanted = pendingSelectId_ ?? storedBlock_?.Id;
        bool force = pendingSelectId_ is not null || forceReloadDraft_;
        pendingSelectId_ = null;
        forceReloadDraft_ = false;

        LogicBlockRowViewModel? row = wanted is { } id ? FindBlockRow(id) : null;
        if (row is null && selectFirstAfterReload_)
        {
            selectFirstAfterReload_ = false;
            row = AllBlockRows().FirstOrDefault();
        }

        if (row is null && wanted is null && !editingNewBlock_)
        {
            row = AllBlockRows().FirstOrDefault();
        }

        if (row is null)
        {
            SelectedBlock = null;
            if (wanted is not null)
            {
                // The block disappeared (deleted elsewhere): fall back to a fresh draft.
                editingNewBlock_ = true;
                LoadDraft(LogicEditorCodec.NewDraftBlock(CurrentBlockCount), null);
            }

            UpdateResultDetail();
            return;
        }

        forceReloadDraft_ = force;
        SelectedBlock = row;
    }

    private void ClearEditor()
    {
        catalog_ = LogicTagCatalog.Empty;
        storedBlock_ = null;
        editingNewBlock_ = false;
        buildingRows_ = true;
        try
        {
            Conditions.Clear();
            Elements.Clear();
            RelayNames.Clear();
            Steps.Clear();
            Actions.Clear();
            BlockName = string.Empty;
            BlockGroup = string.Empty;
            BlockArea = string.Empty;
            BlockDescription = string.Empty;
            BlockTags = string.Empty;
            BlockEnabled = true;
            SelectedKind = KindOptions[0];
        }
        finally
        {
            buildingRows_ = false;
        }

        BlockAreas.Clear();
        HasBlocks = false;
        IsBlocksLoading = false;
        BlocksError = string.Empty;
        SummaryText = "no logic blocks configured";
        SelectedBlock = null;
        ResultElements.Clear();
        ResultSteps.Clear();
        UpdateResultDetail();
        RefreshEditorMode();
        RefreshAreaFallbackHint();
        DirtyChanged();
        NotifyCommands();
    }

    private void LoadDraft(LogicBlockDto block, LogicBlockDto? stored)
    {
        buildingRows_ = true;
        try
        {
            storedBlock_ = stored;
            editingNewBlock_ = stored is null;
            catalog_ = BuildCatalog();
            BridgeCache? cache = SelectedBridge is { } bridge ? Cache(bridge.BridgeId) : null;
            AreaTags.Load(
                block,
                cache?.AreaTags ?? new List<LogicAreaTagsDto>(),
                cache?.Blocks ?? new List<LogicBlockDto>(),
                catalog_);
            BlockName = block.Name;
            SelectedKind = LogicSelectOption.Find(KindOptions, block.Kind) ?? KindOptions[0];
            BlockEnabled = block.Enabled;
            BlockGroup = block.Group;
            BlockArea = block.Area;
            BlockDescription = block.Description;
            BlockTags = string.Join(", ", block.Tags);

            Conditions.Clear();
            Elements.Clear();
            Steps.Clear();
            Actions.Clear();
            foreach (LogicConditionDto condition in block.Conditions)
            {
                AddConditionRow(condition);
            }

            foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(block.Elements))
            {
                AddElementRow(element, depth);
            }

            foreach (LogicStepDto step in block.Steps)
            {
                AddStepRow(step);
            }

            foreach (LogicActionDto action in block.Actions)
            {
                AddActionRow(action);
            }

            RenumberSteps();
            EditorMessage = string.Empty;
            UpdateResultDetail();
        }
        finally
        {
            buildingRows_ = false;
        }

        RefreshEditorMode();
        RefreshAreaTags();
        RefreshAreaFallbackHint();
        DirtyChanged();
        NotifyCommands();
    }

    private LogicTagCatalog BuildCatalog() =>
        SelectedBridge is { } bridge
            ? LogicTagCatalog.Build(connections_.Cache.Tags.Where(
                entry => string.Equals(entry.Key.BridgeId, bridge.BridgeId, StringComparison.OrdinalIgnoreCase)))
            : LogicTagCatalog.Empty;

    private void AddConditionRow(LogicConditionDto condition)
    {
        var row = new LogicConditionRowViewModel(condition, catalog_, RemoveCondition, AreaTags.Entries);
        row.PropertyChanged += OnEditorRowChanged;
        Conditions.Add(row);
    }

    private void AddElementRow(LogicElementDto element, int indent)
    {
        var row = new LogicElementRowViewModel(element, indent, catalog_, RemoveElement, RelayNames, AreaTags.Entries);
        row.PropertyChanged += OnEditorRowChanged;
        Elements.Add(row);
    }

    private void AddStepRow(LogicStepDto step)
    {
        var row = new LogicStepRowViewModel(step, catalog_, Steps, RemoveStep, RenumberSteps, AreaTags.Entries);
        row.PropertyChanged += OnEditorRowChanged;
        row.Conditions.CollectionChanged += OnStepConditionsChanged;
        foreach (LogicConditionRowViewModel condition in row.Conditions)
        {
            condition.PropertyChanged += OnEditorRowChanged;
        }

        Steps.Add(row);
    }

    /// <summary>Keeps the dirty tracker attached to condition rows a step gains or loses later.</summary>
    private void OnStepConditionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (LogicConditionRowViewModel row in e.NewItems?.Cast<LogicConditionRowViewModel>()
            ?? Enumerable.Empty<LogicConditionRowViewModel>())
        {
            row.PropertyChanged += OnEditorRowChanged;
        }

        foreach (LogicConditionRowViewModel row in e.OldItems?.Cast<LogicConditionRowViewModel>()
            ?? Enumerable.Empty<LogicConditionRowViewModel>())
        {
            row.PropertyChanged -= OnEditorRowChanged;
        }

        DirtyChanged();
    }

    private void AddActionRow(LogicActionDto action)
    {
        var row = new LogicActionRowViewModel(action, catalog_, RemoveAction, AreaTags.Entries);
        row.PropertyChanged += OnEditorRowChanged;
        Actions.Add(row);
    }

    private void RemoveCondition(LogicConditionRowViewModel row)
    {
        Conditions.Remove(row);
        DirtyChanged();
        RefreshEditorMode();
    }

    private void RemoveElement(LogicElementRowViewModel row)
    {
        Elements.Remove(row);
        DirtyChanged();
        RefreshEditorMode();
    }

    private void RemoveStep(LogicStepRowViewModel row)
    {
        Steps.Remove(row);
        RenumberSteps();
        DirtyChanged();
        RefreshEditorMode();
    }

    private void RemoveAction(LogicActionRowViewModel row)
    {
        Actions.Remove(row);
        DirtyChanged();
        RefreshEditorMode();
    }

    /// <summary>
    /// Re-numbers the step labels and refreshes every step's move guards — an order change makes
    /// the first step's "up" and the last step's "down" unavailable.
    /// </summary>
    private void RenumberSteps()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].StepLabel = $"STEP {i + 1}";
            Steps[i].NotifyMoveCanExecuteChanged();
        }
    }

    private async Task DeleteBlockAsync(LogicBlockRowViewModel? row)
    {
        if (!IsEditingEnabled || SelectedBridge is not { } bridge)
        {
            EditorMessage = "Editing is disabled while the bridge is unreachable.";
            return;
        }

        LogicBlockDto? target = row?.Block ?? storedBlock_;
        if (target is null)
        {
            EditorMessage = "Nothing saved to delete.";
            return;
        }

        string name = string.IsNullOrWhiteSpace(target.Name) ? target.Id.ToString() : target.Name;
        if (ConfirmAsync is { } confirm
            && !await confirm("Delete block", $"Delete \"{name}\"? Anything not saved is lost.", "Delete")
                .ConfigureAwait(true))
        {
            return;
        }

        if (!connections_.TryGetSession(bridge.BridgeId, out BridgeConnectionManager.BridgeSession? session)
            || session is null)
        {
            EditorMessage = "✗ Bridge not connected.";
            return;
        }

        HmiLogicSaveResult result = await session.Api.DeleteLogicBlockAsync(target.Id, CancellationToken.None)
            .ConfigureAwait(true);
        if (!result.Ok)
        {
            EditorMessage = "✗ " + (result.Error ?? "Delete failed.");
            return;
        }

        editingNewBlock_ = false;
        storedBlock_ = null;
        selectFirstAfterReload_ = true;
        await ReloadDefinitionsAsync(bridge.BridgeId).ConfigureAwait(true);
        await RefreshStateAsync().ConfigureAwait(true);
        EditorMessage = "✓ Block deleted.";
        report_?.Invoke($"Logic block '{name}' deleted");
    }

    private void OnEditorRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (applyingState_ || buildingRows_)
        {
            return;
        }

        TrySharePickedTag(sender, e.PropertyName);
        DirtyChanged();
    }

    /// <summary>
    /// A tag picked on a row of a block that has an area joins that area's tags the moment it is
    /// picked: it becomes a named entry (named from the tag's display name) and the row references
    /// it, so every block of the area can pick it — no separate move step.
    /// </summary>
    private void TrySharePickedTag(object? sender, string? propertyName)
    {
        if (!IsEditingEnabled || BlockArea.Trim().Length == 0 || AreaTags.IsNameBlank)
        {
            return;
        }

        bool shared = propertyName switch
        {
            nameof(LogicConditionRowViewModel.SelectedTag) when sender is LogicConditionRowViewModel condition
                => ShareConditionRow(condition),
            nameof(LogicElementRowViewModel.SelectedTag) when sender is LogicElementRowViewModel element
                => ShareElementRow(element),
            nameof(LogicActionRowViewModel.SelectedTag) when sender is LogicActionRowViewModel action
                => ShareActionRow(action),
            nameof(LogicStepRowViewModel.CompletionTag) when sender is LogicStepRowViewModel step
                => ShareCompletionRow(step),
            _ => false
        };

        if (shared)
        {
            AreaTags.RefreshUsed(ReferencedTagNames());
        }
    }

    /// <summary>A Tags-tab edit (the area's name, an entry, the dirty flag) is an editor edit.</summary>
    private void OnAreaTagsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (buildingRows_ || applyingState_)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(LogicAreaTagsViewModel.IsDirty), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(LogicAreaTagsViewModel.Name), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(LogicAreaTagsViewModel.UsedByCount), StringComparison.Ordinal))
        {
            DirtyChanged();
        }

        if (string.Equals(e.PropertyName, nameof(LogicAreaTagsViewModel.Name), StringComparison.Ordinal))
        {
            // The name belongs to the block's area, and renaming changes who shares the tags. A
            // scope change adopts the new area's list — re-anchor the rows onto it at once, so no
            // reference is left dangling and no picker is left blank.
            ReanchorAreaReferences();
            AreaTags.RefreshUsed(ReferencedTagNames());
            AreaTags.RefreshUsedCount(SelectedBridge is { } bridge
                ? Cache(bridge.BridgeId).Blocks ?? new List<LogicBlockDto>()
                : new List<LogicBlockDto>());
            RefreshAreaFallbackHint();
        }
    }

    private void DirtyChanged()
    {
        if (buildingRows_)
        {
            return;
        }

        IsDirty = ComputeDirty();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private bool ComputeDirty() => ComputeBlockDirty() || AreaTags.IsDirty;

    private bool ComputeBlockDirty()
    {
        if (storedBlock_ is null)
        {
            // A new block is saveable once it has a name; the bridge validates the rest.
            return BlockName.Trim().Length > 0;
        }

        return !LogicEditorCodec.Matches(BuildDraft(), storedBlock_);
    }

    private LogicBlockDto BuildDraft() => LogicEditorCodec.Build(
        storedBlock_,
        CurrentBlockCount,
        BlockName,
        SelectedKind?.Value ?? LogicBlockKinds.Interlock,
        BlockEnabled,
        BlockGroup,
        BlockArea,
        AreaTags.Name,
        BlockDescription,
        BlockTags,
        Conditions,
        Elements,
        Steps,
        Actions);

    private int CurrentBlockCount =>
        SelectedBridge is { } bridge && caches_.TryGetValue(bridge.BridgeId, out BridgeCache? cache)
            ? cache.Blocks?.Count ?? 0
            : 0;

    private void RefreshEditorMode()
    {
        OnPropertyChanged(nameof(IsSequence));
        OnPropertyChanged(nameof(ShowsNetwork));
        OnPropertyChanged(nameof(ShowsConditions));
        OnPropertyChanged(nameof(ShowsSteps));
        NotifyRowCounts();
        NotifyRowCommands();
        ConvertToNetworkCommand.NotifyCanExecuteChanged();
        BackToConditionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Keeps every row's remove / indent / move guards in step with the bridge's editability.</summary>
    private void NotifyRowCommands()
    {
        foreach (LogicConditionRowViewModel row in Conditions)
        {
            row.CanEdit = IsEditingEnabled;
        }

        foreach (LogicElementRowViewModel row in Elements)
        {
            row.CanEdit = IsEditingEnabled;
        }

        foreach (LogicStepRowViewModel row in Steps)
        {
            row.CanEdit = IsEditingEnabled;
            foreach (LogicConditionRowViewModel condition in row.Conditions)
            {
                condition.CanEdit = IsEditingEnabled;
            }
        }

        foreach (LogicActionRowViewModel row in Actions)
        {
            row.CanEdit = IsEditingEnabled;
        }

        AreaTags.SetCanEdit(IsEditingEnabled);
    }

    /// <summary>
    /// Refreshes the Tags tab: the entries' live chips from the tag cache, which of them this
    /// block reads right now, and how many loaded blocks share the area. Only while the tab is
    /// visible — opening it refreshes it, so the hidden tab costs nothing per state tick.
    /// </summary>
    private void RefreshAreaTags()
    {
        if (!IsTagsTab)
        {
            return;
        }

        if (SelectedBridge is { } bridge)
        {
            AreaTags.RefreshLive(bridge.BridgeId, connections_.Cache);
            AreaTags.RefreshUsedCount(Cache(bridge.BridgeId).Blocks ?? new List<LogicBlockDto>());
        }

        AreaTags.RefreshUsed(ReferencedTagNames());
    }

    /// <summary>Every area-tags name the editor's rows reference right now.</summary>
    private List<string> ReferencedTagNames()
    {
        List<string> names = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name.Trim()))
            {
                names.Add(name.Trim());
            }
        }

        foreach (LogicConditionRowViewModel row in Conditions)
        {
            Add(row.Tag);
        }

        foreach (LogicElementRowViewModel row in Elements)
        {
            Add(row.Tag);
        }

        foreach (LogicStepRowViewModel step in Steps)
        {
            Add(step.CompletionTagName);
            foreach (LogicConditionRowViewModel row in step.Conditions)
            {
                Add(row.Tag);
            }
        }

        foreach (LogicActionRowViewModel row in Actions)
        {
            Add(row.Tag);
        }

        return names;
    }

    /// <summary>
    /// Every direct tag the block still carries joins its area's tags: one entry per distinct tag,
    /// named after its display name, with the rows rewritten to reference it (the pair lives on the
    /// entry now). Runs per row when a tag is picked, when the block is given an area, and as part
    /// of a save — the block evaluates the same before and after.
    /// </summary>
    private int ConvertDirectRowsIntoArea()
    {
        if (BlockArea.Trim().Length == 0 || AreaTags.IsNameBlank)
        {
            return 0;
        }

        int shared = 0;
        foreach (LogicConditionRowViewModel row in Conditions)
        {
            shared += ShareConditionRow(row) ? 1 : 0;
        }

        foreach (LogicElementRowViewModel row in Elements)
        {
            shared += ShareElementRow(row) ? 1 : 0;
        }

        foreach (LogicStepRowViewModel step in Steps)
        {
            shared += ShareCompletionRow(step) ? 1 : 0;
            foreach (LogicConditionRowViewModel row in step.Conditions)
            {
                shared += ShareConditionRow(row) ? 1 : 0;
            }
        }

        foreach (LogicActionRowViewModel row in Actions)
        {
            shared += ShareActionRow(row) ? 1 : 0;
        }

        return shared;
    }

    private bool ShareConditionRow(LogicConditionRowViewModel row)
    {
        if (row.HasBlockTag || row.ItemId.Length == 0)
        {
            return false;
        }

        row.SelectedBlockTag = AreaTags.EnsureEntry(
            UniqueTagName(TagNameFor(row.SourceId, row.ItemId)),
            row.SourceId,
            row.ItemId);
        return true;
    }

    private bool ShareElementRow(LogicElementRowViewModel row)
    {
        if (!row.IsTagContact || row.ItemId.Length == 0)
        {
            return false;
        }

        row.SelectedBlockTag = AreaTags.EnsureEntry(
            UniqueTagName(TagNameFor(row.SourceId, row.ItemId)),
            row.SourceId,
            row.ItemId);
        return true;
    }

    private bool ShareActionRow(LogicActionRowViewModel row)
    {
        if (row.HasBlockTag || row.ItemId.Length == 0)
        {
            return false;
        }

        row.SelectedBlockTag = AreaTags.EnsureEntry(
            UniqueTagName(TagNameFor(row.SourceId, row.ItemId)),
            row.SourceId,
            row.ItemId);
        return true;
    }

    private bool ShareCompletionRow(LogicStepRowViewModel step)
    {
        if (step.HasCompletionTag
            || string.IsNullOrWhiteSpace(step.CompletionSourceId)
            || string.IsNullOrWhiteSpace(step.CompletionItemId))
        {
            return false;
        }

        step.SelectedCompletionTag = AreaTags.EnsureEntry(
            UniqueTagName(TagNameFor(step.CompletionSourceId!, step.CompletionItemId!)),
            step.CompletionSourceId!,
            step.CompletionItemId!);
        return true;
    }

    /// <summary>
    /// Re-anchors every row onto the adopted area's tags after a scope change: the row's reference
    /// is ensured in the list — with its binding when the entry is new or still unassigned — and
    /// the row points at the list's own entry, so its picker shows it and a save keeps resolving.
    /// </summary>
    private void ReanchorAreaReferences()
    {
        foreach (LogicConditionRowViewModel row in Conditions)
        {
            ReanchorCondition(row);
        }

        foreach (LogicElementRowViewModel row in Elements)
        {
            if (row.SelectedBlockTag is { } reference)
            {
                LogicTagEntryViewModel entry = AreaTags.EnsureReference(
                    reference.Name.Trim(), reference.SourceId, reference.ItemId);
                if (!ReferenceEquals(entry, reference))
                {
                    row.SelectedBlockTag = entry;
                }
            }
        }

        foreach (LogicStepRowViewModel step in Steps)
        {
            if (step.SelectedCompletionTag is { } handshake)
            {
                LogicTagEntryViewModel entry = AreaTags.EnsureReference(
                    handshake.Name.Trim(), handshake.SourceId, handshake.ItemId);
                if (!ReferenceEquals(entry, handshake))
                {
                    step.SelectedCompletionTag = entry;
                }
            }

            foreach (LogicConditionRowViewModel row in step.Conditions)
            {
                ReanchorCondition(row);
            }
        }

        foreach (LogicActionRowViewModel row in Actions)
        {
            if (row.SelectedBlockTag is { } reference)
            {
                LogicTagEntryViewModel entry = AreaTags.EnsureReference(
                    reference.Name.Trim(), reference.SourceId, reference.ItemId);
                if (!ReferenceEquals(entry, reference))
                {
                    row.SelectedBlockTag = entry;
                }
            }
        }

        void ReanchorCondition(LogicConditionRowViewModel row)
        {
            if (row.SelectedBlockTag is { } reference)
            {
                LogicTagEntryViewModel entry = AreaTags.EnsureReference(
                    reference.Name.Trim(), reference.SourceId, reference.ItemId);
                if (!ReferenceEquals(entry, reference))
                {
                    row.SelectedBlockTag = entry;
                }
            }
        }
    }

    /// <summary>A readable entry name for a direct tag: its display name, upper-cased and sanitized.</summary>
    private string TagNameFor(string sourceId, string itemId)
    {
        string label = catalog_.DisplayName(sourceId, itemId);
        StringBuilder builder = new();
        foreach (char c in label)
        {
            char mapped = char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_';
            if (mapped == '_' && (builder.Length == 0 || builder[^1] == '_'))
            {
                continue;
            }

            builder.Append(mapped);
        }

        string name = builder.ToString().Trim('_');
        int limit = LogicLimits.MaxTagNameLength - 4;
        if (name.Length > limit)
        {
            name = name[..limit].Trim('_');
        }

        return name.Length == 0 ? "TAG" : name;
    }

    private string UniqueTagName(string name)
    {
        if (AreaTags.FindByName(name) is null)
        {
            return name;
        }

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = name + "_" + suffix;
            if (candidate.Length > LogicLimits.MaxTagNameLength)
            {
                candidate = candidate[..LogicLimits.MaxTagNameLength];
            }

            if (AreaTags.FindByName(candidate) is null)
            {
                return candidate;
            }
        }

        return name;
    }

    /// <summary>Deletes the area's tags (the bridge refuses while blocks still use them).</summary>
    private async Task DeleteAreaTagsAsync()
    {
        if (!IsEditingEnabled || SelectedBridge is not { } bridge)
        {
            EditorMessage = "Editing is disabled while the bridge is unreachable.";
            return;
        }

        string name = AreaTags.Name.Trim();
        if (name.Length == 0)
        {
            EditorMessage = "✗ " + LogicText.AreaNameRequired;
            return;
        }

        string dropped = IsDirty ? " Unsaved changes in the editor are dropped." : string.Empty;
        if (ConfirmAsync is { } confirm
            && !await confirm(
                "Delete area tags",
                $"Delete the \"{name}\" area tags? Its entries are removed for every block that reads them.{dropped}",
                "Delete").ConfigureAwait(true))
        {
            return;
        }

        if (!connections_.TryGetSession(bridge.BridgeId, out BridgeConnectionManager.BridgeSession? session)
            || session is null)
        {
            EditorMessage = "✗ Bridge not connected.";
            return;
        }

        HmiLogicAreaTagsResult result = await session.Api
            .DeleteLogicAreaTagsAsync(name, CancellationToken.None)
            .ConfigureAwait(true);
        if (!result.Ok)
        {
            EditorMessage = "✗ " + (result.Error ?? "Delete failed.");
            return;
        }

        forceReloadDraft_ = true;
        await ReloadDefinitionsAsync(bridge.BridgeId).ConfigureAwait(true);
        await RefreshStateAsync().ConfigureAwait(true);
        EditorMessage = "✓ Area tags deleted.";
        report_?.Invoke($"Area tags '{name}' deleted");
    }

    private void NotifyRowCounts()
    {
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(HasElements));
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(HasActions));
    }

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DeleteBlockCommand.NotifyCanExecuteChanged();
        NewBlockCommand.NotifyCanExecuteChanged();
        AddConditionCommand.NotifyCanExecuteChanged();
        AddElementCommand.NotifyCanExecuteChanged();
        AddStepCommand.NotifyCanExecuteChanged();
        AddActionCommand.NotifyCanExecuteChanged();
        ConvertToNetworkCommand.NotifyCanExecuteChanged();
        BackToConditionsCommand.NotifyCanExecuteChanged();
    }

    private void ApplyBlockStates(BridgeCache cache)
    {
        foreach (LogicAreaViewModel area in BlockAreas)
        {
            foreach (LogicGroupViewModel group in area.Groups)
            {
                foreach (LogicBlockRowViewModel row in group.Blocks)
                {
                    row.ApplyState(cache.BlockState(row.BlockId));
                }

                group.UpdateSummary();
            }
        }
    }

    private void ApplyEditorLive(BridgeCache cache)
    {
        Guid blockId = storedBlock_?.Id ?? Guid.Empty;
        Dictionary<Guid, LogicElementStateDto> elements = blockId == Guid.Empty
            ? new Dictionary<Guid, LogicElementStateDto>()
            : cache.ElementsFor(blockId);
        Dictionary<Guid, LogicStepStateDto> steps = blockId == Guid.Empty
            ? new Dictionary<Guid, LogicStepStateDto>()
            : cache.StepsFor(blockId);

        foreach (LogicConditionRowViewModel row in Conditions)
        {
            row.ApplyState(StateLookup.Lookup(elements, row.ConditionId));
        }

        foreach (LogicElementRowViewModel row in Elements)
        {
            row.ApplyState(StateLookup.Lookup(elements, row.ElementId));
        }

        foreach (LogicStepRowViewModel step in Steps)
        {
            step.ApplyState(StateLookup.Lookup(steps, step.StepId));
            foreach (LogicConditionRowViewModel row in step.Conditions)
            {
                row.ApplyState(StateLookup.Lookup(elements, row.ConditionId));
            }
        }
    }

    private void UpdateResultDetail()
    {
        ResultElements.Clear();
        ResultSteps.Clear();
        LogicBlockDto? block = storedBlock_;
        LogicAreaTagsDto? areaTags = CurrentAreaTags();

        ResultTitle = block is null || block.Id == Guid.Empty
            ? (editingNewBlock_ ? "New block — not saved yet" : "No block selected")
            : (string.IsNullOrWhiteSpace(block.Name) ? "(unnamed)" : block.Name);
        ResultKindLabel = block is null ? string.Empty : LogicText.BlockKindLabel(block.Kind);
        ResultDescription = block?.Description ?? string.Empty;
        ResultStateVisible = block is not null && block.Id != Guid.Empty;

        if (block is null || block.Id == Guid.Empty)
        {
            ResultShowsNetwork = false;
            ResultShowsSteps = false;
            HasResultRows = false;
            ResultReason = string.Empty;
            IsReasonBad = false;
            IsReasonOk = false;
            ResultStateWord = string.Empty;
            IsResultOk = false;
            IsResultBad = false;
            IsResultWarn = false;
            IsResultMuted = false;
            NotifyResultEmptyState();
            return;
        }

        if (string.Equals(block.Kind, LogicBlockKinds.Sequence, StringComparison.OrdinalIgnoreCase))
        {
            ResultShowsNetwork = false;
            ResultShowsSteps = true;
            for (int i = 0; i < block.Steps.Count; i++)
            {
                LogicStepDto step = block.Steps[i];
                var stepRow = new LogicResultStepRowViewModel(step, i + 1);
                foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(LogicNetwork.For(step)))
                {
                    stepRow.Elements.Add(new LogicResultElementRowViewModel(element, depth, catalog_, areaTags));
                }

                ResultSteps.Add(stepRow);
            }

            HasResultRows = ResultSteps.Count > 0;
        }
        else
        {
            ResultShowsNetwork = true;
            ResultShowsSteps = false;
            foreach ((LogicElementDto element, int depth) in LogicNetwork.Walk(LogicNetwork.For(block)))
            {
                ResultElements.Add(new LogicResultElementRowViewModel(element, depth, catalog_, areaTags));
            }

            HasResultRows = ResultElements.Count > 0;
        }

        NotifyResultEmptyState();

        if (SelectedBridge is { } selectedBridge)
        {
            // Never show the previous block's state while the next snapshot is in flight.
            ApplyResultState(Cache(selectedBridge.BridgeId));
        }
    }

    /// <summary>The detail pane's empty state depends on the selection, the draft and the list.</summary>
    private void NotifyResultEmptyState()
    {
        OnPropertyChanged(nameof(ResultTitleVisible));
        OnPropertyChanged(nameof(ResultEmptyTitle));
        OnPropertyChanged(nameof(ResultEmptyMessage));
    }

    partial void OnIsBlocksLoadingChanged(bool value) => RefreshBlockListReadState();

    partial void OnBlocksErrorChanged(string value) => RefreshBlockListReadState();

    /// <summary>
    /// The block list's read state changed: the summary line and every empty state that names it
    /// follow, so "reading", "unreachable", "could not read" and "none" never blur into one sentence.
    /// </summary>
    private void RefreshBlockListReadState()
    {
        if (SelectedBridge is { } bridge && !HasBlocks)
        {
            SummaryText = BuildSummary(Cache(bridge.BridgeId));
        }

        OnPropertyChanged(nameof(EmptyBlocksMessage));
        OnPropertyChanged(nameof(ResultEmptyTitle));
        OnPropertyChanged(nameof(ResultEmptyMessage));
    }

    private void ApplyResultState(BridgeCache cache)
    {
        LogicBlockStateDto? blockState = storedBlock_ is { } block && block.Id != Guid.Empty
            ? cache.BlockState(block.Id)
            : null;
        ResultStateWord = blockState is null ? "READING…" : LogicText.StateWord(blockState.State);
        string key = LogicText.StateKey(blockState?.State);
        IsResultOk = key == "ok";
        IsResultBad = key == "bad";
        IsResultWarn = key == "warn";
        IsResultMuted = key == "muted";
        ResultReason = LogicText.DetailReason(blockState);
        IsReasonOk = string.Equals(blockState?.State, LogicBlockStates.Ready, StringComparison.OrdinalIgnoreCase);
        IsReasonBad = !IsReasonOk
            && (string.Equals(blockState?.State, LogicBlockStates.Blocked, StringComparison.OrdinalIgnoreCase)
                || string.Equals(blockState?.State, LogicBlockStates.Unknown, StringComparison.OrdinalIgnoreCase));

        Dictionary<Guid, LogicElementStateDto> elements = blockState is null
            ? new Dictionary<Guid, LogicElementStateDto>()
            : cache.ElementsFor(blockState.Id);
        Dictionary<Guid, LogicStepStateDto> steps = blockState is null
            ? new Dictionary<Guid, LogicStepStateDto>()
            : cache.StepsFor(blockState.Id);

        foreach (LogicResultElementRowViewModel row in ResultElements)
        {
            row.Apply(StateLookup.Lookup(elements, row.ElementId));
        }

        foreach (LogicResultStepRowViewModel stepRow in ResultSteps)
        {
            stepRow.Apply(StateLookup.Lookup(steps, stepRow.StepId));
            foreach (LogicResultElementRowViewModel row in stepRow.Elements)
            {
                row.Apply(StateLookup.Lookup(elements, row.ElementId));
            }
        }
    }

    private string BuildSummary(BridgeCache cache)
    {
        List<LogicBlockDto> blocks = cache.Blocks ?? new List<LogicBlockDto>();
        if (blocks.Count == 0)
        {
            // The summary names what is actually known: reading, a refused read, or none.
            return IsBlocksLoading
                ? "reading the bridge's blocks…"
                : !string.IsNullOrWhiteSpace(BlocksError)
                    ? "blocks could not be read"
                    : "no logic blocks configured";
        }

        int ready = 0;
        int blocked = 0;
        int noData = 0;
        foreach (LogicBlockDto block in blocks)
        {
            string state = cache.BlockState(block.Id)?.State ?? string.Empty;
            if (string.Equals(state, LogicBlockStates.Ready, StringComparison.OrdinalIgnoreCase))
            {
                ready++;
            }
            else if (string.Equals(state, LogicBlockStates.Blocked, StringComparison.OrdinalIgnoreCase))
            {
                blocked++;
            }
            else if (!string.Equals(state, LogicBlockStates.Disabled, StringComparison.OrdinalIgnoreCase))
            {
                noData++;
            }
        }

        string text = $"{ready} ready · {blocked} blocked";
        return noData > 0 ? $"{text} · {noData} no data" : text;
    }

    private LogicBlockRowViewModel? FindBlockRow(Guid id) =>
        AllBlockRows().FirstOrDefault(row => row.BlockId == id);

    /// <summary>The stored block's area's tags, as the bridge holds them (labels, results).</summary>
    private LogicAreaTagsDto? CurrentAreaTags() =>
        SelectedBridge is { } bridge && storedBlock_ is { } block
            ? LogicAreaTags.Find(Cache(bridge.BridgeId).AreaTags, LogicAreaTags.AreaNameFor(block))
            : null;

    private IEnumerable<LogicBlockRowViewModel> AllBlockRows() =>
        BlockAreas.SelectMany(area => area.Groups).SelectMany(group => group.Blocks);

    private bool IsSelected(string bridgeId) =>
        SelectedBridge is { } bridge
        && string.Equals(bridge.BridgeId, bridgeId, StringComparison.OrdinalIgnoreCase);

    private BridgeCache Cache(string bridgeId)
    {
        lock (caches_)
        {
            if (!caches_.TryGetValue(bridgeId, out BridgeCache? cache))
            {
                cache = new BridgeCache();
                caches_[bridgeId] = cache;
            }

            return cache;
        }
    }

    private sealed class BridgeCache
    {
        private LogicStateSnapshot? indexed_;
        private Dictionary<Guid, LogicBlockStateDto> blockStates_ = new();

        public List<LogicBlockDto>? Blocks { get; set; }

        /// <summary>The areas' tags, as GET /api/logic served them.</summary>
        public List<LogicAreaTagsDto> AreaTags { get; set; } = new();

        public long Version { get; set; }

        public LogicStateSnapshot? State { get; set; }

        public void Index()
        {
            LogicStateSnapshot? snapshot = State;
            if (ReferenceEquals(indexed_, snapshot))
            {
                return;
            }

            indexed_ = snapshot;
            blockStates_ = new Dictionary<Guid, LogicBlockStateDto>();
            foreach (LogicBlockStateDto block in snapshot?.Blocks ?? new List<LogicBlockStateDto>())
            {
                blockStates_[block.Id] = block;
            }
        }

        public LogicBlockStateDto? BlockState(Guid blockId) =>
            blockStates_.TryGetValue(blockId, out LogicBlockStateDto? state) ? state : null;

        /// <summary>Every element of the block's network (the steps' pooled in), keyed by element id.</summary>
        public Dictionary<Guid, LogicElementStateDto> ElementsFor(Guid blockId)
        {
            var lookup = new Dictionary<Guid, LogicElementStateDto>();
            LogicBlockStateDto? state = BlockState(blockId);
            foreach (LogicElementStateDto element in state?.Elements ?? new List<LogicElementStateDto>())
            {
                lookup[element.Id] = element;
            }

            return lookup;
        }

        public Dictionary<Guid, LogicStepStateDto> StepsFor(Guid blockId)
        {
            var lookup = new Dictionary<Guid, LogicStepStateDto>();
            LogicBlockStateDto? state = BlockState(blockId);
            foreach (LogicStepStateDto step in state?.Steps ?? new List<LogicStepStateDto>())
            {
                lookup[step.Id] = step;
            }

            return lookup;
        }
    }

    private static class StateLookup
    {
        public static LogicElementStateDto? Lookup(
            Dictionary<Guid, LogicElementStateDto> elements,
            Guid id) =>
            elements.TryGetValue(id, out LogicElementStateDto? state) ? state : null;

        public static LogicStepStateDto? Lookup(
            Dictionary<Guid, LogicStepStateDto> steps,
            Guid id) =>
            steps.TryGetValue(id, out LogicStepStateDto? state) ? state : null;
    }
}
