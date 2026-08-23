using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OpcBridge.Core;
using OpcBridge.Da;

namespace OpcBridge.Ua;

public sealed class OpcUaSourceClient : ISourceClient, ISubscribableSourceClient, ISubscriptionActiveSource
{
    private const int ReadChunkSize = 500;
    private const int MonitoredItemBatchSize = 750;
    private const int NotificationFlushSize = 1000;
    private const int MaxReconnectPeriodMs = 30000;

    private readonly OpcUaSourceClientOptions options_;
    private readonly ILogger logger_;
    private readonly object gate_ = new();
    private readonly DefaultSessionFactory session_factory_ =
#pragma warning disable CS0618 // No ITelemetryContext on source client yet.
        new();
#pragma warning restore CS0618

    private ApplicationConfiguration? configuration_;
    private Session? session_;
    private sealed class SubscriptionBucket
    {
        public string Key { get; init; } = UaSubscriptionPlan.DefaultBucketKey;
        public int PublishingIntervalMs { get; set; }
        public Subscription? Subscription { get; set; }
        public HashSet<string> ItemIds { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, SubscriptionBucket> buckets_ =
        new(StringComparer.OrdinalIgnoreCase);
    private SessionReconnectHandler? reconnect_handler_;
    private IReadOnlyList<TagMapping> last_desired_mappings_ = Array.Empty<TagMapping>();
    private readonly Dictionary<string, MonitoredItem> monitored_items_ =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> node_id_by_display_ =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim reconcile_gate_ = new(1, 1);
    private const int FailedItemRetryIntervalMs = 15_000;
    private readonly HashSet<string> failed_items_ = new(StringComparer.Ordinal);
    private Timer? failed_item_retry_timer_;
    private bool subscriptions_active_;
    private bool disposed_;

    /// <summary>
    /// Raised when a UA subscription delivers values via MonitoredItems.
    /// </summary>
    public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived;

    /// <inheritdoc />
    public bool IsSubscriptionActive => subscriptions_active_;

    public OpcUaSourceClient(OpcUaSourceClientOptions options, ILogger? logger = null)
    {
        options_ = options ?? throw new ArgumentNullException(nameof(options));
        logger_ = logger ?? NullLogger.Instance;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed_, this);

        lock (gate_)
        {
            if (session_ is not null && session_.Connected)
            {
                return;
            }
        }

        string endpointUrl = options_.EndpointUrl?.Trim() ?? string.Empty;
        if (endpointUrl.Length == 0)
        {
            throw new InvalidOperationException("OPC UA EndpointUrl is empty.");
        }

        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? endpointUri)
            || !string.Equals(endpointUri.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"OPC UA EndpointUrl must be an opc.tcp URL (got '{endpointUrl}').");
        }

        MessageSecurityMode desiredMode = MapSecurityMode(options_.SecurityMode);
        string desiredPolicy = MapSecurityPolicy(options_.SecurityPolicy, desiredMode);

        try
        {
            ApplicationConfiguration configuration = await BuildConfigurationAsync(cancellationToken)
                .ConfigureAwait(false);

            EndpointDescription selected = await SelectMatchingEndpointAsync(
                    configuration,
                    endpointUrl,
                    desiredMode,
                    desiredPolicy,
                    cancellationToken)
                .ConfigureAwait(false);

            EndpointConfiguration endpointConfiguration = EndpointConfiguration.Create(configuration);
            ConfiguredEndpoint configuredEndpoint = new(null, selected, endpointConfiguration);

            IUserIdentity identity = CreateUserIdentity(options_.Username, options_.Password);
            uint sessionTimeout = (uint)Math.Max(1000, options_.SessionTimeoutMs);
            string sessionName = $"{options_.ApplicationName}:{options_.SourceId}";

            ISession created = await session_factory_.CreateAsync(
                    configuration,
                    configuredEndpoint,
                    updateBeforeConnect: false,
                    sessionName: sessionName,
                    sessionTimeout: sessionTimeout,
                    identity: identity,
                    preferredLocales: null,
                    ct: cancellationToken)
                .ConfigureAwait(false);

            if (created is not Session session)
            {
                await SafeCloseAndDisposeAsync(created).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"OPC UA session factory returned unexpected type '{created.GetType().FullName}'.");
            }

            Session? previous;
            lock (gate_)
            {
                if (disposed_)
                {
                    // Client was disposed while connecting — never store the new session.
                    previous = null;
                }
                else
                {
                    previous = session_;
                    session_ = session;
                    configuration_ = configuration;
                    // New session owns its own subscriptions; drop local bookkeeping for the old one.
                    ResetBucketBookkeepingLocked();
                    session.KeepAlive += OnSessionKeepAlive;
                    session = null!; // ownership transferred
                }
            }

            if (previous is not null)
            {
                previous.KeepAlive -= OnSessionKeepAlive;
                await SafeCloseAndDisposeAsync(previous).ConfigureAwait(false);
            }

            if (session is not null)
            {
                // disposed_ was true under lock: close the just-created session and fail.
                await SafeCloseAndDisposeAsync(session).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(OpcUaSourceClient));
            }

            logger_.LogInformation(
                "OPC UA source {SourceId} connected to {EndpointUrl} ({SecurityMode}/{SecurityPolicy})",
                options_.SourceId,
                selected.EndpointUrl,
                selected.SecurityMode,
                selected.SecurityPolicyUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            // Connection-level failures (server unreachable, session/channel errors) are
            // transient: the coordinator treats SourceConnectionLostException as retryable
            // and reconnects with backoff instead of marking the source Faulted forever.
            throw new SourceConnectionLostException(
                $"Failed to connect OPC UA source '{options_.SourceId}' to '{endpointUrl}': {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Session-level keepalive handler (canonical OPC Foundation pattern). When the server
    /// stops responding, <see cref="SessionReconnectHandler"/> re-activates the session or
    /// re-creates it with jittered exponential backoff. This recovers dropped connections
    /// in seconds without coordinator involvement; the bridge watchdog is the outer backstop.
    /// </summary>
    private void OnSessionKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (!ServiceResult.IsBad(e.Status))
        {
            return;
        }

        lock (gate_)
        {
            if (disposed_)
            {
                return;
            }

            // Ignore events from discarded sessions (e.g. an old session being disposed).
            if (!ReferenceEquals(session, session_) || session_ is null || !session_.Connected)
            {
                return;
            }

            if (reconnect_handler_ is not null)
            {
                return; // reconnect already in progress
            }

            logger_.LogWarning(
                "OPC UA source {SourceId} keepalive lost ({Status}); reconnecting to {EndpointUrl}",
                options_.SourceId,
                e.Status,
                options_.EndpointUrl);

#pragma warning disable CS0618 // Telemetry-less ctor is obsolete but fine for the source client.
            reconnect_handler_ = new SessionReconnectHandler(
                reconnectAbort: true,
                maxReconnectPeriod: MaxReconnectPeriodMs);
#pragma warning restore CS0618

            reconnect_handler_.BeginReconnect(
                session_,
                Math.Max(SessionReconnectHandler.MinReconnectPeriod, options_.ReconnectDelayMs),
                OnReconnectComplete);

            // Cancel sending a new keepalive request because a reconnect is triggered.
            e.CancelKeepAlive = true;
        }
    }

    /// <summary>
    /// Fired by <see cref="SessionReconnectHandler"/> after a successful reconnect.
    /// A re-activated session keeps its subscriptions; a re-created session must be
    /// adopted and its MonitoredItems re-established.
    /// </summary>
    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        Session? recreated = null;
        bool recreatedAdopted = false;

        lock (gate_)
        {
            if (!ReferenceEquals(sender, reconnect_handler_) || reconnect_handler_ is null)
            {
                return; // callback from a discarded handler
            }

            reconnect_handler_.Dispose();
            reconnect_handler_ = null;

            if (disposed_)
            {
                return;
            }

            Session? handlerSession = (sender as SessionReconnectHandler)?.Session as Session;
            if (handlerSession is not null && !ReferenceEquals(handlerSession, session_))
            {
                // Session was re-created server-side: adopt the new session and reset
                // subscription bookkeeping; MonitoredItems are re-established below.
                if (session_ is not null)
                {
                    session_.KeepAlive -= OnSessionKeepAlive;
                }

                session_ = handlerSession;
                handlerSession.KeepAlive += OnSessionKeepAlive;
                ResetBucketBookkeepingLocked();
                recreated = handlerSession;
                recreatedAdopted = true;
                logger_.LogInformation(
                    "OPC UA source {SourceId} reconnected with new session {SessionId}",
                    options_.SourceId,
                    handlerSession.SessionId);
            }
            else
            {
                logger_.LogInformation(
                    "OPC UA source {SourceId} session re-activated after reconnect",
                    options_.SourceId);
            }
        }

        if (recreatedAdopted && recreated is not null)
        {
            // Re-establish the subscription on the new session. Best-effort, detached from
            // the caller; failures are logged (the watchdog/coordinator will retry the source).
            _ = ReconcileMonitoredItemsAsync(last_desired_mappings_, CancellationToken.None)
                .ContinueWith(
                    task => logger_.LogError(task.Exception, "Reconnect reconcile failed for source {SourceId}", options_.SourceId),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private void ResetBucketBookkeepingLocked()
    {
        // Called under gate_: drop all bucket state; the SDK-side subscriptions die with the old session.
        buckets_.Clear();
        monitored_items_.Clear();
        node_id_by_display_.Clear();
        subscriptions_active_ = false;
    }

    public async Task<IReadOnlyList<BridgeValue>> ReadAsync(
        IReadOnlyList<TagMapping> mappings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session session = GetConnectedSession();

        // When subscriptions are live, values arrive via ValuesReceived; poller stays as
        // health/keepalive but should not re-read the full mapped set each tick.
        if (subscriptions_active_)
        {
            return Array.Empty<BridgeValue>();
        }

        if (mappings.Count == 0)
        {
            return Array.Empty<BridgeValue>();
        }

        List<BridgeValue> results = new(mappings.Count);
        for (int offset = 0; offset < mappings.Count; offset += ReadChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(ReadChunkSize, mappings.Count - offset);
            ReadValueIdCollection nodesToRead = new(count);
            List<TagMapping> chunkMappings = new(count);

            for (int i = 0; i < count; i++)
            {
                TagMapping mapping = mappings[offset + i];
                if (string.IsNullOrWhiteSpace(mapping.ItemId))
                {
                    continue;
                }

                if (!NodeId.TryParse(mapping.ItemId.Trim(), out NodeId? nodeId) || nodeId is null)
                {
                    results.Add(new BridgeValue(
                        options_.SourceId,
                        mapping.ItemId,
                        null,
                        DateTime.UtcNow,
                        0x00,
                        false));
                    continue;
                }

                nodesToRead.Add(new ReadValueId
                {
                    NodeId = nodeId,
                    AttributeId = Attributes.Value
                });
                chunkMappings.Add(mapping);
            }

            if (nodesToRead.Count == 0)
            {
                continue;
            }

            ReadResponse response = await session.ReadAsync(
                    requestHeader: null,
                    maxAge: 0,
                    timestampsToReturn: TimestampsToReturn.Both,
                    nodesToRead: nodesToRead,
                    ct: cancellationToken)
                .ConfigureAwait(false);

            DataValueCollection? values = response.Results;
            for (int i = 0; i < chunkMappings.Count; i++)
            {
                TagMapping mapping = chunkMappings[i];
                DataValue dataValue = values is not null && i < values.Count
                    ? values[i]
                    : new DataValue(StatusCodes.BadUnexpectedError);

                results.Add(ToBridgeValue(mapping.ItemId, dataValue));
            }
        }

        return results;
    }

    public async Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        Session session = GetConnectedSession();
        if (!NodeId.TryParse(itemId.Trim(), out NodeId? nodeId) || nodeId is null)
        {
            return false;
        }

        WriteValue writeValue = new()
        {
            NodeId = nodeId,
            AttributeId = Attributes.Value,
            Value = new DataValue
            {
                // Value-only write (no SourceTimestamp) so servers that reject
                // timestamped writes accept the request.
                Value = value,
                StatusCode = StatusCodes.Good
            }
        };

        WriteResponse response = await session.WriteAsync(
                requestHeader: null,
                nodesToWrite: new WriteValueCollection { writeValue },
                ct: cancellationToken)
            .ConfigureAwait(false);

        if (response.Results is null || response.Results.Count == 0)
        {
            return false;
        }

        return StatusCode.IsGood(response.Results[0]);
    }

    public bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights)
    {
        canonicalDataType = null;
        accessRights = null;

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        Session? session;
        lock (gate_)
        {
            session = session_;
            if (session is null || !session.Connected)
            {
                return false;
            }
        }

        if (!NodeId.TryParse(itemId.Trim(), out NodeId? nodeId) || nodeId is null)
        {
            return false;
        }

        try
        {
            ReadValueIdCollection nodesToRead = new()
            {
                new ReadValueId { NodeId = nodeId, AttributeId = Attributes.DataType },
                new ReadValueId { NodeId = nodeId, AttributeId = Attributes.AccessLevel }
            };

            // Sync interface method: block on async read with a short timeout.
            ReadResponse response = session.ReadAsync(
                    requestHeader: null,
                    maxAge: 0,
                    timestampsToReturn: TimestampsToReturn.Neither,
                    nodesToRead: nodesToRead,
                    ct: CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            DataValueCollection? results = response.Results;
            if (results is null || results.Count < 2)
            {
                return false;
            }

            if (StatusCode.IsGood(results[0].StatusCode) && results[0].Value is NodeId dataTypeId)
            {
                canonicalDataType = MapUaDataTypeToCanonical(dataTypeId);
            }

            if (StatusCode.IsGood(results[1].StatusCode) && results[1].Value is not null)
            {
                try
                {
                    // OPC UA AccessLevel bits: CurrentRead=1, CurrentWrite=2 → DA-like 1=read, 2=write, 3=rw
                    byte accessLevel = Convert.ToByte(results[1].Value);
                    int rights = 0;
                    if ((accessLevel & AccessLevels.CurrentRead) != 0)
                    {
                        rights |= 1;
                    }

                    if ((accessLevel & AccessLevels.CurrentWrite) != 0)
                    {
                        rights |= 2;
                    }

                    accessRights = rights;
                }
                catch
                {
                    // leave accessRights null
                }
            }

            return canonicalDataType.HasValue || accessRights.HasValue;
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "TryGetTagMetadata failed for {NodeId}", itemId);
            return false;
        }
    }

    /// <summary>
    /// Ensure MonitoredItems match the desired mapped set (enabled, non-Manual, non-empty NodeId).
    /// On failure keeps the session and falls back to poll (<see cref="subscriptions_active_"/> false).
    /// </summary>
    public async Task ReconcileMonitoredItemsAsync(
        IReadOnlyList<TagMapping>? desiredMappings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed_, this);

        // Null means "no desired items"; retained for self-recovery after a session-level reconnect.
        IReadOnlyList<TagMapping> mappings = desiredMappings ?? Array.Empty<TagMapping>();
        last_desired_mappings_ = mappings;

        // Reconciles can be fired concurrently: the BridgeWorker mapping-change loop, the
        // connect path, and the detached session-reconnect recovery (OnReconnectComplete).
        // Unsynchronized diffs against monitored_items_ and the live Subscription can leave
        // stale MonitoredItems behind (e.g. a tag that flipped to Write stays subscribed).
        await reconcile_gate_.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileMonitoredItemsCoreAsync(mappings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            reconcile_gate_.Release();
        }
    }

    private async Task ReconcileMonitoredItemsCoreAsync(
        IReadOnlyList<TagMapping> desiredMappings,
        CancellationToken cancellationToken)
    {
        if (!options_.UseSubscriptions)
        {
            await TearDownAllBucketsAsync(keepSession: true).ConfigureAwait(false);
            return;
        }

        Session? session;
        lock (gate_)
        {
            session = session_;
            if (session is null || !session.Connected)
            {
                subscriptions_active_ = false;
                return;
            }
        }

        try
        {
            int defaultSampling = Math.Max(100, options_.UpdateRateMs);
            Dictionary<string, Dictionary<string, int>> plan =
                UaSubscriptionPlan.GroupByBucket(desiredMappings, options_.Subscriptions, defaultSampling);

            lock (gate_)
            {
                if (failed_items_.Count > 0)
                {
                    IEnumerable<string> desiredIds = plan.Values.SelectMany(b => b.Keys);
                    failed_items_.RemoveWhere(id => !desiredIds.Contains(id));
                    if (failed_items_.Count == 0)
                    {
                        failed_item_retry_timer_?.Dispose();
                        failed_item_retry_timer_ = null;
                    }
                }
            }

            int totalDesired = plan.Values.Sum(b => b.Count);

            // Buckets that are no longer defined/desired go away entirely.
            List<string> staleBuckets;
            lock (gate_)
            {
                staleBuckets = buckets_.Keys.Where(k => !plan.ContainsKey(k)).ToList();
            }
            foreach (string staleKey in staleBuckets)
            {
                await RemoveBucketAsync(session, staleKey, keepSession: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (KeyValuePair<string, Dictionary<string, int>> bucketPlan in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReconcileBucketAsync(
                    session,
                    bucketPlan.Key,
                    bucketPlan.Value,
                    defaultSampling,
                    cancellationToken).ConfigureAwait(false);
            }

            lock (gate_)
            {
                bool allCreated = buckets_.Values.All(b => b.Subscription is { Created: true });
                subscriptions_active_ = totalDesired > 0 && allCreated && monitored_items_.Count > 0;
            }

            logger_.LogInformation(
                "OPC UA source {SourceId} subscription reconcile: desired={Desired} active={Active} buckets=[{Buckets}]",
                options_.SourceId,
                totalDesired,
                monitored_items_.Count,
                string.Join(", ", plan.Select(kv =>
                    $"{(kv.Key.Length == 0 ? "default" : kv.Key)}:{kv.Value.Count}@{kv.Value.Values.DefaultIfEmpty(defaultSampling).Min()}ms")));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger_.LogWarning(
                ex,
                "OPC UA source {SourceId} subscription reconcile failed; falling back to poll",
                options_.SourceId);
            await TearDownAllBucketsAsync(keepSession: true).ConfigureAwait(false);
        }
    }

    private async Task ReconcileBucketAsync(
        Session session,
        string bucketKey,
        Dictionary<string, int> desiredSampling,
        int defaultSampling,
        CancellationToken cancellationToken)
    {
        SubscriptionBucket bucket;
        lock (gate_)
        {
            if (!buckets_.TryGetValue(bucketKey, out SubscriptionBucket? found))
            {
                found = new SubscriptionBucket { Key = bucketKey };
                buckets_[bucketKey] = found;
            }
            bucket = found;
        }

        Subscription subscription = await EnsureBucketSubscriptionAsync(
                session, bucket, desiredSampling.Values.DefaultIfEmpty(defaultSampling).Min(),
                cancellationToken)
            .ConfigureAwait(false);

        string[] activeIds;
        lock (gate_)
        {
            activeIds = bucket.ItemIds.ToArray();
        }

        (IReadOnlyList<string> toAdd, IReadOnlyList<string> toRemove) =
            MonitoredItemReconcile.Diff(desiredSampling.Keys, activeIds);

        for (int offset = 0; offset < toRemove.Count; offset += MonitoredItemBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(MonitoredItemBatchSize, toRemove.Count - offset);
            List<MonitoredItem> batch = new(count);
            lock (gate_)
            {
                for (int i = 0; i < count; i++)
                {
                    string nodeId = toRemove[offset + i];
                    if (monitored_items_.Remove(nodeId, out MonitoredItem? item))
                    {
                        node_id_by_display_.Remove(item.DisplayName);
                        item.Notification -= OnMonitoredItemNotification;
                        batch.Add(item);
                    }
                    bucket.ItemIds.Remove(nodeId);
                }
            }

            if (batch.Count == 0)
            {
                continue;
            }

            subscription.RemoveItems(batch);
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        for (int offset = 0; offset < toAdd.Count; offset += MonitoredItemBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(MonitoredItemBatchSize, toAdd.Count - offset);
            List<MonitoredItem> batch = new(count);

            for (int i = 0; i < count; i++)
            {
                string nodeIdString = toAdd[offset + i];
                if (!NodeId.TryParse(nodeIdString, out NodeId? nodeId) || nodeId is null)
                {
                    logger_.LogDebug(
                        "Skipping invalid NodeId for UA subscription on {SourceId}: {NodeId}",
                        options_.SourceId, nodeIdString);
                    continue;
                }

                int sampling = desiredSampling.TryGetValue(nodeIdString, out int s)
                    ? s
                    : defaultSampling;

#pragma warning disable CS0618
                MonitoredItem item = new()
                {
                    StartNodeId = nodeId,
                    AttributeId = Attributes.Value,
                    DisplayName = nodeIdString,
                    SamplingInterval = sampling,
                    QueueSize = 1,
                    DiscardOldest = true,
                    MonitoringMode = MonitoringMode.Reporting,
                    Handle = nodeIdString
                };
#pragma warning restore CS0618
                item.Notification += OnMonitoredItemNotification;
                batch.Add(item);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            subscription.AddItems(batch);
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);

            lock (gate_)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    MonitoredItem item = batch[i];
                    string key = item.Handle as string ?? item.DisplayName;
                    ServiceResult? createError = item.Status.Error;
                    if (!item.Status.Created
                        || (createError is not null && StatusCode.IsBad(createError.StatusCode)))
                    {
                        logger_.LogDebug(
                            "MonitoredItem create failed for {SourceId} {NodeId}: created={Created} status={Status}",
                            options_.SourceId, key, item.Status.Created, createError?.StatusCode);
                        item.Notification -= OnMonitoredItemNotification;
                        subscription.RemoveItem(item);
                        NoteItemCreateFailure(key);
                        continue;
                    }

                    monitored_items_[key] = item;
                    node_id_by_display_[item.DisplayName] = key;
                    bucket.ItemIds.Add(key);
                    failed_items_.Remove(key);
                }
            }

            if (subscription.ChangesPending)
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Rate-only changes on surviving members (per-tag overrides in the default bucket).
        bool samplingChanged = false;
        lock (gate_)
        {
            foreach (KeyValuePair<string, int> kv in desiredSampling)
            {
                if (monitored_items_.TryGetValue(kv.Key, out MonitoredItem? item))
                {
                    int desired = kv.Value > 0 ? kv.Value : defaultSampling;
                    if (item.SamplingInterval != desired)
                    {
                        item.SamplingInterval = desired;
                        samplingChanged = true;
                    }
                }
            }
        }

        if (samplingChanged)
        {
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Track a monitored-item create failure so it can be retried periodically. Tags that
    /// do not exist at the source yet (or transiently failed) are re-attempted on a timer;
    /// without this they would stay disconnected until the next mapping change or reconnect.
    /// </summary>
    private void NoteItemCreateFailure(string nodeId)
    {
        lock (gate_)
        {
            if (failed_items_.Add(nodeId) && failed_item_retry_timer_ is null)
            {
                failed_item_retry_timer_ = new Timer(
                    _ => RetryFailedItems(),
                    null,
                    FailedItemRetryIntervalMs,
                    FailedItemRetryIntervalMs);
            }
        }
    }

    private void RetryFailedItems()
    {
        bool any;
        lock (gate_)
        {
            any = failed_items_.Count > 0;
        }

        if (!any || disposed_)
        {
            return;
        }

        // Serialized with other reconciles by reconcile_gate_; a success clears the item
        // from failed_items_ and the timer stops itself on the next reconcile/purge.
        _ = ReconcileMonitoredItemsAsync(last_desired_mappings_, CancellationToken.None)
            .ContinueWith(
                task => logger_.LogError(task.Exception, "Failed-item retry reconcile failed for source {SourceId}", options_.SourceId),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>True when MonitoredItems are delivering values (poll ReadAsync is a no-op).</summary>
    public bool SubscriptionsActive => subscriptions_active_;

    /// <summary>Snapshot of node ids whose monitored-item create failed and are being retried.</summary>
    public IReadOnlyList<string> GetFailedItemIds()
    {
        lock (gate_)
        {
            return failed_items_.ToArray();
        }
    }

    /// <summary>Live per-bucket snapshot for the dashboard (requested vs server-revised interval).</summary>
    public IReadOnlyList<UaSubscriptionStatus> GetSubscriptionsStatus()
    {
        List<UaSubscriptionStatus> statuses = new();
        lock (gate_)
        {
            foreach (SubscriptionBucket bucket in buckets_.Values.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
            {
                Subscription? sub = bucket.Subscription;
                statuses.Add(new UaSubscriptionStatus(
                    bucket.Key,
                    bucket.PublishingIntervalMs,
                    sub?.CurrentPublishingInterval ?? 0,
                    bucket.ItemIds.Count,
                    sub?.Created ?? false));
            }
        }
        return statuses;
    }

    public async ValueTask DisposeAsync()
    {
        Session? session;
        lock (gate_)
        {
            if (disposed_)
            {
                return;
            }

            disposed_ = true;
            session = session_;
            session_ = null;
            configuration_ = null;
            if (session is not null)
            {
                session.KeepAlive -= OnSessionKeepAlive;
            }

            reconnect_handler_?.Dispose();
            reconnect_handler_ = null;
        }

        await TearDownAllBucketsAsync(keepSession: false).ConfigureAwait(false);

        failed_item_retry_timer_?.Dispose();
        failed_item_retry_timer_ = null;
        reconcile_gate_.Dispose();

        if (session is null)
        {
            return;
        }

        await SafeCloseAndDisposeAsync(session).ConfigureAwait(false);

    }

    private async Task<Subscription> EnsureBucketSubscriptionAsync(
        Session session,
        SubscriptionBucket bucket,
        int publishingIntervalMs,
        CancellationToken cancellationToken)
    {
        int publishing = Math.Max(100, publishingIntervalMs);

        lock (gate_)
        {
            Subscription? current = bucket.Subscription;
            if (current is not null
                && ReferenceEquals(current.Session, session)
                && current.Created
                && bucket.PublishingIntervalMs == publishing)
            {
                return current;
            }
        }

        // Servers don't reliably apply a publishing-interval change to a live subscription,
        // so recreate just this bucket — the caller's reconcile re-adds its monitored items.
        await RemoveBucketAsync(session, bucket.Key, keepSession: true, cancellationToken)
            .ConfigureAwait(false);

#pragma warning disable CS0618
        Subscription subscription = new()
        {
            DisplayName = bucket.Key.Length == 0
                ? $"OpcBridge_{options_.SourceId}"
                : $"OpcBridge_{options_.SourceId}_{bucket.Key}",
            PublishingEnabled = true,
            PublishingInterval = publishing,
            KeepAliveCount = 10,
            LifetimeCount = 1000,
            MaxNotificationsPerPublish = 0,
            TimestampsToReturn = TimestampsToReturn.Both,
            Priority = 0
        };
#pragma warning restore CS0618

        subscription.FastDataChangeCallback = OnFastDataChange;

        if (!session.AddSubscription(subscription))
        {
            try { subscription.FastDataChangeCallback = null; } catch { }
            subscription.Dispose();
            throw new InvalidOperationException(
                $"Failed to add OPC UA subscription '{bucket.Key}' for source '{options_.SourceId}'.");
        }

        try
        {
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
            if (!subscription.Created)
            {
                throw new InvalidOperationException(
                    $"OPC UA subscription create failed for source '{options_.SourceId}' bucket '{bucket.Key}'.");
            }
        }
        catch
        {
            await DiscardUnownedSubscriptionAsync(session, subscription).ConfigureAwait(false);
            throw;
        }

        lock (gate_)
        {
            bucket.Subscription = subscription;
            bucket.PublishingIntervalMs = publishing;
        }

        return subscription;
    }

    /// <summary>
    /// Delete/Remove/Dispose a subscription that was added to the session but never stored in a bucket.
    /// </summary>
    private async Task DiscardUnownedSubscriptionAsync(Session session, Subscription subscription)
    {
        try
        {
            subscription.FastDataChangeCallback = null;
        }
        catch
        {
            // ignore
        }

        try
        {
            if (subscription.Created)
            {
                await subscription.DeleteAsync(silent: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger_.LogDebug(
                ex,
                "Error deleting unowned OPC UA subscription for source {SourceId}",
                options_.SourceId);
        }

        try
        {
            await session.RemoveSubscriptionAsync(subscription, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ignore remove races
        }

        try
        {
            subscription.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private async Task TearDownAllBucketsAsync(bool keepSession)
    {
        List<string> keys;
        lock (gate_)
        {
            keys = buckets_.Keys.ToList();
        }

        foreach (string key in keys)
        {
            await RemoveBucketAsync(GetSessionIfAny(), key, keepSession, CancellationToken.None)
                .ConfigureAwait(false);
        }

        List<MonitoredItem> orphans;
        lock (gate_)
        {
            orphans = monitored_items_.Values.ToList();
            monitored_items_.Clear();
            node_id_by_display_.Clear();
            subscriptions_active_ = false;
        }

        for (int i = 0; i < orphans.Count; i++)
        {
            try { orphans[i].Notification -= OnMonitoredItemNotification; } catch { }
        }
    }

    private Session? GetSessionIfAny()
    {
        lock (gate_)
        {
            return session_;
        }
    }

    private async Task RemoveBucketAsync(Session? session, string bucketKey, bool keepSession, CancellationToken cancellationToken)
    {
        SubscriptionBucket bucket;
        lock (gate_)
        {
            if (!buckets_.Remove(bucketKey, out SubscriptionBucket? found))
            {
                return;
            }
            bucket = found;

            foreach (string nodeId in bucket.ItemIds)
            {
                if (monitored_items_.Remove(nodeId, out MonitoredItem? item))
                {
                    node_id_by_display_.Remove(item.DisplayName);
                    try { item.Notification -= OnMonitoredItemNotification; } catch { }
                }
            }
            bucket.ItemIds.Clear();

            if (monitored_items_.Count == 0)
            {
                subscriptions_active_ = false;
            }
        }

        Subscription? subscription = bucket.Subscription;
        bucket.Subscription = null;
        if (subscription is null)
        {
            return;
        }

        try { subscription.FastDataChangeCallback = null; } catch { }

        try
        {
            if (subscription.Session is not null && subscription.Created)
            {
                await subscription.DeleteAsync(silent: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "Error deleting OPC UA subscription '{Bucket}' for source {SourceId}",
                bucketKey, options_.SourceId);
        }

        try
        {
            if (session is not null && subscription.Session is ISession s && keepSession)
            {
                await s.RemoveSubscriptionAsync(subscription, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore remove races on dispose
        }

        try { subscription.Dispose(); } catch { }
    }

    private void OnFastDataChange(
        Subscription subscription,
        DataChangeNotification notification,
        IList<string> stringTable)
    {
        _ = stringTable;
        if (notification?.MonitoredItems is null || notification.MonitoredItems.Count == 0)
        {
            return;
        }

        List<BridgeValue> batch = new(Math.Min(notification.MonitoredItems.Count, NotificationFlushSize));
        for (int i = 0; i < notification.MonitoredItems.Count; i++)
        {
            MonitoredItemNotification change = notification.MonitoredItems[i];
            MonitoredItem? item = subscription.FindItemByClientHandle(change.ClientHandle);
            string? itemId = ResolveMonitoredItemId(item);
            if (itemId is null)
            {
                continue;
            }

            DataValue dataValue = change.Value ?? new DataValue(StatusCodes.BadNoData);
            batch.Add(ToBridgeValue(itemId, dataValue));

            if (batch.Count >= NotificationFlushSize)
            {
                RaiseValuesReceived(batch);
                batch = new List<BridgeValue>(NotificationFlushSize);
            }
        }

        if (batch.Count > 0)
        {
            RaiseValuesReceived(batch);
        }
    }

    private void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        // FastDataChangeCallback is preferred; per-item handler is a fallback if Fast path is unset.
        lock (gate_)
        {
            if (buckets_.Values.Any(b => b.Subscription?.FastDataChangeCallback is not null))
            {
                return;
            }
        }

        string? itemId = ResolveMonitoredItemId(item);
        if (itemId is null || e.NotificationValue is not MonitoredItemNotification change)
        {
            return;
        }

        DataValue dataValue = change.Value ?? new DataValue(StatusCodes.BadNoData);
        RaiseValuesReceived(new List<BridgeValue>(1) { ToBridgeValue(itemId, dataValue) });
    }

    private string? ResolveMonitoredItemId(MonitoredItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.Handle is string handle && handle.Length > 0)
        {
            return handle;
        }

        if (!string.IsNullOrEmpty(item.DisplayName))
        {
            lock (gate_)
            {
                if (node_id_by_display_.TryGetValue(item.DisplayName, out string? id))
                {
                    return id;
                }
            }

            return item.DisplayName;
        }

        return null;
    }

    private void RaiseValuesReceived(IReadOnlyList<BridgeValue> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        try
        {
            ValuesReceived?.Invoke(values);
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "ValuesReceived handler failed for source {SourceId}", options_.SourceId);
        }
    }



    private async Task SafeCloseAndDisposeAsync(IDisposable session)
    {
        try
        {
            if (session is Session s)
            {
                try
                {
                    await s.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger_.LogDebug(ex, "Error closing OPC UA session for source {SourceId}", options_.SourceId);
                }
            }
            else if (session is ISession isession)
            {
                try
                {
                    await isession.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger_.LogDebug(ex, "Error closing OPC UA session for source {SourceId}", options_.SourceId);
                }
            }
        }
        finally
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // ignore dispose races
            }
        }
    }

    private Session GetConnectedSession()
    {
        ObjectDisposedException.ThrowIf(disposed_, this);
        lock (gate_)
        {
            if (session_ is null || !session_.Connected)
            {
                throw new InvalidOperationException(
                    $"OPC UA source '{options_.SourceId}' is not connected.");
            }

            return session_;
        }
    }

    private BridgeValue ToBridgeValue(string itemId, DataValue dataValue)
    {
        (int daQuality, bool isGood) = UaQualityMapper.FromStatusCode(dataValue.StatusCode.Code);
        DateTime timestamp = ResolveTimestamp(dataValue);
        return new BridgeValue(options_.SourceId, itemId, dataValue.Value, timestamp, daQuality, isGood);
    }

    private static DateTime ResolveTimestamp(DataValue dataValue)
    {
        if (dataValue.SourceTimestamp != DateTime.MinValue)
        {
            return DateTime.SpecifyKind(dataValue.SourceTimestamp, DateTimeKind.Utc);
        }

        if (dataValue.ServerTimestamp != DateTime.MinValue)
        {
            return DateTime.SpecifyKind(dataValue.ServerTimestamp, DateTimeKind.Utc);
        }

        return DateTime.UtcNow;
    }

    private async Task<ApplicationConfiguration> BuildConfigurationAsync(CancellationToken cancellationToken)
    {
        string pkiRoot = Path.Combine(AppContext.BaseDirectory, options_.PkiRoot);
        string applicationName = string.IsNullOrWhiteSpace(options_.ApplicationName)
            ? "OpcBridge.UaClient"
            : options_.ApplicationName.Trim();
        // ApplicationUri must stay stable across sources so the shared
        // pki/ua-client application certificate remains valid.
        string applicationUri = $"urn:ohmypi:{applicationName}";

        ApplicationConfiguration configuration = new()
        {
            ApplicationName = applicationName,
            ApplicationUri = applicationUri,
            ProductUri = "urn:ohmypi:opc-bridge-client",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = applicationName
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "trusted")
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "issuers")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "rejected")
                },
                AutoAcceptUntrustedCertificates = options_.AutoAcceptUntrustedCertificates,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048,
                AddAppCertToTrustedStore = true
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = Math.Max(5000, options_.SessionTimeoutMs),
                MaxStringLength = 1048576,
                MaxByteStringLength = 1048576,
                MaxArrayLength = 65535,
                MaxMessageSize = 4194304,
                MaxBufferSize = 65535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = Math.Max(1000, options_.SessionTimeoutMs)
            },
            TraceConfiguration = new TraceConfiguration()
        };

        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken).ConfigureAwait(false);

        if (options_.AutoAcceptUntrustedCertificates)
        {
            configuration.CertificateValidator.CertificateValidation += (_, e) =>
            {
                e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted
                    || e.Error.StatusCode == StatusCodes.BadCertificateChainIncomplete
                    || e.Error.StatusCode == StatusCodes.BadCertificateTimeInvalid
                    || e.Error.StatusCode == StatusCodes.BadCertificateHostNameInvalid;
            };
        }

        // Match server host pattern: ApplicationInstance without telemetry is obsolete but acceptable
        // when no ITelemetryContext is injected into the source client yet.
#pragma warning disable CS0618
        ApplicationInstance application = new()
        {
            ApplicationName = applicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = configuration
        };
#pragma warning restore CS0618

        bool certificateOk = await application
            .CheckApplicationInstanceCertificatesAsync(silent: true, ct: cancellationToken)
            .ConfigureAwait(false);
        if (!certificateOk)
        {
            throw new InvalidOperationException(
                $"OPC UA client application certificate is invalid under '{pkiRoot}'.");
        }

        return configuration;
    }

    private static async Task<EndpointDescription> SelectMatchingEndpointAsync(
        ApplicationConfiguration configuration,
        string endpointUrl,
        MessageSecurityMode desiredMode,
        string desiredPolicyUri,
        CancellationToken cancellationToken)
    {
        // Prefer discovery so we can match security mode + policy exactly.
        try
        {
            using DiscoveryClient discovery = await DiscoveryClient.CreateAsync(
                    configuration,
                    new Uri(endpointUrl),
                    DiagnosticsMasks.None,
                    cancellationToken)
                .ConfigureAwait(false);

            EndpointDescriptionCollection endpoints = await discovery
                .GetEndpointsAsync(profileUris: null, ct: cancellationToken)
                .ConfigureAwait(false);

            EndpointDescription? match = endpoints
                .Where(e => e.SecurityMode == desiredMode
                    && string.Equals(e.SecurityPolicyUri, desiredPolicyUri, StringComparison.Ordinal))
                .OrderByDescending(e => string.Equals(
                    e.TransportProfileUri,
                    Profiles.UaTcpTransport,
                    StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (match is not null)
            {
                return PreferConfiguredUrl(match, endpointUrl);
            }

            string available = string.Join(
                ", ",
                endpoints.Select(e => $"{e.SecurityMode}/{ShortPolicy(e.SecurityPolicyUri)}"));
            throw new InvalidOperationException(
                $"No endpoint at '{endpointUrl}' matches security {desiredMode}/{ShortPolicy(desiredPolicyUri)}. Available: {available}");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fall back to stack helper (security on/off only).
            bool useSecurity = desiredMode != MessageSecurityMode.None;
            try
            {
#pragma warning disable CS0618 // No ITelemetryContext on source client yet.
                EndpointDescription selected = await CoreClientUtils.SelectEndpointAsync(
                        configuration,
                        endpointUrl,
                        useSecurity,
                        discoverTimeout: Math.Max(5000, configuration.TransportQuotas.OperationTimeout),
                        ct: cancellationToken)
                    .ConfigureAwait(false);
#pragma warning restore CS0618

                if (selected.SecurityMode != desiredMode
                    || !string.Equals(selected.SecurityPolicyUri, desiredPolicyUri, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Discovered endpoint security {selected.SecurityMode}/{ShortPolicy(selected.SecurityPolicyUri)} " +
                        $"does not match requested {desiredMode}/{ShortPolicy(desiredPolicyUri)}.",
                        ex);
                }

                return PreferConfiguredUrl(selected, endpointUrl);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception fallbackEx)
            {
                // Discovery reached the server but endpoint selection failed, or the server
                // is unreachable — both are transport-level and retryable.
                throw new SourceConnectionLostException(
                    $"Endpoint discovery failed for '{endpointUrl}': {fallbackEx.Message}",
                    fallbackEx);
            }
        }
    }

    private static EndpointDescription PreferConfiguredUrl(EndpointDescription selected, string configuredUrl)
    {
        if (string.Equals(selected.EndpointUrl, configuredUrl, StringComparison.OrdinalIgnoreCase))
        {
            return selected;
        }

        EndpointDescription copy = Utils.Clone(selected);
        copy.EndpointUrl = configuredUrl;
        return copy;
    }

    private static IUserIdentity CreateUserIdentity(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return new UserIdentity();
        }

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        return new UserIdentity(username.Trim(), passwordBytes);
    }

    private static MessageSecurityMode MapSecurityMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)
            || mode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return MessageSecurityMode.None;
        }

        if (mode.Equals("Sign", StringComparison.OrdinalIgnoreCase))
        {
            return MessageSecurityMode.Sign;
        }

        if (mode.Equals("SignAndEncrypt", StringComparison.OrdinalIgnoreCase))
        {
            return MessageSecurityMode.SignAndEncrypt;
        }

        throw new InvalidOperationException(
            $"Unsupported OPC UA SecurityMode '{mode}'. Use None, Sign, or SignAndEncrypt.");
    }

    private static string MapSecurityPolicy(string? policy, MessageSecurityMode mode)
    {
        if (mode == MessageSecurityMode.None)
        {
            return SecurityPolicies.None;
        }

        if (string.IsNullOrWhiteSpace(policy)
            || policy.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            // Sign* without explicit policy defaults to Basic256Sha256.
            return SecurityPolicies.Basic256Sha256;
        }

        if (policy.Equals("Basic256Sha256", StringComparison.OrdinalIgnoreCase)
            || string.Equals(policy, SecurityPolicies.Basic256Sha256, StringComparison.Ordinal))
        {
            return SecurityPolicies.Basic256Sha256;
        }

        if (string.Equals(policy, SecurityPolicies.None, StringComparison.Ordinal))
        {
            return SecurityPolicies.None;
        }

        throw new InvalidOperationException(
            $"Unsupported OPC UA SecurityPolicy '{policy}'. Use None or Basic256Sha256.");
    }

    private static string ShortPolicy(string? policyUri)
    {
        if (string.IsNullOrEmpty(policyUri))
        {
            return "None";
        }

        int hash = policyUri.LastIndexOf('#');
        return hash >= 0 && hash < policyUri.Length - 1
            ? policyUri[(hash + 1)..]
            : policyUri;
    }

    private static short? MapUaDataTypeToCanonical(NodeId dataTypeId)
    {
        // Map common UA built-in types to DA canonical VARTYPE-ish codes used elsewhere.
        if (dataTypeId.NamespaceIndex != 0 || dataTypeId.IdType != IdType.Numeric)
        {
            return null;
        }

        uint id = Convert.ToUInt32(dataTypeId.Identifier);
        return id switch
        {
            DataTypes.Boolean => 11,   // VT_BOOL
            DataTypes.SByte => 16,     // VT_I1
            DataTypes.Byte => 17,      // VT_UI1
            DataTypes.Int16 => 2,      // VT_I2
            DataTypes.UInt16 => 18,    // VT_UI2
            DataTypes.Int32 => 3,      // VT_I4
            DataTypes.UInt32 => 19,    // VT_UI4
            DataTypes.Int64 => 20,     // VT_I8
            DataTypes.UInt64 => 21,    // VT_UI8
            DataTypes.Float => 4,      // VT_R4
            DataTypes.Double => 5,     // VT_R8
            DataTypes.String => 8,     // VT_BSTR
            DataTypes.DateTime => 7,   // VT_DATE
            _ => null
        };
    }
}
