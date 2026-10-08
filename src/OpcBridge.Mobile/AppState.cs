using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;

namespace OpcBridge.Mobile;

/// <summary>
/// App-wide state: the bridge address, the shared tag cache, the overview view model and the
/// connection wiring. The hub pushes logic snapshots and value deltas; both are marshalled to
/// the UI thread here, and the screens read this object rather than talking to the bridge.
/// </summary>
public sealed class AppState : IDisposable
{
    private const string BridgeUrlKey = "opcbridge.logic.bridgeUrl";
    private readonly LogicApiClient api_;
    private readonly MobileHubClient hub_;
    private readonly MultiBridgeTagCache tags_ = new();

    public AppState(LogicApiClient api, MobileHubClient hub)
    {
        api_ = api;
        hub_ = hub;
        BaseUrl = Preferences.Default.Get(BridgeUrlKey, "http://127.0.0.1:8080");
        api_.SetBaseAddress(BaseUrl);

        hub_.LogicStateChanged += snapshot =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Overview.ApplyState(snapshot);
                ConnectionText = "live";
                RaiseChanged();
            });

        hub_.ValuesChanged += deltas =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                tags_.ApplyDeltas(MobileBridge.Id, deltas);
                RaiseChanged();
            });

        hub_.Reconnecting += _ =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ConnectionText = "reconnecting…";
                RaiseChanged();
            });

        hub_.Reconnected += _ =>
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                ConnectionText = "live";
                await RefreshAsync(CancellationToken.None);
            });

        hub_.Closed += error =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ConnectionText = "live link lost" + (error is null ? string.Empty : ": " + error.Message);
                RaiseChanged();
            });
    }

    public LogicApiClient Api => api_;

    public MultiBridgeTagCache Tags => tags_;

    public LogicOverviewViewModel Overview { get; } = new();

    public string BaseUrl { get; private set; }

    public bool IsConnected { get; private set; }

    public MobileSession? Session { get; private set; }

    public string ConnectionText { get; private set; } = "not connected";

    /// <summary>Raised on the UI thread after any live update, so open screens can repaint.</summary>
    public event Action? Changed;

    /// <summary>
    /// Finds the bridge from what the operator typed (host, host:port or URL), takes a
    /// snapshot and opens the live link. Failure is reported, never thrown.
    /// </summary>
    public async Task<MobileResult<string>> ConnectAsync(string host, CancellationToken cancellationToken)
    {
        ConnectionText = "searching for the bridge…";
        RaiseChanged();

        string? found = await BridgeProbe.FindAsync(host, 1200, cancellationToken);
        if (found is null)
        {
            IsConnected = false;
            ConnectionText = "no bridge answered on " + host;
            RaiseChanged();
            return MobileResult<string>.Fail(ConnectionText);
        }

        BaseUrl = found;
        Preferences.Default.Set(BridgeUrlKey, found);
        api_.SetBaseAddress(found);

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

        try
        {
            await hub_.ConnectAsync(found, cancellationToken);
            ConnectionText = "live";
        }
        catch (Exception exception)
        {
            ConnectionText = "connected (live link failed: " + exception.Message + ")";
        }

        IsConnected = true;
        RaiseChanged();
        return MobileResult<string>.Success(found);
    }

    /// <summary>Snapshot refresh: definitions, tags and evaluated state.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        MobileResult<IReadOnlyList<LogicBlockDto>> definitions = await api_.GetLogicBlocksAsync(cancellationToken);
        if (definitions.Ok)
        {
            Overview.ApplyDefinitions(definitions.Value!);
        }

        MobileResult<IReadOnlyList<HmiTagDto>> tags = await api_.GetTagsAsync(cancellationToken);
        if (tags.Ok)
        {
            tags_.ReplaceBridge(MobileBridge.Id, tags.Value!);
        }

        MobileResult<LogicStateSnapshot> state = await api_.GetLogicStateAsync(cancellationToken);
        if (state.Ok)
        {
            Overview.ApplyState(state.Value);
        }

        RaiseChanged();
    }

    public async Task<MobileResult<MobileSession>> SignInAsync(string username, string password, CancellationToken cancellationToken)
    {
        MobileResult<LogicApiClient.SessionResponse> result = await api_.LoginAsync(username, password, cancellationToken);
        if (!result.Ok || result.Value is null)
        {
            return MobileResult<MobileSession>.Fail(result.Error ?? "sign-in failed");
        }

        Session = new MobileSession(
            result.Value.Authenticated,
            result.Value.Username,
            result.Value.DisplayName,
            result.Value.Role,
            result.Value.AuthEnabled);
        RaiseChanged();
        return MobileResult<MobileSession>.Success(Session);
    }

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        hub_.DisposeAsync().AsTask().GetAwaiter().GetResult();
        api_.Dispose();
    }
}
