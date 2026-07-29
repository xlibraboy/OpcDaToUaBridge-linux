using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OpcBridge.Core;
using OpcBridge.Da;

namespace OpcBridge.Ua;

public sealed class OpcUaSourceClient : ISourceClient, ISubscribableSourceClient
{
    private const int ReadChunkSize = 500;
    private const int MonitoredItemBatchSize = 750;
    private const int NotificationFlushSize = 1000;

    private readonly OpcUaSourceClientOptions options_;
    private readonly ILogger logger_;
    private readonly object gate_ = new();
    private readonly DefaultSessionFactory session_factory_ =
#pragma warning disable CS0618 // No ITelemetryContext on source client yet.
        new();
#pragma warning restore CS0618

    private ApplicationConfiguration? configuration_;
    private Session? session_;
    private Subscription? subscription_;
    private readonly Dictionary<string, MonitoredItem> monitored_items_ =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> node_id_by_display_ =
        new(StringComparer.Ordinal);
    private bool subscriptions_active_;
    private bool disposed_;

    /// <summary>
    /// Raised when a UA subscription delivers values via MonitoredItems.
    /// </summary>
    public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived;

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
                    // New session owns its own subscription; drop local bookkeeping for the old one.
                    subscription_ = null;
                    monitored_items_.Clear();
                    node_id_by_display_.Clear();
                    subscriptions_active_ = false;
                    session = null!; // ownership transferred
                }
            }

            if (previous is not null)
            {
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
            throw new InvalidOperationException(
                $"Failed to connect OPC UA source '{options_.SourceId}' to '{endpointUrl}': {ex.Message}",
                ex);
        }
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
        IReadOnlyList<TagMapping> desiredMappings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed_, this);

        if (!options_.UseSubscriptions)
        {
            await TearDownSubscriptionAsync(keepSession: true).ConfigureAwait(false);
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
            Dictionary<string, int> desiredSampling = BuildDesiredSampling(desiredMappings);
            IReadOnlyCollection<string> desiredIds = desiredSampling.Keys;

            Subscription subscription = await EnsureSubscriptionAsync(session, cancellationToken)
                .ConfigureAwait(false);

            string[] activeIds;
            lock (gate_)
            {
                activeIds = monitored_items_.Keys.ToArray();
            }

            (IReadOnlyList<string> toAdd, IReadOnlyList<string> toRemove) =
                MonitoredItemReconcile.Diff(desiredIds, activeIds);

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
                            options_.SourceId,
                            nodeIdString);
                        continue;
                    }

                    int sampling = desiredSampling.TryGetValue(nodeIdString, out int s)
                        ? s
                        : Math.Max(100, options_.UpdateRateMs);

                    // No ITelemetryContext on source client yet — parameterless ctor is obsolete but fine.
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
                                options_.SourceId,
                                key,
                                item.Status.Created,
                                createError?.StatusCode);
                            item.Notification -= OnMonitoredItemNotification;
                            subscription.RemoveItem(item);
                            continue;
                        }

                        monitored_items_[key] = item;
                        node_id_by_display_[item.DisplayName] = key;
                    }
                }

                // Drop failed creates from the subscription if any were removed above.
                if (subscription.ChangesPending)
                {
                    await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            // Keep sampling intervals aligned for items that remain desired.
            // (Add/remove covered; existing items keep prior sampling — acceptable for v1.)

            lock (gate_)
            {
                // Only disable poll when at least one MonitoredItem is actually tracked.
                // Empty desired (Manual-only) or all creates failed → keep poll path.
                subscriptions_active_ = subscription.Created && monitored_items_.Count > 0;
            }

            logger_.LogInformation(
                "OPC UA source {SourceId} subscription reconcile: desired={Desired} active={Active} added={Added} removed={Removed}",
                options_.SourceId,
                desiredIds.Count,
                monitored_items_.Count,
                toAdd.Count,
                toRemove.Count);
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
            await TearDownSubscriptionAsync(keepSession: true).ConfigureAwait(false);
        }
    }

    /// <summary>True when MonitoredItems are delivering values (poll ReadAsync is a no-op).</summary>
    public bool SubscriptionsActive => subscriptions_active_;

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
        }

        await TearDownSubscriptionAsync(keepSession: false).ConfigureAwait(false);

        if (session is null)
        {
            return;
        }

        await SafeCloseAndDisposeAsync(session).ConfigureAwait(false);

    }

    private Dictionary<string, int> BuildDesiredSampling(IReadOnlyList<TagMapping>? desiredMappings)
    {
        Dictionary<string, int> desired = new(StringComparer.Ordinal);
        if (desiredMappings is null)
        {
            return desired;
        }

        int defaultSampling = Math.Max(100, options_.UpdateRateMs);
        for (int i = 0; i < desiredMappings.Count; i++)
        {
            TagMapping mapping = desiredMappings[i];
            if (!mapping.Enabled)
            {
                continue;
            }

            if (string.Equals(mapping.Mode, TagMode.Manual, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.ItemId))
            {
                continue;
            }

            // Write-only tags are not source-read (matches SourceMappingCache.SourceRead filter).
            if (string.Equals(mapping.AccessRights, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string nodeId = mapping.ItemId.Trim();
            int sampling = mapping.PollRateMs > 0 ? mapping.PollRateMs : defaultSampling;
            if (sampling < 0)
            {
                sampling = defaultSampling;
            }

            // First wins; Diff keys are unique.
            if (!desired.ContainsKey(nodeId))
            {
                desired[nodeId] = sampling;
            }
        }

        return desired;
    }

    private async Task<Subscription> EnsureSubscriptionAsync(Session session, CancellationToken cancellationToken)
    {
        Subscription? existing;
        lock (gate_)
        {
            existing = subscription_;
            if (existing is not null
                && ReferenceEquals(existing.Session, session)
                && existing.Created)
            {
                int desiredPublishing = Math.Max(100, options_.UpdateRateMs);
                if (existing.PublishingInterval != desiredPublishing)
                {
                    existing.PublishingInterval = desiredPublishing;
                }

                return existing;
            }
        }

        // Drop stale subscription bookkeeping (session may have changed).
        await TearDownSubscriptionAsync(keepSession: true).ConfigureAwait(false);
        // No ITelemetryContext on source client yet — parameterless ctor is obsolete but fine.
#pragma warning disable CS0618
        Subscription subscription = new()
        {
            DisplayName = $"OpcBridge_{options_.SourceId}",
            PublishingEnabled = true,
            PublishingInterval = Math.Max(100, options_.UpdateRateMs),
            KeepAliveCount = 10,
            LifetimeCount = 1000,
            MaxNotificationsPerPublish = 0,
            TimestampsToReturn = TimestampsToReturn.Both,
            Priority = 0
        };
#pragma warning restore CS0618

        // Prefer batch notification path for large publish sets.
        subscription.FastDataChangeCallback = OnFastDataChange;

        if (!session.AddSubscription(subscription))
        {
            try
            {
                subscription.FastDataChangeCallback = null;
            }
            catch
            {
                // ignore
            }

            subscription.Dispose();
            throw new InvalidOperationException(
                $"Failed to add OPC UA subscription for source '{options_.SourceId}'.");
        }

        try
        {
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
            if (!subscription.Created)
            {
                throw new InvalidOperationException(
                    $"OPC UA subscription create failed for source '{options_.SourceId}'.");
            }
        }
        catch
        {
            // CreateAsync may throw before Created, or return with Created=false.
            // subscription_ is still null — tear down the local object so it is not orphaned on the session.
            await DiscardUnownedSubscriptionAsync(session, subscription).ConfigureAwait(false);
            throw;
        }

        lock (gate_)
        {
            subscription_ = subscription;
        }

        return subscription;
    }

    /// <summary>
    /// Delete/Remove/Dispose a subscription that was added to the session but never stored in <see cref="subscription_"/>.
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

    private async Task TearDownSubscriptionAsync(bool keepSession)
    {
        Subscription? subscription;
        List<MonitoredItem> items;
        lock (gate_)
        {
            subscription = subscription_;
            subscription_ = null;
            items = monitored_items_.Values.ToList();
            monitored_items_.Clear();
            node_id_by_display_.Clear();
            subscriptions_active_ = false;
        }

        for (int i = 0; i < items.Count; i++)
        {
            try
            {
                items[i].Notification -= OnMonitoredItemNotification;
            }
            catch
            {
                // ignore
            }
        }

        if (subscription is null)
        {
            return;
        }

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
            if (subscription.Session is not null && subscription.Created)
            {
                await subscription.DeleteAsync(silent: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "Error deleting OPC UA subscription for source {SourceId}", options_.SourceId);
        }

        try
        {
            if (subscription.Session is ISession s && keepSession)
            {
                await s.RemoveSubscriptionAsync(subscription, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore remove races on dispose
        }

        try
        {
            subscription.Dispose();
        }
        catch
        {
            // ignore
        }

        _ = keepSession;
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
        if (subscription_?.FastDataChangeCallback is not null)
        {
            return;
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
            ? "OpcDaToUaBridge.UaClient"
            : options_.ApplicationName.Trim();
        // ApplicationUri must stay stable across sources so the shared
        // pki/ua-client application certificate remains valid.
        string applicationUri = $"urn:ohmypi:{applicationName}";

        ApplicationConfiguration configuration = new()
        {
            ApplicationName = applicationName,
            ApplicationUri = applicationUri,
            ProductUri = "urn:ohmypi:opc-da-to-ua-bridge-client",
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
                throw new InvalidOperationException(
                    $"Endpoint discovery failed for '{endpointUrl}': {ex.Message}",
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
