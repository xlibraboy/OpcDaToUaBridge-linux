using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using OpcBridge.Client.Workers;
using OpcBridge.Core;
using OpcBridge.Da;

namespace OpcBridge.App;

/// <summary>
/// Hosts the worker's OPC DA source clients behind the pipe protocol: one client per source,
/// created and disposed on demand, read/write/metadata requests answered with the request's
/// sequence number, subscription values pushed as typed frames. Runs inside the worker
/// process only (Windows), never in the bridge.
/// </summary>
internal sealed class DaWorkerSession : IAsyncDisposable
{
    private const int ValueChunkSize = 500;
    private const int HeartbeatIntervalMs = 2000;

    private readonly Stream pipe_;
    private readonly Channel<WorkerFrame> outbound_ = Channel.CreateUnbounded<WorkerFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, WorkerSource> sources_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim mutationLock_ = new(1, 1);
    private readonly CancellationTokenSource shutdown_ = new();
    private readonly long startedTimestamp_ = Stopwatch.GetTimestamp();
    private Task? writerTask_;
    private Task? heartbeatTask_;
    private long pushSequence_;

    private sealed class WorkerSource
    {
        public WorkerSource(WorkerSourceConfig config)
        {
            Config = config;
        }

        public WorkerSourceConfig Config { get; }

        public ISourceClient? Client { get; set; }
    }

    public DaWorkerSession(WorkerBootstrap bootstrap, Stream pipe)
    {
        pipe_ = pipe;
        foreach (WorkerSourceConfig config in bootstrap.Sources ?? Array.Empty<WorkerSourceConfig>())
        {
            sources_[config.SourceId] = new WorkerSource(config);
        }
    }

    public void Start()
    {
        writerTask_ = Task.Run(WriterLoopAsync);
        Send(WorkerFrame.Create(
            WorkerFrameTypes.Ready,
            NextPushSequence(),
            new WorkerReady(WorkerProtocol.Version, Environment.ProcessId, CurrentAccountName())));
        heartbeatTask_ = Task.Run(HeartbeatLoopAsync);
    }

    /// <summary>Handles one inbound frame; returns false when the worker should exit cleanly.</summary>
    public bool HandleFrame(WorkerFrame frame)
    {
        switch (frame.Type)
        {
            case WorkerFrameTypes.Shutdown:
                return false;

            case WorkerFrameTypes.Ping:
                Send(WorkerFrame.Create(
                    WorkerFrameTypes.Pong,
                    frame.Seq,
                    new WorkerPong(Interlocked.Read(ref pushSequence_))));
                return true;

            case WorkerFrameTypes.Connect:
            case WorkerFrameTypes.Disconnect:
            case WorkerFrameTypes.UpsertSource:
            case WorkerFrameTypes.RemoveSource:
            case WorkerFrameTypes.Read:
            case WorkerFrameTypes.Write:
            case WorkerFrameTypes.Metadata:
            case WorkerFrameTypes.Browse:
                _ = Task.Run(() => HandleRequestAsync(frame));
                return true;

            default:
                throw new WorkerProtocolException($"Unexpected frame type '{frame.Type}'.");
        }
    }

    public async Task StopAsync()
    {
        shutdown_.Cancel();
        outbound_.Writer.TryComplete();

        await WaitForTaskAsync(writerTask_).ConfigureAwait(false);
        await WaitForTaskAsync(heartbeatTask_).ConfigureAwait(false);

        foreach (WorkerSource source in sources_.Values)
        {
            await DisposeClientAsync(source).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        shutdown_.Dispose();
        mutationLock_.Dispose();
    }

    private static async Task WaitForTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Shutdown must not throw on a task that is still winding down.
        }
    }

    private async Task HandleRequestAsync(WorkerFrame frame)
    {
        try
        {
            switch (frame.Type)
            {
                case WorkerFrameTypes.Connect:
                    await HandleConnectAsync(frame).ConfigureAwait(false);
                    return;
                case WorkerFrameTypes.Disconnect:
                    await HandleDisconnectAsync(frame, remove: false).ConfigureAwait(false);
                    return;
                case WorkerFrameTypes.RemoveSource:
                    await HandleDisconnectAsync(frame, remove: true).ConfigureAwait(false);
                    return;
                case WorkerFrameTypes.UpsertSource:
                    await HandleUpsertAsync(frame).ConfigureAwait(false);
                    return;
                case WorkerFrameTypes.Read:
                    await HandleReadAsync(frame).ConfigureAwait(false);
                    return;
                case WorkerFrameTypes.Write:
                    await HandleWriteAsync(frame).ConfigureAwait(false);
                    return;
                case WorkerFrameTypes.Metadata:
                    HandleMetadata(frame);
                    return;
                case WorkerFrameTypes.Browse:
                    HandleBrowse(frame);
                    return;
                default:
                    Send(WorkerFrame.Create(
                        WorkerFrameTypes.Fatal,
                        frame.Seq,
                        new WorkerFatal($"unexpected request '{frame.Type}'")));
                    return;
            }
        }
        catch (Exception ex)
        {
            Send(WorkerFrame.Create(
                WorkerFrameTypes.Fatal,
                frame.Seq,
                new WorkerFatal(ex.Message, ex.GetType().Name)));
        }
    }

    private async Task HandleConnectAsync(WorkerFrame frame)
    {
        WorkerConnectRequest? request = frame.PayloadAs<WorkerConnectRequest>();
        if (request is null)
        {
            Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(false, "Malformed connect request.")));
            return;
        }

        try
        {
            await ConnectSourceAsync(request.SourceId).ConfigureAwait(false);
            bool subscriptionActive = sources_.TryGetValue(request.SourceId, out WorkerSource? connected)
                && connected.Client is ISubscriptionActiveSource { IsSubscriptionActive: true };
            Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(true, SubscriptionActive: subscriptionActive)));
        }
        catch (SourceConnectionLostException ex)
        {
            Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(false, ex.Message, Transient: true)));
        }
        catch (Exception ex)
        {
            Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(false, ex.Message)));
        }
    }

    private async Task HandleDisconnectAsync(WorkerFrame frame, bool remove)
    {
        string? sourceId = remove
            ? frame.PayloadAs<WorkerRemoveSource>()?.SourceId
            : frame.PayloadAs<WorkerDisconnectRequest>()?.SourceId;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(false, "Malformed disconnect request.")));
            return;
        }

        await DisconnectSourceAsync(sourceId, remove).ConfigureAwait(false);
        Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(true)));
    }

    private async Task HandleUpsertAsync(WorkerFrame frame)
    {
        WorkerUpsertSource? request = frame.PayloadAs<WorkerUpsertSource>();
        if (request?.Config is null || string.IsNullOrWhiteSpace(request.Config.SourceId))
        {
            Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(false, "Malformed upsert request.")));
            return;
        }

        bool reconnect;
        await mutationLock_.WaitAsync().ConfigureAwait(false);
        try
        {
            if (sources_.TryGetValue(request.Config.SourceId, out WorkerSource? existing))
            {
                reconnect = existing.Client is not null;
                await DisposeClientAsync(existing).ConfigureAwait(false);
            }
            else
            {
                reconnect = false;
            }

            sources_[request.Config.SourceId] = new WorkerSource(request.Config);
        }
        finally
        {
            mutationLock_.Release();
        }

        if (reconnect)
        {
            try
            {
                await ConnectSourceAsync(request.Config.SourceId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(false, ex.Message, ex is SourceConnectionLostException)));
                return;
            }
        }

        Send(Reply(frame, WorkerFrameTypes.Ack, new WorkerAck(true)));
    }

    private async Task HandleReadAsync(WorkerFrame frame)
    {
        WorkerReadRequest? request = frame.PayloadAs<WorkerReadRequest>();
        if (request is null || !sources_.TryGetValue(request.SourceId, out WorkerSource? source) || source.Client is null)
        {
            Send(Reply(frame, WorkerFrameTypes.ReadResult, new WorkerReadResult(
                Array.Empty<WireValue>(),
                $"Source '{request?.SourceId}' is not connected.",
                Transient: true)));
            return;
        }

        IReadOnlyList<WorkerTagRef> tags = request.Tags ?? Array.Empty<WorkerTagRef>();
        List<TagMapping> mappings = tags
            .Select(tag => new TagMapping { ItemId = tag.ItemId, PollRateMs = tag.PollRateMs, DataType = tag.DataType })
            .ToList();

        try
        {
            IReadOnlyList<BridgeValue> values = await source.Client
                .ReadAsync(mappings, CancellationToken.None)
                .ConfigureAwait(false);
            Send(Reply(frame, WorkerFrameTypes.ReadResult, new WorkerReadResult(values.Select(ToWire).ToList())));
        }
        catch (SourceConnectionLostException ex)
        {
            Send(Reply(frame, WorkerFrameTypes.ReadResult, new WorkerReadResult(
                Array.Empty<WireValue>(), ex.Message, Transient: true)));
        }
        catch (OperationCanceledException)
        {
            Send(Reply(frame, WorkerFrameTypes.ReadResult, new WorkerReadResult(
                Array.Empty<WireValue>(), "Read cancelled.", Transient: true)));
        }
        catch (Exception ex)
        {
            Send(Reply(frame, WorkerFrameTypes.ReadResult, new WorkerReadResult(Array.Empty<WireValue>(), ex.Message)));
        }
    }

    private async Task HandleWriteAsync(WorkerFrame frame)
    {
        WorkerWriteRequest? request = frame.PayloadAs<WorkerWriteRequest>();
        if (request is null || !sources_.TryGetValue(request.SourceId, out WorkerSource? source) || source.Client is null)
        {
            Send(Reply(frame, WorkerFrameTypes.WriteResult, new WorkerWriteResult(
                false,
                $"Source '{request?.SourceId}' is not connected.",
                Transient: true)));
            return;
        }

        try
        {
            object? value = WireValueCodec.Decode(request.Type, request.Value);
            bool ok = await source.Client
                .WriteAsync(request.ItemId, value, CancellationToken.None)
                .ConfigureAwait(false);
            Send(Reply(frame, WorkerFrameTypes.WriteResult, new WorkerWriteResult(ok)));
        }
        catch (SourceConnectionLostException ex)
        {
            Send(Reply(frame, WorkerFrameTypes.WriteResult, new WorkerWriteResult(false, ex.Message, Transient: true)));
        }
        catch (Exception ex)
        {
            Send(Reply(frame, WorkerFrameTypes.WriteResult, new WorkerWriteResult(false, ex.Message)));
        }
    }

    private void HandleMetadata(WorkerFrame frame)
    {
        WorkerMetadataRequest? request = frame.PayloadAs<WorkerMetadataRequest>();
        if (request is null || !sources_.TryGetValue(request.SourceId, out WorkerSource? source) || source.Client is null)
        {
            Send(Reply(frame, WorkerFrameTypes.MetadataResult, new WorkerMetadataResult(false)));
            return;
        }

        bool found = source.Client.TryGetTagMetadata(request.ItemId, out short? canonicalDataType, out int? accessRights);
        Send(Reply(frame, WorkerFrameTypes.MetadataResult, new WorkerMetadataResult(found, canonicalDataType, accessRights)));
    }

    /// <summary>
    /// Browses the source's address space from this process, so the COM call carries the
    /// worker's identity — the same one the vendor stack accepts for the data path. The
    /// browse works whether or not the source is connected: it activates its own server
    /// object, like the in-process Tag Browser does.
    /// </summary>
    private void HandleBrowse(WorkerFrame frame)
    {
        WorkerBrowseRequest? request = frame.PayloadAs<WorkerBrowseRequest>();
        if (request is null
            || string.IsNullOrWhiteSpace(request.SourceId)
            || !sources_.TryGetValue(request.SourceId, out WorkerSource? source))
        {
            Send(Reply(frame, WorkerFrameTypes.BrowseResult, WorkerBrowseResult.Failed($"Unknown source '{request?.SourceId}'.")));
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Send(Reply(frame, WorkerFrameTypes.BrowseResult, WorkerBrowseResult.Failed("OPC DA browsing requires Windows.")));
            return;
        }

        try
        {
            OpcTagBrowseResult result = OpcTagBrowser.Browse(
                source.Config.ProgId,
                source.Config.Host,
                request.Path ?? string.Empty,
                request.Recursive,
                source.Config.RemoteUsername,
                source.Config.RemotePassword,
                source.Config.RemoteDomain);

            WorkerBrowseResult payload = new(
                result.Branches,
                result.Tags
                    .Select(tag => new WorkerBrowseTag(tag.Name, tag.ItemId, tag.CanonicalDataType, tag.AccessRights))
                    .ToList(),
                result.Warnings ?? Array.Empty<string>());

            // A browse result must fit one frame; letting an oversized tree hit the codec
            // would fault the writer loop and take the whole worker down with it. Fail the
            // request with a clear message instead.
            if (JsonSerializer.SerializeToUtf8Bytes(payload, WorkerProtocol.JsonOptions).Length
                > FrameCodec.MaxFrameBytes - 4096)
            {
                payload = WorkerBrowseResult.Failed(
                    $"The browse returned {result.Tags.Count} tags — too many for one worker frame; browse a narrower folder.");
            }

            Send(Reply(frame, WorkerFrameTypes.BrowseResult, payload));
        }
        catch (Exception exception)
        {
            Send(Reply(frame, WorkerFrameTypes.BrowseResult, WorkerBrowseResult.Failed(exception.Message)));
        }
    }

    private async Task ConnectSourceAsync(string sourceId)
    {
        await mutationLock_.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!sources_.TryGetValue(sourceId, out WorkerSource? source))
            {
                throw new InvalidOperationException($"Unknown source '{sourceId}'.");
            }

            if (source.Client is not null)
            {
                return;
            }

            ISourceClient client = CreateClient(source.Config);
            if (client is ISubscribableSourceClient subscribable)
            {
                subscribable.ValuesReceived += values => PushValues(source.Config.SourceId, values);
            }

            try
            {
                await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await DisposeClientSafelyAsync(client).ConfigureAwait(false);
                throw;
            }

            source.Client = client;
        }
        finally
        {
            mutationLock_.Release();
        }
    }

    private async Task DisconnectSourceAsync(string sourceId, bool remove)
    {
        await mutationLock_.WaitAsync().ConfigureAwait(false);
        try
        {
            if (sources_.TryGetValue(sourceId, out WorkerSource? source))
            {
                await DisposeClientAsync(source).ConfigureAwait(false);
                if (remove)
                {
                    sources_.TryRemove(sourceId, out _);
                }
            }
        }
        finally
        {
            mutationLock_.Release();
        }
    }

    private static ISourceClient CreateClient(WorkerSourceConfig config)
    {
        DaSourceRuntimeSettings source = ToRuntimeSettings(config);
        var snapshot = new DaRuntimeSettingsSnapshot(
            source.UpdateRateMs,
            source.UseSubscriptions,
            new[] { source },
            Version: 1);
        return new SourceClientFactory().Create(snapshot, source);
    }

    private static DaSourceRuntimeSettings ToRuntimeSettings(WorkerSourceConfig config) => new(
        SourceId: config.SourceId,
        DisplayName: config.SourceId,
        SourceType: SourceTypes.OpcDa,
        UpdateRateMs: config.UpdateRateMs,
        UseSubscriptions: config.UseSubscriptions,
        MaxMappedTags: 50000,
        OpcDa: new OpcDaSourceOptions(
            config.ProgId,
            config.Host,
            config.RemoteUsername,
            config.RemotePassword,
            config.RemoteDomain,
            config.GroupIoModes is { Count: > 0 } groups
                ? groups.Select(group => new DaGroupIoMode(group.Name, group.Rate, group.IoMode)).ToList()
                : null,
            config.WatchdogTimeoutMs),
        OpcUa: null,
        Melsec: null,
        S7200: null,
        IoMode: config.IoMode);

    private void PushValues(string sourceId, IReadOnlyList<BridgeValue> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        for (int offset = 0; offset < values.Count; offset += ValueChunkSize)
        {
            int count = Math.Min(ValueChunkSize, values.Count - offset);
            List<WireValue> chunk = new(count);
            for (int i = 0; i < count; i++)
            {
                chunk.Add(ToWire(values[offset + i]));
            }

            Send(WorkerFrame.Create(WorkerFrameTypes.Values, NextPushSequence(), new WorkerValues(sourceId, chunk)));
        }
    }

    private static WireValue ToWire(BridgeValue value)
    {
        (string type, JsonElement json) = WireValueCodec.Encode(value.Value);
        return new WireValue(value.ItemId, type, json, value.TimestampUtc, value.DaQuality, value.IsGood);
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (WorkerFrame frame in outbound_.Reader.ReadAllAsync(shutdown_.Token).ConfigureAwait(false))
            {
                await FrameCodec.WriteAsync(pipe_, frame, shutdown_.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The parent went away; the read loop sees EOF and exits with the pipe-closed code.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatIntervalMs, shutdown_.Token).ConfigureAwait(false);
                Send(WorkerFrame.Create(
                    WorkerFrameTypes.Heartbeat,
                    NextPushSequence(),
                    new WorkerHeartbeat(
                        sources_.Count,
                        (long)Stopwatch.GetElapsedTime(startedTimestamp_).TotalMilliseconds,
                        Interlocked.Read(ref pushSequence_))));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task DisposeClientAsync(WorkerSource source)
    {
        ISourceClient? client = source.Client;
        source.Client = null;
        if (client is not null)
        {
            await DisposeClientSafelyAsync(client).ConfigureAwait(false);
        }
    }

    private static async Task DisposeClientSafelyAsync(ISourceClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Teardown of a half-open connection must not kill the worker.
        }
    }

    private void Send(WorkerFrame frame) => outbound_.Writer.TryWrite(frame);

    private long NextPushSequence() => Interlocked.Increment(ref pushSequence_);

    private static WorkerFrame Reply<T>(WorkerFrame request, string type, T payload)
        => WorkerFrame.Create(type, request.Seq, payload);

    private static string CurrentAccountName()
        => OperatingSystem.IsWindows() ? CurrentWindowsAccountName() : Environment.UserName;

    [SupportedOSPlatform("windows")]
    private static string CurrentWindowsAccountName() => WindowsIdentity.GetCurrent().Name;
}
