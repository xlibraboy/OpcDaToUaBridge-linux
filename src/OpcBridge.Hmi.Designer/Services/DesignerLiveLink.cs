using OpcBridge.Client;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;

namespace OpcBridge.Hmi.Designer.Services;

/// <summary>How good the Designer's picture of the bridge is right now.</summary>
public enum DesignerLiveState
{
    /// <summary>No snapshot: the bridge did not answer.</summary>
    Offline,

    /// <summary>Tag snapshot loaded, but the live value stream is not connected.</summary>
    Snapshot,

    /// <summary>Snapshot loaded and the live value stream is up.</summary>
    Live
}

/// <summary>
/// The Designer's live link to one bridge: loads the mapped-tag snapshot into the shared
/// cache, then follows the SignalR value stream so bound widgets preview real values.
/// Callbacks run on background threads; the view model marshals them to the UI thread.
/// </summary>
public interface IDesignerLiveLink : IAsyncDisposable
{
    DesignerLiveState State { get; }

    string? LastError { get; }

    event Action? StateChanged;

    Task StartAsync(string baseUrl, string bridgeId, Action onChanged, CancellationToken ct);

    Task StopAsync();
}

public sealed class DesignerLiveLink : IDesignerLiveLink
{
    private readonly MultiBridgeTagCache cache_;
    private readonly BridgeApiClient api_ = new();
    private readonly HmiHubClient hub_ = new();
    private readonly object sync_ = new();
    private CancellationTokenSource? lifetime_;
    private string bridgeId_ = "default";
    private Action? onChanged_;

    public DesignerLiveLink(MultiBridgeTagCache cache) => cache_ = cache;

    public DesignerLiveState State { get; private set; } = DesignerLiveState.Offline;

    public string? LastError { get; private set; }

    public event Action? StateChanged;

    public async Task StartAsync(string baseUrl, string bridgeId, Action onChanged, CancellationToken ct)
    {
        await StopAsync().ConfigureAwait(false);

        bridgeId_ = string.IsNullOrWhiteSpace(bridgeId) ? "default" : bridgeId.Trim();
        onChanged_ = onChanged;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (sync_)
        {
            lifetime_ = lifetime;
        }

        CancellationToken token = lifetime.Token;
        api_.SetBaseAddress(baseUrl);

        // Snapshot first: bound widgets preview values even when the live stream cannot connect.
        try
        {
            HmiTagsResponse snapshot = await api_.GetTagsAsync(token).ConfigureAwait(false);
            cache_.ReplaceBridge(bridgeId_, snapshot.Tags);
            SetState(DesignerLiveState.Snapshot, null);
            Notify();
        }
        catch (Exception ex)
        {
            SetState(DesignerLiveState.Offline, Describe(ex));
            Notify();
            return;
        }

        _ = ConnectHubAsync(baseUrl, token);
    }

    private async Task ConnectHubAsync(string baseUrl, CancellationToken ct)
    {
        hub_.Reconnecting += _ =>
        {
            SetState(DesignerLiveState.Snapshot, "Live link reconnecting; showing the last values.");
            Notify();
            return Task.CompletedTask;
        };
        hub_.Reconnected += connectionId =>
        {
            _ = connectionId;
            SetState(DesignerLiveState.Live, null);
            Notify();
            _ = RefreshSnapshotAsync(ct);
            return Task.CompletedTask;
        };
        hub_.Closed += _ =>
        {
            SetState(DesignerLiveState.Snapshot, "Live link closed; showing the last values.");
            Notify();
            return Task.CompletedTask;
        };

        try
        {
            await hub_.ConnectAsync(
                baseUrl,
                deltas =>
                {
                    cache_.ApplyDeltas(bridgeId_, deltas);
                    Notify();
                    return Task.CompletedTask;
                },
                _ => RefreshSnapshotAsync(ct),
                ct).ConfigureAwait(false);

            SetState(DesignerLiveState.Live, null);
            Notify();
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                SetState(DesignerLiveState.Snapshot, Describe(ex));
                Notify();
            }
        }
    }

    /// <summary>Mapping edits on the bridge: re-read the snapshot so the tag list stays current.</summary>
    private async Task RefreshSnapshotAsync(CancellationToken ct)
    {
        try
        {
            HmiTagsResponse snapshot = await api_.GetTagsAsync(ct).ConfigureAwait(false);
            cache_.ReplaceBridge(bridgeId_, snapshot.Tags);
            Notify();
        }
        catch
        {
            // The next reconnect or manual refresh retries.
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? lifetime;
        lock (sync_)
        {
            lifetime = lifetime_;
            lifetime_ = null;
        }

        if (lifetime is not null)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }

        await hub_.DisposeAsync().ConfigureAwait(false);
        onChanged_ = null;
        SetState(DesignerLiveState.Offline, null);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        api_.Dispose();
    }

    private void Notify() => onChanged_?.Invoke();

    private void SetState(DesignerLiveState state, string? error)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke();
    }

    private static string Describe(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException
            ? "Bridge not reachable."
            : ex.Message;
}
