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
    private readonly Action<Action> post_;

    public BridgeConnection(SavedBridge bridge, MultiBridgeTagCache tags, Action<Action>? post = null)
    {
        Bridge = bridge;
        tags_ = tags;
        post_ = post ?? (action => action());

        hub_.LogicStateChanged += snapshot => post_(() =>
        {
            Overview.ApplyState(snapshot);
            StatusText = LiveText;
            RaiseChanged();
        });
        hub_.ValuesChanged += deltas => post_(() =>
        {
            tags_.ApplyDeltas(CacheKey, deltas);
            RaiseChanged();
        });
        hub_.Reconnecting += _ => post_(() =>
        {
            StatusText = "reconnecting…";
            RaiseChanged();
        });
        hub_.Reconnected += connectionId => post_(() =>
        {
            StatusText = LiveText;
            _ = RefreshAsync(CancellationToken.None);
            RaiseChanged();
        });
        hub_.Closed += error => post_(() =>
        {
            StatusText = error is null ? "live link closed" : "live link lost: " + error.Message;
            RaiseChanged();
        });
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

    public bool IsLive => hub_.IsConnected;

    /// <summary>
    /// Takes the session, the logic snapshot and the live link, in that order. Failure is
    /// reported in <see cref="StatusText"/> (the bridge answered but wants a session, the
    /// address is wrong, the link dropped) — never thrown.
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
            StatusText = IsLive ? LiveText : "connected";
        }
        else
        {
            IsConnected = false;
            StatusText = FailureText(definitions);
        }

        if (definitions.Ok)
        {
            MobileResult<IReadOnlyList<HmiTagDto>> tags = await api_.GetTagsAsync(cancellationToken);
            if (tags.Ok)
            {
                tags_.ReplaceBridge(CacheKey, tags.Value!);
            }

            MobileResult<LogicStateSnapshot> state = await api_.GetLogicStateAsync(cancellationToken);
            if (state.Ok)
            {
                Overview.ApplyState(state.Value);
            }
        }

        RaiseChanged();
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
