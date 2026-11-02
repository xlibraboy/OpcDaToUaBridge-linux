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
    private readonly MultiBridgeTagCache tags_;
    private readonly IBridgeCacheStore cache_;
    private readonly Action<Action> post_;
    private readonly SemaphoreSlim connectGate_ = new(1, 1);
    private List<HmiTagDto> lastKnownTags_ = new();
    private LogicStateSnapshot? lastKnownState_;
    private Dictionary<Guid, List<LogicNoteDto>> notesByBlock_ = new();
    private DateTime? lastCachedUtc_;
    private DateTime lastKnownPersistUtc_;
    private int syncingDepth_;
    private bool flushing_;
    private CancellationTokenSource? latencyCts_;

    public BridgeConnection(SavedBridge bridge, MultiBridgeTagCache tags, Action<Action>? post = null, IBridgeCacheStore? cache = null)
    {
        Bridge = bridge;
        tags_ = tags;
        cache_ = cache ?? new MemoryBridgeCacheStore();
        post_ = post ?? (action => action());
        Outbox = new MobileOutbox(cache_, MobileBridge.OutboxKey(Bridge.Id));

        hub_.LogicStateChanged += snapshot => post_(() =>
        {
            Overview.ApplyState(snapshot);
            // The pushed snapshot is also the persistent screen's latest data; keep it even
            // though the live list would clear if the link dropped a moment later.
            lastKnownState_ = snapshot;
            // A pushed snapshot is the link working: whatever was cached is live again.
            IsOffline = false;
            StatusText = LiveText;
            LastSyncedUtc = DateTime.UtcNow;
            PersistLastKnown(force: false);
            RecomputeSync();
            TryFlushOutbox();
            RaiseChanged();
        });
        hub_.ValuesChanged += deltas => post_(() =>
        {
            tags_.ApplyDeltas(CacheKey, deltas);
            ApplyDeltasToLastKnown(deltas);
            IsOffline = false;
            LastSyncedUtc = DateTime.UtcNow;
            PersistLastKnown(force: false);
            RecomputeSync();
            TryFlushOutbox();
            RaiseChanged();
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

        // The list the bridge served last time is on disk: show it immediately, before the
        // first call decides whether the link is there.
        LoadCached();
        SetOutboxCounts(Outbox.PendingCount, Outbox.FailedCount);
    }

    /// <summary>Raised on the posting thread after any change this bridge caused.</summary>
    public event Action? Changed;

    public SavedBridge Bridge { get; }

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
    /// True while the link is down. The block definitions and notes stay on screen, but every
    /// live reading and evaluated state has been cleared — nothing is presented as current.
    /// </summary>
    [ObservableProperty] private bool _isOffline;

    /// <summary>"offline · live values hidden · last synced 2026-10-09 12:31:05" — the banner.</summary>
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
    /// The last readings this bridge served, kept after the link drops. The live screens do
    /// not show them; the Elements screen does, with <see cref="BridgeLastKnown.SavedUtc"/>.
    /// </summary>
    public BridgeLastKnown? LastKnown =>
        lastKnownState_ is null && lastKnownTags_.Count == 0
            ? null
            : new BridgeLastKnown(
                lastCachedUtc_ ?? DateTime.MinValue,
                Overview.Definitions,
                Overview.AreaTags,
                lastKnownState_,
                lastKnownTags_);

    /// <summary>
    /// Takes the session, the logic snapshot and the live link, in that order. Failure is
    /// reported in <see cref="StatusText"/> (the bridge answered but wants a session, the
    /// address is wrong, the link dropped) — never thrown. A failed refresh leaves the cached
    /// list on screen, marked offline.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // A bridge switch connects the incoming bridge while the sign-in screen's own appearance
        // may connect the same one; one client means one connect at a time.
        await connectGate_.WaitAsync(cancellationToken);
        BeginSync();
        StatusText = "searching for the bridge…";
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

    /// <summary>Snapshot refresh: definitions, tags and evaluated state.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        BeginSync();
        try
        {
            MobileResult<MobileLogicDefinitions> definitions = await api_.GetLogicDefinitionsAsync(cancellationToken);
            if (definitions.Ok)
            {
                Overview.ApplyDefinitions(definitions.Value!.Blocks, definitions.Value.TagSources);
                IsConnected = true;
                IsOffline = false;
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
                    lastKnownTags_ = tags.Value!.ToList();
                    tags_.ReplaceBridge(CacheKey, lastKnownTags_);
                }

                MobileResult<LogicStateSnapshot> state = await api_.GetLogicStateAsync(cancellationToken);
                if (state.Ok)
                {
                    Overview.ApplyState(state.Value);
                    lastKnownState_ = state.Value;
                }

                LastSyncedUtc = DateTime.UtcNow;
                PersistLastKnown(force: true);
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
            PersistNotes();
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

        PersistNotes();
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
                if (IsConnected && !IsOffline)
                {
                    await MeasureLatencyOnceAsync(token).ConfigureAwait(false);
                }
                else
                {
                    post_(() => ApplyLatencyUnavailable(LatencyState.Offline, "offline"));
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
    /// Shows the structure this bridge served last time — the definitions, tag metadata and
    /// notes — so the list is never blank while the link is being acquired (or after it is
    /// gone). The cached readings and evaluated state are deliberately not applied: live
    /// values come only from the bridge. Leaves <see cref="IsOffline"/> to a failed call — a
    /// cached list that is about to be refreshed is not "offline" yet.
    /// </summary>
    public void LoadCached()
    {
        BridgeCacheSnapshot? snapshot = BridgeCacheCodec.Deserialize(cache_.Read(BridgeCacheCodec.Key(Bridge.Id)));
        if (snapshot is null)
        {
            return;
        }

        Overview.ApplyDefinitions(snapshot.Blocks, snapshot.TagSources);
        // The last readings stay available to the Elements screen, but they are not applied to
        // the live view or the shared tag cache: a cached value must never read as current.
        lastKnownState_ = snapshot.State;
        lastKnownTags_ = snapshot.Tags is { Count: > 0 } tags ? tags.ToList() : new List<HmiTagDto>();
        if (lastKnownTags_.Count > 0)
        {
            // The entries keep their names, units and on/off texts — the labels on the screen —
            // while their readings are cleared from the live cache.
            tags_.ReplaceBridge(CacheKey, lastKnownTags_);
            tags_.ClearValues(CacheKey);
        }

        // Definitions and notes are the cache's offline value; the last evaluation is not
        // shown, because the phone is not connected yet.
        Overview.ClearLiveState();
        notesByBlock_ = snapshot.NotesByBlock is { } notes
            ? notes
            : new Dictionary<Guid, List<LogicNoteDto>>();
        lastCachedUtc_ = snapshot.SavedUtc;
        LastSyncedUtc = snapshot.SavedUtc;
        RecomputeSync();
    }

    /// <summary>
    /// Writes the last-known snapshot, at most once every five seconds unless forced. A live
    /// push can change state or values many times a second; the file must not be rewritten for
    /// each one.
    /// </summary>
    private void PersistLastKnown(bool force)
    {
        if (Overview.Definitions.Count == 0)
        {
            // Nothing to anchor the snapshot to — and an empty block list reads as no cache.
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (!force && now - lastKnownPersistUtc_ < TimeSpan.FromSeconds(5))
        {
            return;
        }

        lastKnownPersistUtc_ = now;
        WriteSnapshot(now);
    }

    /// <summary>
    /// Writes the snapshot after a notes read. The banner's data age (the last refresh's time)
    /// stays put, so a notes fetch cannot make the list look fresher than it is.
    /// </summary>
    private void PersistNotes()
    {
        if (Overview.Definitions.Count == 0)
        {
            // Nothing to anchor the snapshot to — and an empty block list reads as no cache.
            return;
        }

        WriteSnapshot(lastCachedUtc_ ?? DateTime.UtcNow);
    }

    private void WriteSnapshot(DateTime savedUtc)
    {
        BridgeCacheSnapshot snapshot = new()
        {
            SavedUtc = savedUtc,
            Blocks = Overview.Definitions.ToList(),
            TagSources = Overview.AreaTags.ToList(),
            State = lastKnownState_,
            Tags = lastKnownTags_.ToList(),
            NotesByBlock = new Dictionary<Guid, List<LogicNoteDto>>(notesByBlock_)
        };
        cache_.Write(BridgeCacheCodec.Key(Bridge.Id), BridgeCacheCodec.Serialize(snapshot));
        lastCachedUtc_ = snapshot.SavedUtc;
    }

    /// <summary>
    /// Marks the link as gone and drops everything the bridge had been reporting: every tag
    /// reading and the evaluated state become no data, so the screen cannot freeze on the last
    /// values the plant happened to show. The last readings are captured for the Elements
    /// screen before they are cleared, and the banner says how old that data is.
    /// </summary>
    private void MarkOffline()
    {
        CaptureLastKnown();
        PersistLastKnown(force: true);
        tags_.ClearValues(CacheKey);
        Overview.ClearLiveState();
        IsOffline = true;
        OfflineText = lastCachedUtc_ is { } savedUtc
            ? "offline · live values hidden · last synced " + savedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "offline · live values hidden";
        ApplyLatencyUnavailable(LatencyState.Offline, "offline");
        RecomputeSync();
    }

    /// <summary>Keeps the latest live evaluation for the persistent Elements screen.</summary>
    private void CaptureLastKnown()
    {
        if (Overview.State is { } state)
        {
            lastKnownState_ = state;
        }
    }

    /// <summary>Updates the last-known DTOs from a values push, so the persistent read is current.</summary>
    private void ApplyDeltasToLastKnown(IReadOnlyList<HmiValueDelta> deltas)
    {
        foreach (HmiValueDelta delta in deltas)
        {
            HmiTagDto? tag = lastKnownTags_.FirstOrDefault(candidate =>
                string.Equals(candidate.SourceId, delta.SourceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ItemId, delta.ItemId, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
            {
                continue;
            }

            tag.Value = delta.Value;
            tag.TimestampUtc = delta.TimestampUtc;
            tag.DaQuality = delta.DaQuality;
            tag.IsGood = delta.IsGood;
        }
    }

    public async Task<MobileResult<MobileSession>> SignInAsync(string username, string password, CancellationToken cancellationToken)
    {
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

    /// <summary>Closes the live link (the coordinator keeps one link open across bridges).</summary>
    public async Task DisconnectAsync()
    {
        // Closing on purpose leaves the same rule in place as losing the link: nothing this
        // bridge reported stays on screen as a current reading, but the last readings stay
        // available to the Elements screen.
        CaptureLastKnown();
        PersistLastKnown(force: true);
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
            await hub_.ConnectAsync(Bridge.Url, cancellationToken, null, api_.Cookies);
            StatusText = LiveText;
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
            SyncText = lastCachedUtc_ is { } saved
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
