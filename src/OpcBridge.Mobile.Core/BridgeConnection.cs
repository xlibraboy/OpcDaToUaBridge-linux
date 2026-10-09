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
    private List<HmiTagDto> lastTags_ = new();
    private DateTime? lastCachedUtc_;

    public BridgeConnection(SavedBridge bridge, MultiBridgeTagCache tags, Action<Action>? post = null, IBridgeCacheStore? cache = null)
    {
        Bridge = bridge;
        tags_ = tags;
        cache_ = cache ?? new MemoryBridgeCacheStore();
        post_ = post ?? (action => action());

        hub_.LogicStateChanged += snapshot => post_(() =>
        {
            Overview.ApplyState(snapshot);
            // A pushed snapshot is the link working: whatever was cached is live again.
            IsOffline = false;
            StatusText = LiveText;
            RaiseChanged();
        });
        hub_.ValuesChanged += deltas => post_(() =>
        {
            tags_.ApplyDeltas(CacheKey, deltas);
            IsOffline = false;
            RaiseChanged();
        });
        hub_.Reconnecting += _ => post_(() =>
        {
            StatusText = "reconnecting…";
            RaiseChanged();
        });
        hub_.Reconnected += connectionId => post_(() =>
        {
            IsOffline = false;
            StatusText = LiveText;
            _ = RefreshAsync(CancellationToken.None);
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
    }

    /// <summary>Raised on the posting thread after any change this bridge caused.</summary>
    public event Action? Changed;

    public SavedBridge Bridge { get; }

    public LogicApiClient Api => api_;

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

    /// <summary>
    /// True when the list on screen is the last data the bridge served — the link is down,
    /// nothing live is arriving, and <see cref="OfflineText"/> says how old it is.
    /// </summary>
    [ObservableProperty] private bool _isOffline;

    /// <summary>"offline · showing Plant 1 as of 2026-10-09 12:31:05" — what the banner reads.</summary>
    [ObservableProperty] private string _offlineText = string.Empty;

    public bool IsLive => hub_.IsConnected;

    /// <summary>
    /// Takes the session, the logic snapshot and the live link, in that order. Failure is
    /// reported in <see cref="StatusText"/> (the bridge answered but wants a session, the
    /// address is wrong, the link dropped) — never thrown. A failed refresh leaves the cached
    /// list on screen, marked offline.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        StatusText = "searching for the bridge…";
        RaiseChanged();

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
        RaiseChanged();
    }

    /// <summary>Snapshot refresh: definitions, tags and evaluated state.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        MobileResult<IReadOnlyList<LogicBlockDto>> definitions = await api_.GetLogicBlocksAsync(cancellationToken);
        if (definitions.Ok)
        {
            Overview.ApplyDefinitions(definitions.Value!);
            IsConnected = true;
            IsOffline = false;
            StatusText = IsLive ? LiveText : "connected";
        }
        else
        {
            IsConnected = false;
            StatusText = FailureText(definitions);
            MarkOffline();
        }

        if (definitions.Ok)
        {
            MobileResult<IReadOnlyList<HmiTagDto>> tags = await api_.GetTagsAsync(cancellationToken);
            if (tags.Ok)
            {
                lastTags_ = tags.Value!.ToList();
                tags_.ReplaceBridge(CacheKey, lastTags_);
            }

            MobileResult<LogicStateSnapshot> state = await api_.GetLogicStateAsync(cancellationToken);
            if (state.Ok)
            {
                Overview.ApplyState(state.Value);
            }

            Persist();
        }

        RaiseChanged();
    }

    /// <summary>
    /// Shows the last data this bridge served, so the list is never blank while the link is
    /// being acquired (or after it is gone). Leaves <see cref="IsOffline"/> to a failed call —
    /// a cached list that is about to be refreshed is not "offline" yet.
    /// </summary>
    public void LoadCached()
    {
        BridgeCacheSnapshot? snapshot = BridgeCacheCodec.Deserialize(cache_.Read(BridgeCacheCodec.Key(Bridge.Id)));
        if (snapshot is null)
        {
            return;
        }

        Overview.ApplyDefinitions(snapshot.Blocks);
        lastTags_ = snapshot.Tags;
        if (snapshot.Tags.Count > 0)
        {
            tags_.ReplaceBridge(CacheKey, snapshot.Tags);
        }

        Overview.ApplyState(snapshot.State);
        lastCachedUtc_ = snapshot.SavedUtc;
    }

    private void Persist()
    {
        BridgeCacheSnapshot snapshot = new()
        {
            SavedUtc = DateTime.UtcNow,
            Blocks = Overview.Definitions.ToList(),
            State = Overview.State,
            Tags = lastTags_.ToList()
        };
        cache_.Write(BridgeCacheCodec.Key(Bridge.Id), BridgeCacheCodec.Serialize(snapshot));
        lastCachedUtc_ = snapshot.SavedUtc;
    }

    /// <summary>Marks the list as the last data served, aged by when the bridge last answered.</summary>
    private void MarkOffline()
    {
        if (lastCachedUtc_ is not { } savedUtc)
        {
            return;
        }

        IsOffline = true;
        OfflineText = "offline · showing " + Bridge.DisplayName + " as of " + savedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
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
        }

        RaiseChanged();
        return MobileResult<MobileSession>.Success(Session);
    }

    /// <summary>Closes the live link (the coordinator keeps one link open across bridges).</summary>
    public async Task DisconnectAsync()
    {
        await hub_.DisposeAsync();
        RaiseChanged();
    }

    public void Dispose()
    {
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
    }

    private string FailureText(MobileResult<IReadOnlyList<LogicBlockDto>> definitions)
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
