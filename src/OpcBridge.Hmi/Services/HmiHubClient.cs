using Microsoft.AspNetCore.SignalR.Client;
using OpcBridge.Client;

namespace OpcBridge.Hmi.Services;

/// <summary>
/// How the hub link behaves: heartbeats and the automatic-reconnect schedule. The defaults
/// are tuned for a plant LAN — a silently dropped link trips the client's server timeout
/// instead of the stock 30 s, and reconnect retries start immediately and then every 5 s,
/// so a network that comes back is rejoined right away.
/// </summary>
public sealed record HmiHubTiming(
    TimeSpan KeepAliveInterval,
    TimeSpan ServerTimeout,
    IReadOnlyList<TimeSpan> ReconnectDelays)
{
    public static HmiHubTiming Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) });
}

public sealed class HmiHubClient : IAsyncDisposable
{
    private HubConnection? connection_;

    /// <summary>Raised when the link drops and automatic reconnect takes over.</summary>
    public event Func<Exception?, Task>? Reconnecting;

    /// <summary>Raised after automatic reconnect restored the link.</summary>
    public event Func<string?, Task>? Reconnected;

    /// <summary>Raised when the connection is closed for good (reconnect gave up or was refused).</summary>
    public event Func<Exception?, Task>? Closed;

    public async Task ConnectAsync(
        string baseUrl,
        Func<HmiValueDelta[], Task> onValues,
        Func<HmiMappingsChanged, Task> onMappingsChanged,
        CancellationToken ct,
        HmiHubTiming? timing = null)
    {
        await DisposeAsync().ConfigureAwait(false);
        HmiHubTiming settings = timing ?? HmiHubTiming.Default;

        string hubUrl = baseUrl.TrimEnd('/') + "/hmi";
        connection_ = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(settings.ReconnectDelays.ToArray())
            .Build();
        connection_.KeepAliveInterval = settings.KeepAliveInterval;
        connection_.ServerTimeout = settings.ServerTimeout;

        connection_.On<HmiValueDelta[]>("values", async batch => await onValues(batch).ConfigureAwait(false));
        connection_.On<HmiMappingsChanged>("mappingsChanged", async msg => await onMappingsChanged(msg).ConfigureAwait(false));

        connection_.Reconnecting += async error =>
        {
            Func<Exception?, Task>? handler = Reconnecting;
            if (handler is not null)
            {
                await handler(error).ConfigureAwait(false);
            }
        };

        connection_.Reconnected += async connectionId =>
        {
            Func<string?, Task>? handler = Reconnected;
            if (handler is not null)
            {
                await handler(connectionId).ConfigureAwait(false);
            }
        };

        connection_.Closed += async error =>
        {
            Func<Exception?, Task>? handler = Closed;
            if (handler is not null)
            {
                await handler(error).ConfigureAwait(false);
            }
        };

        await connection_.StartAsync(ct).ConfigureAwait(false);
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
