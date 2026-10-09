using System.Collections.ObjectModel;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// The phone's bridge list: which bridges are saved, which one is being shown, and the one
/// live link. Adding a bridge probes the address first (so a typo is reported, not saved),
/// and activating one closes the previous link — the operator watches one plant at a time.
/// <see cref="Bridges"/> is bound to the UI, so these methods await without
/// <c>ConfigureAwait(false)</c>: they resume on the caller's thread (the UI thread) before
/// touching the list.
/// </summary>
public sealed class BridgeCoordinator : IDisposable
{
    /// <summary>Where the saved list and the active bridge live on the phone.</summary>
    public const string BridgesKey = "opcbridge.logic.bridges";

    public const string ActiveBridgeKey = "opcbridge.logic.activeBridgeId";

    private readonly IBridgeSettings settings_;
    private readonly Action<Action> post_;
    private readonly MultiBridgeTagCache tags_;

    public BridgeCoordinator(IBridgeSettings settings, MultiBridgeTagCache? tags = null, Action<Action>? post = null)
    {
        settings_ = settings;
        tags_ = tags ?? new MultiBridgeTagCache();
        post_ = post ?? (action => action());
    }

    /// <summary>Raised on the posting thread when the list, the active bridge or a state changes.</summary>
    public event Action? Changed;

    public ObservableCollection<BridgeConnection> Bridges { get; } = new();

    public MultiBridgeTagCache Tags => tags_;

    public BridgeConnection? Active { get; private set; }

    /// <summary>The saved bridges from the last run, none of them connected yet.</summary>
    public void Load()
    {
        Bridges.Clear();
        foreach (SavedBridge bridge in BridgeListCodec.Deserialize(settings_.Get(BridgesKey, string.Empty)))
        {
            Bridges.Add(NewConnection(bridge));
        }

        string activeId = settings_.Get(ActiveBridgeKey, string.Empty);
        Active = Bridges.FirstOrDefault(connection => connection.Bridge.Id == activeId) ?? Bridges.FirstOrDefault();
        if (Active is not null)
        {
            Active.IsActive = true;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Adds the address (or reuses the entry already saved for it) and makes it the active
    /// bridge. A probe that finds no bridge reports the address instead of saving it.
    /// </summary>
    public async Task<MobileResult<BridgeConnection>> AddOrConnectAsync(string host, string? name, CancellationToken cancellationToken)
    {
        string? url = await BridgeProbe.FindAsync(host, 1200, cancellationToken);
        if (url is null)
        {
            return MobileResult<BridgeConnection>.Fail("no bridge answered on " + host);
        }

        BridgeConnection? connection = Bridges.FirstOrDefault(candidate => BridgeListCodec.SameAddress(candidate.Bridge.Url, url));
        if (connection is null)
        {
            SavedBridge bridge = new(
                BridgeListCodec.NewId(),
                string.IsNullOrWhiteSpace(name) ? BridgeListCodec.DefaultName(url) : name.Trim(),
                url);
            connection = NewConnection(bridge);
            Bridges.Add(connection);
        }

        await ActivateAsync(connection, cancellationToken);
        return MobileResult<BridgeConnection>.Success(connection);
    }

    /// <summary>Shows this bridge: the previous link is closed, this one connected.</summary>
    public async Task ActivateAsync(BridgeConnection connection, CancellationToken cancellationToken)
    {
        if (Active is not null && !ReferenceEquals(Active, connection))
        {
            Active.IsActive = false;
            await Active.DisconnectAsync();
        }

        Active = connection;
        connection.IsActive = true;
        Persist();
        RaiseChanged();

        await connection.ConnectAsync(cancellationToken);
    }

    /// <summary>Connects the remembered bridge on start-up, when it is not connected yet.</summary>
    public async Task ConnectActiveAsync(CancellationToken cancellationToken)
    {
        if (Active is not null && !Active.IsConnected)
        {
            await Active.ConnectAsync(cancellationToken);
        }
    }

    public async Task<MobileResult<MobileSession>> SignInAsync(
        BridgeConnection connection,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        return await connection.SignInAsync(username, password, cancellationToken);
    }

    /// <summary>Forgets a bridge; its tag entries go with it.</summary>
    public async Task RemoveAsync(BridgeConnection connection)
    {
        if (ReferenceEquals(Active, connection))
        {
            await connection.DisconnectAsync();
            connection.IsActive = false;
            Active = null;
        }

        Bridges.Remove(connection);
        tags_.ClearBridge(connection.CacheKey);
        connection.Dispose();
        Active ??= Bridges.FirstOrDefault();
        Persist();
        RaiseChanged();
    }

    public void Dispose()
    {
        foreach (BridgeConnection connection in Bridges)
        {
            connection.Dispose();
        }

        Bridges.Clear();
        Active = null;
    }

    private BridgeConnection NewConnection(SavedBridge bridge)
    {
        BridgeConnection connection = new(bridge, tags_, post_);
        connection.Changed += RaiseChanged;
        return connection;
    }

    private void Persist()
    {
        settings_.Set(BridgesKey, BridgeListCodec.Serialize(Bridges.Select(connection => connection.Bridge)));
        settings_.Set(ActiveBridgeKey, Active?.Bridge.Id ?? string.Empty);
    }

    private void RaiseChanged() => Changed?.Invoke();
}
