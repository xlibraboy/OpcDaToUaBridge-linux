using System.Collections.Concurrent;
using System.Text.Json;
using OpcBridge.Client.Workers;
using OpcBridge.Core;
using OpcBridge.Da;

namespace OpcBridge.App;

/// <summary>Seam between the proxy and the supervisor (a fake host in tests).</summary>
internal interface IWorkerHost
{
    Task<IWorkerChannel> EnsureChannelAsync(string workerKey, CancellationToken cancellationToken);

    void ReleaseClient(string workerKey);
}

/// <summary>
/// Parent-side proxy for a source hosted in a DA worker process. Implements the same client
/// seam as the in-process <c>OpcDaClient</c> (ISourceClient, ISubscribableSourceClient,
/// ISubscriptionActiveSource, IRateGroupBoundSource), so BridgeWorker treats an isolated
/// source like any other source and its retry/watchdog logic drives worker restarts through
/// <see cref="IWorkerHost"/>.
/// </summary>
internal sealed class WorkerSourceClient : ISourceClient, ISubscribableSourceClient, ISubscriptionActiveSource, IRateGroupBoundSource
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(3);

    private readonly IWorkerHost host_;
    private readonly string workerKey_;
    private readonly string sourceId_;
    private readonly ConcurrentDictionary<string, (short? CanonicalDataType, int? AccessRights)?> metadataCache_ = new();
    private IWorkerChannel? channel_;
    private bool subscriptionActive_;
    private bool disposed_;

    public WorkerSourceClient(IWorkerHost host, string workerKey, string sourceId)
    {
        host_ = host;
        workerKey_ = workerKey;
        sourceId_ = sourceId;
    }

    public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived;

    public bool IsSubscriptionActive => subscriptionActive_;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        IWorkerChannel channel = await host_.EnsureChannelAsync(workerKey_, cancellationToken).ConfigureAwait(false);

        WorkerFrame response;
        try
        {
            response = await channel.RequestAsync(
                    WorkerFrameTypes.Connect,
                    new WorkerConnectRequest(sourceId_),
                    ConnectTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsChannelFailure(ex))
        {
            throw new SourceConnectionLostException($"Worker source '{sourceId_}': {ex.Message}", ex);
        }

        WorkerAck? ack = response.PayloadAs<WorkerAck>();
        if (ack is null || !ack.Ok)
        {
            string message = ack?.Error ?? "Worker connect failed.";
            if (ack?.Transient == true)
            {
                throw new SourceConnectionLostException($"Worker source '{sourceId_}': {message}");
            }

            throw new InvalidOperationException($"Worker source '{sourceId_}': {message}");
        }

        subscriptionActive_ = ack.SubscriptionActive;
        Bind(channel);
    }

    public async Task<IReadOnlyList<BridgeValue>> ReadAsync(
        IReadOnlyList<TagMapping> mappings,
        CancellationToken cancellationToken)
    {
        if (mappings.Count == 0)
        {
            return Array.Empty<BridgeValue>();
        }

        List<WorkerTagRef> tags = mappings
            .Select(mapping => new WorkerTagRef(mapping.ItemId, mapping.PollRateMs))
            .ToList();

        WorkerFrame response = await RequestAsync(
                WorkerFrameTypes.Read,
                new WorkerReadRequest(sourceId_, tags),
                RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        WorkerReadResult? result = response.PayloadAs<WorkerReadResult>();
        if (result is null || result.Error is not null)
        {
            string message = result?.Error ?? "Worker read failed.";
            if (result?.Transient == true)
            {
                throw new SourceConnectionLostException($"Worker source '{sourceId_}': {message}");
            }

            throw new InvalidOperationException($"Worker source '{sourceId_}': {message}");
        }

        return (result.Values ?? Array.Empty<WireValue>()).Select(ToBridgeValue).ToList();
    }

    public async Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken)
    {
        (string type, JsonElement json) = WireValueCodec.Encode(value);

        WorkerFrame response = await RequestAsync(
                WorkerFrameTypes.Write,
                new WorkerWriteRequest(sourceId_, itemId, type, json),
                RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        WorkerWriteResult? result = response.PayloadAs<WorkerWriteResult>();
        if (result is null)
        {
            throw new SourceConnectionLostException($"Worker source '{sourceId_}': write returned no result.");
        }

        if (result.Error is not null && result.Transient)
        {
            throw new SourceConnectionLostException($"Worker source '{sourceId_}': {result.Error}");
        }

        return result.Ok;
    }

    public bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights)
    {
        if (metadataCache_.TryGetValue(itemId, out var cached))
        {
            canonicalDataType = cached?.CanonicalDataType;
            accessRights = cached?.AccessRights;
            return cached is not null;
        }

        try
        {
            WorkerFrame response = RequestAsync(
                    WorkerFrameTypes.Metadata,
                    new WorkerMetadataRequest(sourceId_, itemId),
                    MetadataTimeout,
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            WorkerMetadataResult? result = response.PayloadAs<WorkerMetadataResult>();
            if (result is null || !result.Found)
            {
                metadataCache_[itemId] = null;
                canonicalDataType = null;
                accessRights = null;
                return false;
            }

            metadataCache_[itemId] = (result.CanonicalDataType, result.AccessRights);
            canonicalDataType = result.CanonicalDataType;
            accessRights = result.AccessRights;
            return true;
        }
        catch (Exception)
        {
            // A missing worker must not fail an HTTP read of interlink metadata.
            canonicalDataType = null;
            accessRights = null;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;
        IWorkerChannel? channel = channel_;
        Detach();

        if (channel is not null)
        {
            try
            {
                await channel.RequestAsync(
                        WorkerFrameTypes.Disconnect,
                        new WorkerDisconnectRequest(sourceId_),
                        DisconnectTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Disposal must not throw; the supervisor tears the worker down when its
                // last source leaves.
            }
        }

        host_.ReleaseClient(workerKey_);
    }

    private async Task<WorkerFrame> RequestAsync(
        string type,
        object payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        IWorkerChannel channel = channel_
            ?? throw new SourceConnectionLostException($"Worker source '{sourceId_}' is not connected.");

        try
        {
            return await channel.RequestAsync(type, payload, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsChannelFailure(ex))
        {
            throw new SourceConnectionLostException($"Worker source '{sourceId_}': {ex.Message}", ex);
        }
    }

    private static bool IsChannelFailure(Exception exception) =>
        exception is IOException or ObjectDisposedException or TimeoutException or WorkerProtocolException or InvalidOperationException;

    private void Bind(IWorkerChannel channel)
    {
        Detach();
        channel_ = channel;
        channel.PushReceived += OnPush;
    }

    private void Detach()
    {
        if (channel_ is not null)
        {
            channel_.PushReceived -= OnPush;
            channel_ = null;
        }
    }

    private void OnPush(WorkerFrame frame)
    {
        if (frame.Type != WorkerFrameTypes.Values)
        {
            return;
        }

        WorkerValues? payload = frame.PayloadAs<WorkerValues>();
        if (payload is null
            || !string.Equals(payload.SourceId, sourceId_, StringComparison.OrdinalIgnoreCase)
            || payload.Values is null)
        {
            return;
        }

        ValuesReceived?.Invoke(payload.Values.Select(ToBridgeValue).ToList());
    }

    private BridgeValue ToBridgeValue(WireValue value)
        => new(
            sourceId_,
            value.ItemId,
            WireValueCodec.Decode(value.Type, value.Value),
            value.TimestampUtc,
            value.DaQuality,
            value.IsGood);
}
