using Microsoft.AspNetCore.SignalR.Client;
using OpcBridge.Client;

namespace OpcBridge.Mobile.Core;

/// <summary>
/// How the hub link behaves: heartbeats and the reconnect schedule, defaulted for a plant
/// LAN (the same timings the desktop HMI uses).
/// </summary>
public sealed record MobileHubTiming(
    TimeSpan KeepAliveInterval,
    TimeSpan ServerTimeout,
    IReadOnlyList<TimeSpan> ReconnectDelays)
{
    public static MobileHubTiming Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) });
}

/// <summary>
/// SignalR client for the bridge's <c>/hmi</c> hub: the <c>logic</c> snapshots the bridge
/// pushes when the derived states change, plus the existing batched <c>values</c> deltas.
/// </summary>
public sealed class MobileHubClient : IAsyncDisposable
{
    private HubConnection? connection_;

    public event Action<LogicStateSnapshot>? LogicStateChanged;
    public event Action<IReadOnlyList<HmiValueDelta>>? ValuesChanged;

    /// <summary>Raised when the link drops and automatic reconnect takes over.</summary>
    public event Action<Exception?>? Reconnecting;

    /// <summary>Raised after automatic reconnect restored the link (the app must re-sync snapshots).</summary>
    public event Action<string?>? Reconnected;

    /// <summary>Raised when the connection is closed for good.</summary>
    public event Action<Exception?>? Closed;

    public bool IsConnected => connection_?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string baseUrl, CancellationToken cancellationToken, MobileHubTiming? timing = null)
    {
        await DisposeAsync().ConfigureAwait(false);
        MobileHubTiming settings = timing ?? MobileHubTiming.Default;

        connection_ = new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + "/hmi")
            .WithAutomaticReconnect(settings.ReconnectDelays.ToArray())
            .Build();
        connection_.KeepAliveInterval = settings.KeepAliveInterval;
        connection_.ServerTimeout = settings.ServerTimeout;

        connection_.On<LogicStateSnapshot>("logic", snapshot => LogicStateChanged?.Invoke(snapshot));
        connection_.On<HmiValueDelta[]>("values", batch => ValuesChanged?.Invoke(batch));
        connection_.Reconnecting += error => { Reconnecting?.Invoke(error); return Task.CompletedTask; };
        connection_.Reconnected += connectionId => { Reconnected?.Invoke(connectionId); return Task.CompletedTask; };
        connection_.Closed += error => { Closed?.Invoke(error); return Task.CompletedTask; };

        await connection_.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection_ is not null)
        {
            await connection_.DisposeAsync().ConfigureAwait(false);
            connection_ = null;
        }
    }
}
