using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
        pollTimer_ = new System.Threading.Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Set by the page: asks the operator to confirm a destructive step. Null = proceed.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

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
        OnPropertyChanged(nameof(IsEditingEnabled));
        OnPropertyChanged(nameof(BridgeStatusText));
        OnPropertyChanged(nameof(EditingHint));
        OnPropertyChanged(nameof(IsBridgeOk));
        OnPropertyChanged(nameof(IsBridgeWarn));
        OnPropertyChanged(nameof(IsBridgeBad));
        NotifyCommands();
        if (!suppressBridgeLoad_)
        {
            _ = LoadSelectedBridgeAsync();
        }
    }

    // ---- Editor / Result tabs ----

    [ObservableProperty]
    private bool _isEditorTab = true;

    [ObservableProperty]
    private bool _isResultTab;

    partial void OnIsEditorTabChanged(bool value)
    {
        if (value && IsResultTab)
        {
            IsResultTab = false;
        }
    }

    partial void OnIsResultTabChanged(bool value)
    {
        if (value && IsEditorTab)
        {
            IsEditorTab = false;
        }
    }

    [ObservableProperty]
    private string _editorMessage = string.Empty;

    // ---- Block list ----

    public ObservableCollection<LogicGroupViewModel> BlockGroups { get; } = new();

    [ObservableProperty]
    private bool _hasBlocks;

    [ObservableProperty]
    private string _summaryText = "no logic blocks configured";

    [ObservableProperty]
    private LogicBlockRowViewModel? _selectedBlock;

    [ObservableProperty]
    private bool _hasSelectedBlock;

    public string EmptyBlocksMessage => "No logic blocks yet — use New block to author the first one.";

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
    private string _blockDescription = string.Empty;

    [ObservableProperty]
    private string _blockTags = string.Empty;

    public ObservableCollection<LogicConditionRowViewModel> Conditions { get; } = new();

    public ObservableCollection<LogicElementRowViewModel> Elements { get; } = new();

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

    partial void OnIsDirtyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnSelectedKindChanged(LogicSelectOption? value)
    {
        RefreshEditorMode();
        DirtyChanged();
    }

    partial void OnBlockNameChanged(string value) => DirtyChanged();

    partial void OnBlockEnabledChanged(bool value) => DirtyChanged();

    partial void OnBlockGroupChanged(string value) => DirtyChanged();

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

    public string ResultEmptyMessage => storedBlock_ is null || storedBlock_.Id == Guid.Empty
        ? "Save the block to see its evaluated network."
        : "No evaluated elements yet — waiting for the bridge.";

    public bool HasResultDescription => ResultDescription.Length > 0;

    public bool HasResultReason => ResultReason.Length > 0;

    partial void OnResultDescriptionChanged(string value) => OnPropertyChanged(nameof(HasResultDescription));

    partial void OnResultReasonChanged(string value) => OnPropertyChanged(nameof(HasResultReason));

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewBlock()
    {
        SelectedBlock = null;
        editingNewBlock_ = true;
        LoadDraft(LogicEditorCodec.NewDraftBlock(CurrentBlockCount), null);
        EditorMessage = string.Empty;
    }

    /// <summary>Selects a listed block from the Result tab's list (and the Blocks window).</summary>
    [RelayCommand]
    private void SelectBlock(LogicBlockRowViewModel? row)
    {
        if (row is not null)
        {
            SelectedBlock = row;
        }
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

        LogicBlockDto block = BuildDraft();
        EditorMessage = "Saving…";
        HmiLogicSaveResult result = await session.Api.SaveLogicBlockAsync(block, CancellationToken.None)
            .ConfigureAwait(true);
        if (!result.Ok)
        {
            EditorMessage = "✗ " + (result.Error ?? "Save failed.");
            report_?.Invoke($"Logic save failed: {result.Error}");
            return;
        }

        pendingSelectId_ = result.Block?.Id;
        await ReloadDefinitionsAsync(bridge.BridgeId).ConfigureAwait(true);
        await RefreshStateAsync().ConfigureAwait(true);
        EditorMessage = "✓ Saved.";
        report_?.Invoke($"Logic block '{block.Name}' saved");
    }

    private bool CanSave() => IsEditingEnabled && IsDirty;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteSelected() => DeleteBlockAsync(SelectedBlock ?? null);

    private bool CanDelete() => IsEditingEnabled && storedBlock_ is not null;

    /// <summary>Deletes one listed block (also the Blocks window's per-row delete).</summary>
    [RelayCommand]
    private Task DeleteBlock(LogicBlockRowViewModel? row) => DeleteBlockAsync(row);

    private bool CanEdit() => IsEditingEnabled;

    [RelayCommand]
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

    [RelayCommand]
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

    [RelayCommand]
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

    [RelayCommand]
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

        applyingState_ = true;
        try
        {
            ApplyBlockStates(cache);
            SummaryText = BuildSummary(cache);
            ApplyEditorLive(cache);
            ApplyResultState(cache);
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
            _ = RefreshStateAsync();
        }
        else
        {
            pollTimer_.Change(Timeout.Infinite, Timeout.Infinite);
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
            OnPropertyChanged(nameof(IsEditingEnabled));
            OnPropertyChanged(nameof(BridgeStatusText));
            OnPropertyChanged(nameof(EditingHint));
            OnPropertyChanged(nameof(IsBridgeOk));
            OnPropertyChanged(nameof(IsBridgeWarn));
            OnPropertyChanged(nameof(IsBridgeBad));
            NotifyCommands();
        }
    }

    private async Task FetchDefinitionsAsync(string bridgeId, BridgeCache cache)
    {
        if (!connections_.TryGetSession(bridgeId, out BridgeConnectionManager.BridgeSession? session)
            || session is null)
        {
            cache.Blocks ??= new List<LogicBlockDto>();
            ApplyLoadedBlocks(bridgeId, cache);
            return;
        }

        try
        {
            HmiLogicDefinitions definitions = await session.Api.GetLogicDefinitionsAsync(CancellationToken.None)
                .ConfigureAwait(true);
            cache.Blocks = definitions.Blocks.ToList();
            cache.Version = definitions.Version;
        }
        catch (Exception ex)
        {
            cache.Blocks ??= new List<LogicBlockDto>();
            report_?.Invoke("Logic definitions: " + ex.Message);
        }

        ApplyLoadedBlocks(bridgeId, cache);
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
        RestoreSelection();
        SummaryText = BuildSummary(cache);
        ApplyBlockStates(cache);
        ApplyEditorLive(cache);
        ApplyResultState(cache);
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

        bool anyGrouped = rows.Any(row => row.Group.Length > 0);
        List<LogicGroupViewModel> groups = anyGrouped
            ? rows
                .GroupBy(row => row.Group, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key.Length == 0 ? 0 : 1)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new LogicGroupViewModel(
                    group.Key.Length == 0 ? "Ungrouped" : group.Key,
                    showHeading: true,
                    group.OrderBy(row => row.Block.Order).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)))
                .ToList()
            : new List<LogicGroupViewModel> { new(string.Empty, showHeading: false, rows) };

        BlockGroups.Clear();
        foreach (LogicGroupViewModel group in groups)
        {
            BlockGroups.Add(group);
        }

        HasBlocks = rows.Count > 0;
        foreach (LogicGroupViewModel group in BlockGroups)
        {
            group.UpdateSummary();
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
            Steps.Clear();
            Actions.Clear();
            BlockName = string.Empty;
            BlockGroup = string.Empty;
            BlockDescription = string.Empty;
            BlockTags = string.Empty;
            BlockEnabled = true;
            SelectedKind = KindOptions[0];
        }
        finally
        {
            buildingRows_ = false;
        }

        BlockGroups.Clear();
        HasBlocks = false;
        SummaryText = "no logic blocks configured";
        SelectedBlock = null;
        ResultElements.Clear();
        ResultSteps.Clear();
        UpdateResultDetail();
        RefreshEditorMode();
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
            BlockName = block.Name;
            SelectedKind = LogicSelectOption.Find(KindOptions, block.Kind) ?? KindOptions[0];
            BlockEnabled = block.Enabled;
            BlockGroup = block.Group;
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
        var row = new LogicConditionRowViewModel(condition, catalog_, RemoveCondition);
        row.PropertyChanged += OnEditorRowChanged;
        Conditions.Add(row);
    }

    private void AddElementRow(LogicElementDto element, int indent)
    {
        var row = new LogicElementRowViewModel(element, indent, catalog_, RemoveElement);
        row.PropertyChanged += OnEditorRowChanged;
        Elements.Add(row);
    }

    private void AddStepRow(LogicStepDto step)
    {
        var row = new LogicStepRowViewModel(step, catalog_, RemoveStep, MoveStepUp, MoveStepDown);
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
        var row = new LogicActionRowViewModel(action, catalog_, RemoveAction);
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

    private void MoveStepUp(LogicStepRowViewModel row)
    {
        int index = Steps.IndexOf(row);
        if (index > 0)
        {
            Steps.Move(index, index - 1);
            RenumberSteps();
            DirtyChanged();
        }
    }

    private void MoveStepDown(LogicStepRowViewModel row)
    {
        int index = Steps.IndexOf(row);
        if (index >= 0 && index < Steps.Count - 1)
        {
            Steps.Move(index, index + 1);
            RenumberSteps();
            DirtyChanged();
        }
    }

    private void RemoveAction(LogicActionRowViewModel row)
    {
        Actions.Remove(row);
        DirtyChanged();
        RefreshEditorMode();
    }

    private void RenumberSteps()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].StepLabel = $"STEP {i + 1}";
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

        DirtyChanged();
    }

    private void DirtyChanged()
    {
        if (buildingRows_)
        {
            return;
        }

        IsDirty = ComputeDirty();
    }

    private bool ComputeDirty()
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
        ConvertToNetworkCommand.NotifyCanExecuteChanged();
        BackToConditionsCommand.NotifyCanExecuteChanged();
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
        NewBlockCommand.NotifyCanExecuteChanged();
        ConvertToNetworkCommand.NotifyCanExecuteChanged();
        BackToConditionsCommand.NotifyCanExecuteChanged();
    }

    private void ApplyBlockStates(BridgeCache cache)
    {
        foreach (LogicGroupViewModel group in BlockGroups)
        {
            foreach (LogicBlockRowViewModel row in group.Blocks)
            {
                row.ApplyState(cache.BlockState(row.BlockId));
            }

            group.UpdateSummary();
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

        ResultTitle = block is null || block.Id == Guid.Empty
            ? "New block — not saved yet"
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
            OnPropertyChanged(nameof(ResultEmptyMessage));
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
                    stepRow.Elements.Add(new LogicResultElementRowViewModel(element, depth, catalog_));
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
                ResultElements.Add(new LogicResultElementRowViewModel(element, depth, catalog_));
            }

            HasResultRows = ResultElements.Count > 0;
        }

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
            return "no logic blocks configured";
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

    private IEnumerable<LogicBlockRowViewModel> AllBlockRows() =>
        BlockGroups.SelectMany(group => group.Blocks);

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
