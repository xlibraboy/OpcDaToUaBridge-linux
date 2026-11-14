using CommunityToolkit.Mvvm.ComponentModel;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// One saved bridge's live session on the phone: its own HTTP client (so its session cookie
/// is its own), its own hub link, its own logic overview and tag entries in the shared cache.
/// The coordinator owns which connection is active; a connection never talks to another bridge.
/// Its methods deliberately await without <c>ConfigureAwait(false)</c>: the pages call them
/// from the UI thread and the results mutate UI-bound state (the overview list, the status
/// text), which Android refuses to touch from a pool thread.
/// </summary>
public sealed partial class BridgeConnection : ObservableObject, IDisposable
{
    private const string LiveText = "connected · live";

    private readonly MobileHubClient hub_ = new();
    private readonly LogicApiClient api_ = new();
    private readonly AccessPointGate? accessPointGate_;
    private readonly MultiBridgeTagCache tags_;
    private readonly IMobileStore store_;
    private readonly Action<Action> post_;
    private readonly SemaphoreSlim connectGate_ = new(1, 1);
    private List<HmiTagDto> tagMetadata_ = new();
    private Dictionary<Guid, List<LogicNoteDto>> notesByBlock_ = new();
    private DateTime? datasetSyncedUtc_;
    private int syncingDepth_;
    private bool flushing_;
    private bool autoReconnecting_;
    private DateTime nextAutoReconnectUtc_;
    private CancellationTokenSource? latencyCts_;

    public BridgeConnection(
        SavedBridge bridge,
        MultiBridgeTagCache tags,
        Action<Action>? post = null,
        IMobileStore? store = null,
        AccessPointGate? accessPointGate = null)
    {
        Bridge = bridge;
        accessPointGate_ = accessPointGate;
        tags_ = tags;
        store_ = store ?? new MemoryMobileStore();
        post_ = post ?? (action => action());
        api_.CanGoOnline = () => OnlineAllowed;
        // Sign-in is the control-user door: it is how an Engineer/Admin provisions or repairs
        // the AP, even when the phone is currently off the bound network. No data is readable
        // until the gate opens, so this cannot become a live-data back door.
        api_.CanAuthenticate = () => true;
        Outbox = new MobileOutbox(store_, Bridge.Id);

        hub_.LogicStateChanged += snapshot => post_(() =>
        {
            Overview.ApplyState(snapshot);
            // A pushed snapshot is the link working: the bridge is reachable again, so an
            // offline grant ends. Without a session the access rule sends the app to Sign in.
            IsOffline = false;
            IsOfflineViewer = false;
            StatusText = LiveText;
            LastSyncedUtc = DateTime.UtcNow;
            RecomputeSync();
            TryFlushOutbox();
            RaiseChanged();
        });
        hub_.ValuesChanged += deltas => post_(() =>
        {
            tags_.ApplyDeltas(CacheKey, deltas);
            IsOffline = false;
            IsOfflineViewer = false;
            LastSyncedUtc = DateTime.UtcNow;
            RecomputeSync();
            TryFlushOutbox();
            // A values batch changes readings, not structure or evaluated state, so the screens
            // that show readings update just the rows behind these tags. Rebuilding every list
            // ten times a second is what made the app hitch between screens.
            TagValuesChanged?.Invoke(deltas);
        });
        hub_.Reconnecting += _ => post_(() =>
        {
            StatusText = "reconnecting…";
            // The link is down now: the values on screen are no longer live, so they go.
            MarkOffline();
            RaiseChanged();
        });
        hub_.Reconnected += connectionId => post_(() =>
        {
            IsOffline = false;
            StatusText = LiveText;
            _ = RefreshAsync(CancellationToken.None);
            RecomputeSync();
            RaiseChanged();
        });
        hub_.Closed += error => post_(() =>
        {
            StatusText = error is null ? "live link closed" : "live link lost: " + error.Message;
            // Closing a connection this bridge has already replaced (every connect builds a new
            // one) is not a lost link; only a close with nothing live left marks the list offline.
            if (!hub_.IsConnected)
            {
                MarkOffline();
            }

            RaiseChanged();
        });

        // The dataset the bridge served last time is on the phone: show it immediately, before
        // the first call decides whether the link is there.
        LoadStored();
        SetOutboxCounts(Outbox.PendingCount, Outbox.FailedCount);
    }

    /// <summary>Raised on the posting thread after any change this bridge caused.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised on the posting thread with the readings one live push changed. Only the rows
    /// behind these tags need repainting; everything else can wait for its own change.
    /// </summary>
    public event Action<IReadOnlyList<HmiValueDelta>>? TagValuesChanged;

    public SavedBridge Bridge { get; }

    /// <summary>True when a gate is installed and the phone is on the bound plant access point.</summary>
    public bool OnlineAllowed => accessPointGate_?.IsOnlineAllowed ?? true;

    public LogicApiClient Api => api_;

    /// <summary>The durable notes/actions queue for this bridge.</summary>
    public MobileOutbox Outbox { get; }

    /// <summary>The outbox as the Settings list shows it.</summary>
    public IReadOnlyList<OutboxItem> OutboxItems => Outbox.All;

    public LogicOverviewViewModel Overview { get; } = new();

    /// <summary>The key this bridge's tags live under in the shared cache.</summary>
    public string CacheKey => MobileBridge.CacheKey(Bridge.Id);

    /// <summary>What the pickers show: the operator's name, else the address.</summary>
    public string DisplayName => Bridge.DisplayName;

    /// <summary>True when this is the bridge the coordinator is showing (one live link at a time).</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private MobileSession? _session;

    [ObservableProperty] private string _statusText = "not connected";

    /// <summary>True once the bridge answered with its logic (signed out still counts).</summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>True when the last failed call was refused for a missing session (HTTP 401/403).</summary>
    [ObservableProperty] private bool _signInRequired;

    /// <summary>
    /// True while the link is down. The stored structure (blocks, areas' tags and notes) stays
    /// on screen, but every live reading and evaluated state has been cleared — nothing is
    /// presented as current.
    /// </summary>
    [ObservableProperty] private bool _isOffline;

    /// <summary>
    /// True while the operator is reading the stored dataset without a bridge account. The
    /// offline view is read-only and lasts only while the bridge is unreachable: a live answer
    /// clears it, and the access rule asks for a sign-in again.
    /// </summary>
    [ObservableProperty] private bool _isOfflineViewer;

    /// <summary>
    /// "offline · structure only · last synced 2026-10-09 12:31:05" — or "offline · no stored
    /// structure yet" when nothing was ever synced from this bridge — the banner.
    /// </summary>
    [ObservableProperty] private string _offlineText = string.Empty;

    /// <summary>The badge's state: synced, syncing, offline, pending or error.</summary>
    [ObservableProperty] private BridgeSyncState _syncState = BridgeSyncState.Offline;

    /// <summary>"synced · 12:31:05" / "offline · 2 pending" — the badge's word.</summary>
    [ObservableProperty] private string _syncText = "not connected";

    /// <summary>State key for the badge's colour ("synced", "syncing", "offline", "pending", "error").</summary>
    [ObservableProperty] private string _syncKey = "offline";

    /// <summary>What the outbox still has to send; updated by the outbox.</summary>
    [ObservableProperty] private int _pendingCount;

    /// <summary>Outbox items the bridge refused; they wait for Retry or Discard in Settings.</summary>
    [ObservableProperty] private int _failedCount;

    /// <summary>When the bridge last answered with data (refresh or live push).</summary>
    [ObservableProperty] private DateTime? _lastSyncedUtc;

    /// <summary>Good/fair/poor/offline classification of the last <c>/health</c> round-trip.</summary>
    [ObservableProperty] private LatencyState _latencyState = LatencyState.Unknown;

    /// <summary>"good · 12 ms" / "fair · 87 ms" / "poor · 210 ms" / "offline" — value included.</summary>
    [ObservableProperty] private string _latencyText = "latency —";

    /// <summary>"12 ms" / "offline" / "—" — the compact value the strips and bridge rows show.</summary>
    public string LatencyValue => BridgeReadingText.LatencyValue(LatencyText);

    /// <summary>"synced 12:31:05" — the strip form without the dot between state and time.</summary>
    public string SyncStamp => BridgeReadingText.SyncStamp(SyncText);

    partial void OnLatencyTextChanged(string value) => OnPropertyChanged(nameof(LatencyValue));

    partial void OnSyncTextChanged(string value) => OnPropertyChanged(nameof(SyncStamp));

    [ObservableProperty] private string _latencyKey = "unknown";

    /// <summary>The measured round-trip in milliseconds; null when no good measurement exists.</summary>
    [ObservableProperty] private int? _latencyMs;

    [ObservableProperty] private DateTime? _latencyMeasuredUtc;

    public bool IsLive => hub_.IsConnected;

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            StartLatencyMonitor();
        }
        else
        {
            StopLatencyMonitor();
        }
    }

    /// <summary>
    /// True when the bridge is away and a synced dataset is stored, so the Sign in screen can
    /// offer the offline view.
    /// </summary>
    public bool CanUseOffline => IsOffline && Overview.Definitions.Count > 0;

    /// <summary>
    /// Opens the stored dataset without a bridge account. It is a read-only view of the
    /// structure — no readings, no evaluated states — and <see cref="MobileAccess"/> ends it
    /// as soon as the bridge answers again.
    /// </summary>
    public void EnterOfflineViewer()
    {
        if (!CanUseOffline)
        {
            return;
        }

        IsOfflineViewer = true;
        RaiseChanged();
    }

    /// <summary>
    /// Takes the session, the logic snapshot and the live link, in that order. Failure is
    /// reported in <see cref="StatusText"/> (the bridge answered but wants a session, the
    /// address is wrong, the link dropped) — never thrown. A failed refresh leaves the cached
    /// list on screen, marked offline.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!OnlineAllowed)
        {
            await SuspendForAccessPointAsync(AccessPointGate.BlockedText);
            return;
        }

        // A bridge switch connects the incoming bridge while the sign-in screen's own appearance
        // may connect the same one; one client means one connect at a time.
        await connectGate_.WaitAsync(cancellationToken);
        BeginSync();
        // An automatic retry must not overwrite the offline banner every half minute; the
        // first attempt (nothing on screen yet) does say what it is doing.
        if (!IsOffline)
        {
            StatusText = "searching for the bridge…";
        }

        RaiseChanged();

        try
        {
            api_.SetBaseAddress(Bridge.Url);

            MobileResult<LogicApiClient.SessionResponse> session = await api_.GetSessionAsync(cancellationToken);
            if (session.Ok && session.Value is not null)
            {
                Session = new MobileSession(
                    session.Value.Authenticated,
                    session.Value.Username,
                    session.Value.DisplayName,
                    session.Value.Role,
                    session.Value.AuthEnabled);
            }

            await RefreshAsync(cancellationToken);
            if (!IsConnected)
            {
                RaiseChanged();
                return;
            }

            await ConnectHubAsync(cancellationToken);
            if (IsActive)
            {
                StartLatencyMonitor();
            }
        }
        finally
        {
            EndSync();
            connectGate_.Release();
        }

        RaiseChanged();
    }

    /// <summary>
    /// Syncs the bridge's dataset: definitions, the areas' tags, tag metadata and the operator
    /// notes are stored in one step, so the phone keeps a complete copy for the offline view.
    /// The evaluated state is fetched only for the live screens — it is never stored.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!OnlineAllowed)
        {
            await SuspendForAccessPointAsync(AccessPointGate.BlockedText);
            return;
        }

        BeginSync();
        try
        {
            MobileResult<MobileLogicDefinitions> definitions = await api_.GetLogicDefinitionsAsync(cancellationToken);
            if (definitions.Ok)
            {
                Overview.ApplyDefinitions(definitions.Value!.Blocks, definitions.Value.TagSources);
                IsConnected = true;
                IsOffline = false;
                // A live answer ends the no-login offline view: online access needs a session.
                IsOfflineViewer = false;
                SignInRequired = false;
                StatusText = IsLive ? LiveText : "connected";
            }
            else
            {
                IsConnected = false;
                SignInRequired = definitions.IsSignInRequired;
                if (definitions.IsSignInRequired)
                {
                    // The bridge refused the session this connection carried: the gate closes and
                    // the login screen owns the next attempt.
                    Session = null;
                }

                StatusText = FailureText(definitions);
                MarkOffline();
            }

            if (definitions.Ok)
            {
                MobileResult<IReadOnlyList<HmiTagDto>> tags = await api_.GetTagsAsync(cancellationToken);
                if (tags.Ok)
                {
                    tagMetadata_ = tags.Value!.ToList();
                    tags_.ReplaceBridge(CacheKey, tagMetadata_);
                }

                MobileResult<IReadOnlyList<LogicNoteDto>> notes = await api_.GetNotesAsync(null, 500, cancellationToken);
                if (notes.Ok)
                {
                    notesByBlock_ = notes.Value!
                        .Where(note => note.BlockId != Guid.Empty)
                        .GroupBy(note => note.BlockId)
                        .ToDictionary(group => group.Key, group => group.ToList());
                    store_.SaveNotes(Bridge.Id, notesByBlock_);
                }

                MobileResult<LogicStateSnapshot> state = await api_.GetLogicStateAsync(cancellationToken);
                if (state.Ok)
                {
                    Overview.ApplyState(state.Value);
                }

                DateTime syncedUtc = DateTime.UtcNow;
                LastSyncedUtc = syncedUtc;
                SaveDataset(syncedUtc);
                TryFlushOutbox();
                if (IsActive)
                {
                    _ = MeasureLatencyOnceAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            EndSync();
        }

        RaiseChanged();
    }

    /// <summary>Notes for one block as last served by the bridge (empty when none were cached).</summary>
    public IReadOnlyList<LogicNoteDto> CachedNotes(Guid blockId) =>
        notesByBlock_.TryGetValue(blockId, out List<LogicNoteDto>? notes) ? notes : Array.Empty<LogicNoteDto>();

    /// <summary>
    /// Reads one block's notes and keeps them for offline reading. A failed call returns the
    /// failure and leaves the cached notes alone, so the detail screen keeps showing the last
    /// notes the bridge served instead of an empty section.
    /// </summary>
    public async Task<MobileResult<IReadOnlyList<LogicNoteDto>>> GetNotesAsync(Guid blockId, CancellationToken cancellationToken)
    {
        MobileResult<IReadOnlyList<LogicNoteDto>> notes = await api_.GetNotesAsync(blockId, 50, cancellationToken);
        if (notes.Ok && notes.Value is not null)
        {
            notesByBlock_[blockId] = notes.Value.ToList();
            store_.SaveNotes(Bridge.Id, notesByBlock_);
        }

        return notes;
    }

    /// <summary>
    /// Adds a note now when the bridge is reachable, otherwise queues it with a client id so a
    /// retry cannot duplicate it. A transport failure is queued with the same id the direct call
    /// used, so a note that did land is returned by the bridge instead of stored twice.
    /// </summary>
    public async Task<OutboxSubmitResult> SubmitNoteAsync(
        Guid blockId,
        string text,
        CancellationToken cancellationToken,
        Guid? conditionId = null)
    {
        if (IsOfflineViewer)
        {
            return new OutboxSubmitResult(
                OutboxSubmitKind.Rejected,
                "the offline view is read-only — sign in when the bridge is back to send notes");
        }

        if (!OnlineAllowed)
        {
            ApplyAccessPointOffline();
        }

        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 500)
        {
            return new OutboxSubmitResult(OutboxSubmitKind.Rejected, "Note text is required (max 500 characters).");
        }

        Guid clientId = Guid.NewGuid();
        if (IsOffline || !IsConnected)
        {
            return EnqueueNote(blockId, trimmed, conditionId, clientId, null);
        }

        MobileResult<LogicNoteDto> result = await api_.AddNoteAsync(
            new LogicNoteAddRequest
            {
                Id = clientId,
                BlockId = blockId,
                ConditionId = conditionId,
                Text = trimmed
            },
            cancellationToken);

        if (result.Ok && result.Value is not null)
        {
            CacheNote(result.Value);
            return new OutboxSubmitResult(OutboxSubmitKind.Sent);
        }

        if (result.StatusCode == 0)
        {
            return EnqueueNote(blockId, trimmed, conditionId, clientId, result.Error);
        }

        if (result.IsSignInRequired)
        {
            SignInRequired = true;
            OutboxSubmitResult queued = EnqueueNote(blockId, trimmed, conditionId, clientId, result.Error);
            return queued.Kind == OutboxSubmitKind.Queued
                ? new OutboxSubmitResult(OutboxSubmitKind.SignInRequired, result.Error)
                : queued;
        }

        return new OutboxSubmitResult(
            OutboxSubmitKind.Rejected,
            result.Error ?? "the bridge rejected the note");
    }

    /// <summary>
    /// Writes an action now when the bridge is reachable. While offline it is queued safely;
    /// if the call fails after it may have been sent, it is queued as <see cref="OutboxItemState.Uncertain"/>
    /// and never replayed without the operator's Retry.
    /// </summary>
    public async Task<OutboxSubmitResult> SubmitActionAsync(
        Guid blockId,
        string? blockName,
        string? actionLabel,
        string? tagLabel,
        string sourceId,
        string itemId,
        string? valueText,
        CancellationToken cancellationToken)
    {
        if (IsOfflineViewer)
        {
            return new OutboxSubmitResult(
                OutboxSubmitKind.Rejected,
                "the offline view is read-only — sign in when the bridge is back to run actions");
        }

        if (!OnlineAllowed)
        {
            ApplyAccessPointOffline();
        }

        if (IsOffline || !IsConnected)
        {
            return EnqueueWrite(
                blockId, blockName, actionLabel, tagLabel, sourceId, itemId, valueText,
                OutboxItemState.Pending, null);
        }

        MobileResult<HmiWriteResponse> result = await api_.WriteAsync(
            new HmiWriteRequest
            {
                SourceId = sourceId,
                ItemId = itemId,
                Value = OutboxValue.Parse(valueText)
            },
            cancellationToken);

        if (result.Ok)
        {
            return new OutboxSubmitResult(OutboxSubmitKind.Sent);
        }

        if (result.StatusCode == 0)
        {
            // The request may have reached the bridge before the link died. Keep it visible but
            // never auto-send it again.
            return EnqueueWrite(
                blockId, blockName, actionLabel, tagLabel, sourceId, itemId, valueText,
                OutboxItemState.Uncertain,
                "the write was not confirmed — retry or discard it in Settings ▸ Sync");
        }

        if (result.IsSignInRequired)
        {
            SignInRequired = true;
            return new OutboxSubmitResult(OutboxSubmitKind.SignInRequired, result.Error);
        }

        return new OutboxSubmitResult(
            OutboxSubmitKind.Rejected,
            result.Error ?? "the bridge rejected the write");
    }

    /// <summary>
    /// Sends the queued notes and safe writes, oldest first. A transport failure or a sign-in
    /// gate stops the pass and leaves the remaining items pending; a bridge rejection marks
    /// that item failed and continues. Only one pass runs at a time.
    /// </summary>
    public async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        if (flushing_)
        {
            return;
        }

        if (!OnlineAllowed)
        {
            await SuspendForAccessPointAsync(AccessPointGate.BlockedText);
            return;
        }

        OutboxItem[] pending = Outbox.All
            .Where(item => item.State == OutboxItemState.Pending)
            .OrderBy(item => item.QueuedUtc)
            .ToArray();
        if (pending.Length == 0)
        {
            SyncOutboxCounts();
            return;
        }

        flushing_ = true;
        BeginSync();
        try
        {
            foreach (OutboxItem item in pending)
            {
                item.State = OutboxItemState.Sending;
                Outbox.Update(item);
                SyncOutboxCounts();

                bool stop = item.Kind == OutboxKind.Note
                    ? !await TrySendQueuedNoteAsync(item, cancellationToken)
                    : !await TrySendQueuedWriteAsync(item, cancellationToken);
                SyncOutboxCounts();
                if (stop)
                {
                    break;
                }
            }
        }
        finally
        {
            flushing_ = false;
            EndSync();
            SyncOutboxCounts();
            RaiseChanged();
        }
    }

    /// <summary>Puts a failed/uncertain item back in the queue and tries the pass again.</summary>
    public async Task RetryOutboxItemAsync(Guid id, CancellationToken cancellationToken)
    {
        Outbox.Retry(id);
        SyncOutboxCounts();
        await FlushOutboxAsync(cancellationToken);
    }

    public void DiscardOutboxItem(Guid id)
    {
        Outbox.Remove(id);
        SyncOutboxCounts();
        RaiseChanged();
    }

    private OutboxSubmitResult EnqueueNote(
        Guid blockId,
        string text,
        Guid? conditionId,
        Guid clientId,
        string? error)
    {
        OutboxItem item = new()
        {
            Id = clientId,
            Kind = OutboxKind.Note,
            BlockId = blockId,
            BlockName = FindBlockName(blockId),
            ConditionId = conditionId,
            NoteText = text,
            QueuedUtc = DateTime.UtcNow,
            Error = error
        };

        if (!Outbox.TryAdd(item, out string? queueError))
        {
            return new OutboxSubmitResult(OutboxSubmitKind.Rejected, queueError);
        }

        SyncOutboxCounts();
        RaiseChanged();
        return new OutboxSubmitResult(OutboxSubmitKind.Queued, error);
    }

    private OutboxSubmitResult EnqueueWrite(
        Guid blockId,
        string? blockName,
        string? actionLabel,
        string? tagLabel,
        string sourceId,
        string itemId,
        string? valueText,
        OutboxItemState state,
        string? error)
    {
        OutboxItem item = new()
        {
            Kind = OutboxKind.Write,
            BlockId = blockId,
            BlockName = blockName ?? string.Empty,
            ActionLabel = actionLabel,
            TagLabel = tagLabel,
            SourceId = sourceId,
            ItemId = itemId,
            ValueText = valueText,
            QueuedUtc = DateTime.UtcNow,
            Error = error
        };

        if (!Outbox.TryAdd(item, out string? queueError, state))
        {
            return new OutboxSubmitResult(OutboxSubmitKind.Rejected, queueError);
        }

        SyncOutboxCounts();
        RaiseChanged();
        return state == OutboxItemState.Uncertain
            ? new OutboxSubmitResult(OutboxSubmitKind.Uncertain, error)
            : new OutboxSubmitResult(OutboxSubmitKind.Queued);
    }

    private async Task<bool> TrySendQueuedNoteAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        MobileResult<LogicNoteDto> result = await api_.AddNoteAsync(
            new LogicNoteAddRequest
            {
                Id = item.Id,
                BlockId = item.BlockId,
                ConditionId = item.ConditionId,
                Text = item.NoteText ?? string.Empty
            },
            cancellationToken);

        if (result.Ok && result.Value is not null)
        {
            CacheNote(result.Value);
            Outbox.Remove(item.Id);
            return true;
        }

        if (result.StatusCode == 0)
        {
            item.State = OutboxItemState.Pending;
            item.Error = result.Error;
            Outbox.Update(item);
            return false;
        }

        if (result.IsSignInRequired)
        {
            item.State = OutboxItemState.Pending;
            item.Error = result.Error;
            Outbox.Update(item);
            SignInRequired = true;
            return false;
        }

        item.State = OutboxItemState.Failed;
        item.Error = result.Error ?? "the bridge rejected the note";
        Outbox.Update(item);
        return true;
    }

    private async Task<bool> TrySendQueuedWriteAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - item.QueuedUtc > TimeSpan.FromMinutes(2))
        {
            item.State = OutboxItemState.Failed;
            item.Error = "stale — older than 2 minutes, not sent";
            Outbox.Update(item);
            return true;
        }

        MobileResult<HmiWriteResponse> result = await api_.WriteAsync(
            new HmiWriteRequest
            {
                SourceId = item.SourceId ?? string.Empty,
                ItemId = item.ItemId ?? string.Empty,
                Value = OutboxValue.Parse(item.ValueText)
            },
            cancellationToken);

        if (result.Ok)
        {
            Outbox.Remove(item.Id);
            return true;
        }

        if (result.StatusCode == 0)
        {
            item.State = OutboxItemState.Pending;
            item.Error = result.Error;
            Outbox.Update(item);
            return false;
        }

        if (result.IsSignInRequired)
        {
            item.State = OutboxItemState.Pending;
            item.Error = result.Error;
            Outbox.Update(item);
            SignInRequired = true;
            return false;
        }

        item.State = OutboxItemState.Failed;
        item.Error = result.Error ?? "the bridge rejected the write";
        Outbox.Update(item);
        return true;
    }

    private void CacheNote(LogicNoteDto note)
    {
        if (!notesByBlock_.TryGetValue(note.BlockId, out List<LogicNoteDto>? notes))
        {
            notes = new List<LogicNoteDto>();
            notesByBlock_[note.BlockId] = notes;
        }

        if (!notes.Any(candidate => candidate.Id == note.Id))
        {
            notes.Insert(0, note);
        }

        store_.SaveNotes(Bridge.Id, notesByBlock_);
    }

    private string FindBlockName(Guid blockId) =>
        Overview.Definitions.FirstOrDefault(block => block.Id == blockId)?.Name ?? string.Empty;

    private void SyncOutboxCounts() =>
        SetOutboxCounts(Outbox.PendingCount, Outbox.FailedCount);

    /// <summary>
    /// Pings the bridge's public /health every ten seconds while this connection is the one
    /// on screen. The loop runs off the UI thread and posts the measured value back.
    /// </summary>
    private void StartLatencyMonitor()
    {
        StopLatencyMonitor();
        if (!IsActive)
        {
            return;
        }

        latencyCts_ = new CancellationTokenSource();
        CancellationToken token = latencyCts_.Token;
        _ = Task.Run(() => LatencyLoopAsync(token), token);
    }

    private void StopLatencyMonitor()
    {
        CancellationTokenSource? cts = latencyCts_;
        latencyCts_ = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }
    }

    private async Task LatencyLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (OnlineAllowed && IsConnected && !IsOffline)
                {
                    await MeasureLatencyOnceAsync(token).ConfigureAwait(false);
                }
                else
                {
                    post_(() =>
                    {
                        ApplyLatencyUnavailable(LatencyState.Offline, "offline");
                        // While the bridge is away, ask it again: the first answer that lands
                        // ends the offline view, and the app requires a sign-in for live data.
                        TryAutoReconnect();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // A ping failure is reported as an unknown measurement, never as a crash.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task MeasureLatencyOnceAsync(CancellationToken token)
    {
        MobileResult<int> result = await api_.MeasureLatencyAsync(token).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (result.Ok)
        {
            int milliseconds = result.Value;
            LatencyState state = LatencyGrade.Classify(milliseconds);
            post_(() => ApplyLatency(state, milliseconds));
        }
        else
        {
            post_(() => ApplyLatencyUnavailable(LatencyState.Unknown, "latency —"));
        }
    }

    private void ApplyLatency(LatencyState state, int milliseconds)
    {
        LatencyState = state;
        LatencyKey = LatencyGrade.Key(state);
        LatencyMs = milliseconds;
        LatencyMeasuredUtc = DateTime.UtcNow;
        LatencyText = LatencyGrade.Text(state, milliseconds);
        RaiseChanged();
    }

    private void ApplyLatencyUnavailable(LatencyState state, string text)
    {
        bool changed = LatencyState != state || !string.Equals(LatencyText, text, StringComparison.Ordinal);
        LatencyState = state;
        LatencyKey = LatencyGrade.Key(state);
        LatencyMs = null;
        LatencyMeasuredUtc = null;
        LatencyText = text;
        if (changed)
        {
            RaiseChanged();
        }
    }

    private void TryFlushOutbox()
    {
        if (!flushing_ && Outbox.PendingCount > 0)
        {
            _ = FlushOutboxAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Retries a lost link at most every half minute while this connection is the one on
    /// screen, so a phone that walks back into the plant Wi-Fi reconnects on its own. Runs on
    /// the UI thread (posted by the monitor) because connecting mutates UI-bound state.
    /// </summary>
    private void TryAutoReconnect()
    {
        if (!OnlineAllowed || !IsActive || autoReconnecting_ || !IsOffline || IsConnected)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now < nextAutoReconnectUtc_)
        {
            return;
        }

        nextAutoReconnectUtc_ = now.AddSeconds(30);
        autoReconnecting_ = true;
        _ = ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await ConnectAsync(CancellationToken.None);
        }
        catch
        {
            // A retry that fails leaves the offline banner in place; the next tick tries again.
        }
        finally
        {
            autoReconnecting_ = false;
        }
    }

    /// <summary>
    /// Shows the dataset this bridge served last time — the blocks, the areas' tags, the tag
    /// metadata and the notes — so the list is never blank while the link is being acquired
    /// (or after it is gone). Readings and evaluated states are never restored: the offline
    /// view is structure only. Leaves <see cref="IsOffline"/> to a failed call — stored data
    /// that is about to be refreshed is not "offline" yet.
    /// </summary>
    public void LoadStored()
    {
        BridgeDataset? dataset = store_.LoadDataset(Bridge.Id);
        if (dataset is null)
        {
            return;
        }

        Overview.ApplyDefinitions(dataset.Blocks, dataset.TagSources);
        tagMetadata_ = dataset.Tags.ToList();
        if (tagMetadata_.Count > 0)
        {
            // The entries keep their names, units and on/off texts — the labels on the screen —
            // while their readings are cleared from the live cache.
            tags_.ReplaceBridge(CacheKey, tagMetadata_);
            tags_.ClearValues(CacheKey);
        }

        Overview.ClearLiveState();
        notesByBlock_ = new Dictionary<Guid, List<LogicNoteDto>>(store_.LoadNotes(Bridge.Id));
        datasetSyncedUtc_ = dataset.SyncedUtc;
        LastSyncedUtc = dataset.SyncedUtc;
        RecomputeSync();
    }

    /// <summary>
    /// Replaces this bridge's stored dataset after a sync. A dataset save is one transaction,
    /// so the previous copy stays readable if this one cannot be written.
    /// </summary>
    private void SaveDataset(DateTime syncedUtc)
    {
        // A sync that leaves the plant with no blocks still replaces the stored copy; keeping
        // the previous blocks would show structure the bridge no longer has.
        store_.SaveDataset(
            Bridge.Id,
            new BridgeDataset(
                syncedUtc,
                Overview.Definitions.ToList(),
                Overview.AreaTags.ToList(),
                tagMetadata_.ToList()));
        datasetSyncedUtc_ = syncedUtc;
    }

    /// <summary>
    /// Marks the link as gone and drops everything the bridge had been reporting: every tag
    /// reading and the evaluated state become no data, so the screen cannot freeze on the last
    /// values the plant happened to show. Only the stored structure survives, and the banner
    /// says how old it is.
    /// </summary>
    private void MarkOffline()
    {
        tags_.ClearValues(CacheKey);
        Overview.ClearLiveState();
        IsOffline = true;
        OfflineText = datasetSyncedUtc_ is { } savedUtc
            ? "offline · structure only · last synced " + savedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "offline · no stored structure yet";
        ApplyLatencyUnavailable(LatencyState.Offline, "offline");
        RecomputeSync();
    }

    public async Task<MobileResult<MobileSession>> SignInAsync(string username, string password, CancellationToken cancellationToken)
    {
        // Sign-in is allowed even while the online gate is shut: the account's role is what
        // decides whether the AP page may provision, and no live data is read here.
        api_.SetBaseAddress(Bridge.Url);
        MobileResult<LogicApiClient.SessionResponse> result = await api_.LoginAsync(username, password, cancellationToken);
        if (!result.Ok || result.Value is null)
        {
            return MobileResult<MobileSession>.Fail(result.Error ?? "sign-in failed", result.StatusCode);
        }

        Session = new MobileSession(
            result.Value.Authenticated,
            result.Value.Username,
            result.Value.DisplayName,
            result.Value.Role,
            result.Value.AuthEnabled);

        await RefreshAsync(cancellationToken);
        if (IsConnected)
        {
            // Re-link so the hub carries the session cookie on a bridge that gates it.
            await ConnectHubAsync(cancellationToken);
            await FlushOutboxAsync(cancellationToken);
        }

        RaiseChanged();
        return MobileResult<MobileSession>.Success(Session);
    }

    /// <summary>
    /// Ends this bridge's session and locks the app again. The logout call is best-effort — an
    /// unreachable bridge must not keep the phone signed in — but the local session and every
    /// reading go either way, so the next screen is the login page.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        _ = await api_.LogoutAsync(cancellationToken);
        Session = null;
        SignInRequired = false;
        await DisconnectAsync();
        IsConnected = false;
        StatusText = "not connected";
        RecomputeSync();
        RaiseChanged();
    }

    /// <summary>
    /// Ends the live link because the phone left the bound access point. The stored structure
    /// stays readable, but every reading and evaluated state goes and the banner says why.
    /// </summary>
    public async Task SuspendForAccessPointAsync(string reason)
    {
        await DisconnectAsync();
        ApplyAccessPointOffline(reason);
    }

    /// <summary>Marks the bridge offline without opening a socket on a non-bound network.</summary>
    private void ApplyAccessPointOffline(string reason = AccessPointGate.BlockedText)
    {
        MarkOffline();
        StatusText = reason;
        OfflineText = datasetSyncedUtc_ is { } savedUtc
            ? "offline · " + reason + " · last synced " + savedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "offline · " + reason;
        RecomputeSync();
        RaiseChanged();
    }

    /// <summary>Closes the live link (the coordinator keeps one link open across bridges).</summary>
    public async Task DisconnectAsync()
    {
        // Closing on purpose leaves the same rule in place as losing the link: nothing this
        // bridge reported stays on screen as a current reading. The stored dataset is what
        // keeps the structure readable.
        tags_.ClearValues(CacheKey);
        Overview.ClearLiveState();
        StopLatencyMonitor();
        ApplyLatencyUnavailable(LatencyState.Offline, "offline");
        await hub_.DisposeAsync();
        RecomputeSync();
        RaiseChanged();
    }

    public void Dispose()
    {
        StopLatencyMonitor();
        hub_.DisposeAsync().AsTask().GetAwaiter().GetResult();
        api_.Dispose();
    }

    private async Task ConnectHubAsync(CancellationToken cancellationToken)
    {
        try
        {
            await hub_.ConnectAsync(Bridge.Url, cancellationToken, null, api_.Cookies, () => OnlineAllowed);
            StatusText = LiveText;
            // Reaching the bridge's hub means the link is back. Clear the offline view here
            // as well as in RefreshAsync: a reconnect can follow a state that was marked
            // offline without a new refresh.
            IsOffline = false;
            IsOfflineViewer = false;
        }
        catch (Exception exception)
        {
            StatusText = "connected (live link failed: " + exception.Message + ")";
        }

        RecomputeSync();
    }

    /// <summary>Marks a read/flush as in progress; nested calls share one depth count.</summary>
    private void BeginSync()
    {
        syncingDepth_++;
        RecomputeSync();
    }

    private void EndSync()
    {
        if (syncingDepth_ > 0)
        {
            syncingDepth_--;
        }

        RecomputeSync();
    }

    /// <summary>Called by the outbox after every enqueue, flush or failed item.</summary>
    public void SetOutboxCounts(int pending, int failed)
    {
        PendingCount = pending;
        FailedCount = failed;
        RecomputeSync();
    }

    /// <summary>
    /// The one place the badge's state is derived. A live push must not paint "synced" while
    /// the outbox still has work, and an offline link with pending items says both.
    /// </summary>
    private void RecomputeSync()
    {
        if (IsOffline)
        {
            SyncState = BridgeSyncState.Offline;
            SyncKey = "offline";
            SyncText = datasetSyncedUtc_ is { } saved
                ? "offline · last synced " + saved.ToLocalTime().ToString("HH:mm:ss")
                : "offline";
            if (PendingCount > 0)
            {
                SyncText += " · " + PendingCount + " pending";
            }

            if (FailedCount > 0)
            {
                SyncText += " · " + FailedCount + " failed";
            }

            return;
        }

        if (syncingDepth_ > 0)
        {
            SyncState = BridgeSyncState.Syncing;
            SyncKey = "syncing";
            SyncText = PendingCount > 0 ? "syncing… · " + PendingCount + " pending" : "syncing…";
            return;
        }

        if (FailedCount > 0)
        {
            SyncState = BridgeSyncState.Error;
            SyncKey = "error";
            SyncText = FailedCount + " failed"
                + (PendingCount > 0 ? " · " + PendingCount + " pending" : string.Empty);
            return;
        }

        if (PendingCount > 0)
        {
            SyncState = BridgeSyncState.Pending;
            SyncKey = "pending";
            SyncText = PendingCount + " pending";
            return;
        }

        if (IsConnected)
        {
            SyncState = BridgeSyncState.Synced;
            SyncKey = "synced";
            SyncText = LastSyncedUtc is { } at
                ? "synced · " + at.ToLocalTime().ToString("HH:mm:ss")
                : "synced";
            return;
        }

        SyncState = BridgeSyncState.Offline;
        SyncKey = "offline";
        SyncText = "not connected";
    }

    private string FailureText(MobileResult<MobileLogicDefinitions> definitions)
    {
        if (definitions.IsSignInRequired)
        {
            return "the bridge requires sign-in";
        }

        return definitions.StatusCode == 0
            ? "no bridge answered on " + Bridge.Url
            : "the bridge answered with HTTP " + definitions.StatusCode;
    }

    private void RaiseChanged() => Changed?.Invoke();
}
