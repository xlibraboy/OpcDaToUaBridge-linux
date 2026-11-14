using System.Collections.ObjectModel;
using OpcBridge.Client;
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
    private readonly IMobileStore store_;
    private readonly AccessPointGate? accessPointGate_;

    public BridgeCoordinator(
        IBridgeSettings settings,
        MultiBridgeTagCache? tags = null,
        Action<Action>? post = null,
        IMobileStore? store = null,
        AccessPointGate? accessPointGate = null)
    {
        settings_ = settings;
        tags_ = tags ?? new MultiBridgeTagCache();
        store_ = store ?? new MemoryMobileStore();
        post_ = post ?? (action => action());
        accessPointGate_ = accessPointGate;
        if (accessPointGate_ is not null)
        {
            accessPointGate_.Changed += OnAccessPointChanged;
        }
    }

    /// <summary>Raised on the posting thread when the list, the active bridge or a state changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised on the posting thread with the readings one live push changed. Screens that show
    /// readings update only the rows behind these tags instead of repainting their whole list.
    /// </summary>
    public event Action<IReadOnlyList<HmiValueDelta>>? TagValuesChanged;

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
        // A phone with no access point provisioned may still find and sign in to a bridge on
        // the plant network; that session is how an Engineer/Admin provisions the AP. A bound
        // phone may only probe while the bound BSSID is the one it is on.
        if (accessPointGate_ is { Binding: not null, IsOnlineAllowed: false })
        {
            return MobileResult<BridgeConnection>.Fail(
                "connect to the plant access point first — Settings ▸ Plant Wi-Fi");
        }

        // The sweep is network-bound and must not share the UI thread with the page that is
        // waiting for it; the coordinator returns to the caller's context after the probe.
        string? url = await Task.Run(
            () => BridgeProbe.FindAsync(host, 3000, cancellationToken),
            cancellationToken);
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
        if (Active is null)
        {
            return;
        }

        if (accessPointGate_ is { IsOnlineAllowed: false })
        {
            await Active.SuspendForAccessPointAsync(AccessPointGate.BlockedText);
            RaiseChanged();
            return;
        }

        if (!Active.IsConnected)
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
        store_.ForgetBridge(connection.Bridge.Id);
        connection.Dispose();
        Active ??= Bridges.FirstOrDefault();
        Persist();
        RaiseChanged();
    }

    public void Dispose()
    {
        if (accessPointGate_ is not null)
        {
            accessPointGate_.Changed -= OnAccessPointChanged;
        }

        foreach (BridgeConnection connection in Bridges)
        {
            connection.Dispose();
        }

        Bridges.Clear();
        Active = null;
    }

    private BridgeConnection NewConnection(SavedBridge bridge)
    {
        BridgeConnection connection = new(bridge, tags_, post_, store_, accessPointGate_);
        connection.Changed += RaiseChanged;
        connection.TagValuesChanged += RaiseTagValuesChanged;
        return connection;
    }

    /// <summary>
    /// The phone's Wi-Fi changed. Leaving the bound AP closes the live link immediately;
    /// rejoining it brings the remembered bridge back without waiting for the operator.
    /// </summary>
    private void OnAccessPointChanged()
    {
        post_(() =>
        {
            BridgeConnection? active = Active;
            if (active is not null)
            {
                if (accessPointGate_ is { IsOnlineAllowed: false })
                {
                    _ = SuspendActiveAsync(active);
                }
                else if (!active.IsConnected)
                {
                    _ = ReconnectActiveAsync(active);
                }
            }

            RaiseChanged();
        });
    }

    private async Task SuspendActiveAsync(BridgeConnection connection)
    {
        try
        {
            await connection.SuspendForAccessPointAsync(AccessPointGate.BlockedText);
        }
        catch (Exception)
        {
            // The link is already gone; the stored structure stays on screen and the next
            // change reapplies the gate.
        }
    }

    private async Task ReconnectActiveAsync(BridgeConnection connection)
    {
        if (!ReferenceEquals(Active, connection))
        {
            return;
        }

        try
        {
            await connection.ConnectAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // A reconnect that fails leaves the offline banner in place.
        }
    }

    private void Persist()
    {
        settings_.Set(BridgesKey, BridgeListCodec.Serialize(Bridges.Select(connection => connection.Bridge)));
        settings_.Set(ActiveBridgeKey, Active?.Bridge.Id ?? string.Empty);
    }

    private void RaiseChanged() => Changed?.Invoke();

    private void RaiseTagValuesChanged(IReadOnlyList<HmiValueDelta> deltas) => TagValuesChanged?.Invoke(deltas);
}
