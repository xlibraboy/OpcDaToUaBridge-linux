using Microsoft.AspNetCore.SignalR.Client;
using OpcBridge.Client;
using System.Net;

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

    /// <summary>
    /// Opens the link. <paramref name="cookies"/> carries the bridge session from
    /// <see cref="LogicApiClient"/>, so a bridge that gates the hub accepts the handshake.
    /// </summary>
    public async Task ConnectAsync(
        string baseUrl,
        CancellationToken cancellationToken,
        MobileHubTiming? timing = null,
        CookieContainer? cookies = null,
        Func<bool>? canGoOnline = null)
    {
        if (canGoOnline?.Invoke() == false)
        {
            throw new InvalidOperationException(AccessPointGate.BlockedText);
        }

        await DisposeAsync().ConfigureAwait(false);
        MobileHubTiming settings = timing ?? MobileHubTiming.Default;

        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + "/hmi", options =>
            {
                if (cookies is not null)
                {
                    options.Cookies = cookies;
                }
            })
            .WithAutomaticReconnect(settings.ReconnectDelays.ToArray())
            .Build();
        connection_ = connection;
        connection.KeepAliveInterval = settings.KeepAliveInterval;
        connection.ServerTimeout = settings.ServerTimeout;

        // Replacing a link disposes the old one; its closing events must not be mistaken for
        // the current link dropping. Every handler checks that it still owns connection_.
        connection.On<LogicStateSnapshot>("logic", snapshot =>
        {
            if (ReferenceEquals(connection_, connection))
            {
                LogicStateChanged?.Invoke(snapshot);
            }
        });
        connection.On<HmiValueDelta[]>("values", batch =>
        {
            if (ReferenceEquals(connection_, connection))
            {
                ValuesChanged?.Invoke(batch);
            }
        });
        connection.Reconnecting += error =>
        {
            if (ReferenceEquals(connection_, connection))
            {
                Reconnecting?.Invoke(error);
            }

            return Task.CompletedTask;
        };
        connection.Reconnected += connectionId =>
        {
            if (ReferenceEquals(connection_, connection))
            {
                Reconnected?.Invoke(connectionId);
            }

            return Task.CompletedTask;
        };
        connection.Closed += error =>
        {
            if (ReferenceEquals(connection_, connection))
            {
                Closed?.Invoke(error);
            }

            return Task.CompletedTask;
        };

        await connection.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        HubConnection? connection = connection_;
        // Clear the field first: disposing raises Closed, and the handler must see that this
        // connection is no longer the current one.
        connection_ = null;
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
