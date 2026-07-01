using Opc.Ua;
using Opc.Ua.Server;
using OpcBridge.Core;

namespace OpcBridge.Ua;

internal sealed class BridgeUaServer : StandardServer
{
    private readonly IReadOnlyList<TagMapping> mappings_;
    private BridgeNodeManager? node_manager_;

    public BridgeUaServer(IReadOnlyList<TagMapping> mappings)
    {
        mappings_ = mappings;
    }

    public void UpdateValue(BridgeValue value)
    {
        node_manager_?.UpdateValue(value);
    }
    public void SetWriteHandler(Action<BridgeValue, TaskCompletionSource<bool>> handler)
    {
        node_manager_?.SetWriteHandler(handler);
    }


    public void SyncMappings(IReadOnlyList<TagMapping> mappings)
    {
        if (node_manager_ is null)
        {
            return;
        }

        HashSet<string> desired = mappings
            .Select(mapping => GetMappingKey(mapping.SourceId, mapping.DaItemId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> current = node_manager_.GetMappedKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string key in current)
        {
            if (!desired.Contains(key))
            {
                int separator = key.IndexOf("::", StringComparison.Ordinal);
                if (separator > 0)
                {
                    node_manager_.RemoveMapping(key[..separator], key[(separator + 2)..]);
                }
            }
        }

        foreach (TagMapping mapping in mappings)
        {
            if (!current.Contains(GetMappingKey(mapping.SourceId, mapping.DaItemId)))
            {
                node_manager_.AddMapping(mapping);
            }
        }
    }

    public int GetConnectedSessionCount()
    {
        ISessionManager? sessionManager = ServerInternal?.SessionManager;
        if (sessionManager is null)
        {
            return 0;
        }

        return sessionManager
            .GetSessions()
            .Cast<ISession>()
            .Count(session => session.Activated && !session.HasExpired);
    }

    public int GetMappedNodeCount()
    {
        return node_manager_?.GetMappedNodeCount() ?? 0;
    }

    public DateTime? GetLastValueUpdateUtc()
    {
        return node_manager_?.GetLastValueUpdateUtc();
    }
    public (long TotalNotifications, double NotificationsPerSec) GetBandwidthEstimate()
    {
        return node_manager_?.GetBandwidthEstimate() ?? (0, 0);
    }

    public IReadOnlyList<UaSessionDiagnostic> GetSessionDiagnostics()
    {
        ISessionManager? sessionManager = ServerInternal?.SessionManager;
        if (sessionManager is null)
        {
            return Array.Empty<UaSessionDiagnostic>();
        }

        List<UaSessionDiagnostic> result = new();
        foreach (ISession session in sessionManager.GetSessions().Cast<ISession>())
        {
            if (!session.Activated || session.HasExpired)
            {
                continue;
            }

            SessionDiagnosticsDataType diag = session.SessionDiagnostics;
            string clientName = session.Identity?.DisplayName
                ?? session.EffectiveIdentity?.DisplayName
                ?? "anonymous";
            string endpointUrl = diag?.EndpointUrl ?? string.Empty;

            result.Add(new UaSessionDiagnostic(
                session.Id?.ToString() ?? "?",
                clientName,
                endpointUrl,
                (int)(diag?.CurrentSubscriptionsCount ?? 0),
                (int)(diag?.CurrentMonitoredItemsCount ?? 0),
                (int)(diag?.CurrentPublishRequestsInQueue ?? 0),
                (long)(diag?.PublishCount?.TotalCount ?? 0),
                diag?.ClientLastContactTime ?? DateTime.MinValue));
        }

        return result;
    }

    public IReadOnlyList<UaSubscriptionDiagnostic> GetSubscriptionDiagnostics()
    {
        ISubscriptionManager? subMgr = ServerInternal?.SubscriptionManager;
        if (subMgr is null)
        {
            return Array.Empty<UaSubscriptionDiagnostic>();
        }

        List<UaSubscriptionDiagnostic> result = new();
        foreach (var sub in subMgr.GetSubscriptions())
        {
            var d = sub.Diagnostics;
            string clientName = sub.Session?.Identity?.DisplayName
                ?? sub.EffectiveIdentity?.DisplayName
                ?? "anonymous";
            result.Add(new UaSubscriptionDiagnostic(
                (int)sub.Id,
                clientName,
                (int)sub.MonitoredItemCount,
                sub.PublishingInterval,
                (long)(d?.DataChangeNotificationsCount ?? 0),
                (long)(d?.NotificationsCount ?? 0),
                (long)(d?.PublishRequestCount ?? 0),
                (long)(d?.LatePublishRequestCount ?? 0)));
        }

        return result;
    }



    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        node_manager_ = new BridgeNodeManager(server, configuration, mappings_);
        return new MasterNodeManager(server, configuration, null, new INodeManager[] { node_manager_ });
    }

    protected override ServerProperties LoadServerProperties()
    {
        return new ServerProperties
        {
            ManufacturerName = "Oh My Pi",
            ProductName = "OPC DA to OPC UA Bridge",
            ProductUri = "urn:ohmypi:opc-da-to-ua-bridge",
            SoftwareVersion = typeof(BridgeUaServer).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            BuildNumber = "0",
            BuildDate = DateTime.UtcNow
        };
    }

    private static string GetMappingKey(string sourceId, string daItemId)
    {
        return string.Concat(sourceId.Trim(), "::", daItemId.Trim());
    }
}

public sealed record UaSessionDiagnostic(
    string SessionId,
    string ClientName,
    string EndpointUrl,
    int Subscriptions,
    int MonitoredItems,
    int PublishRequestsInQueue,
    long TotalPublishCount,
    DateTime LastContactUtc);

public sealed record UaSubscriptionDiagnostic(
    int SubscriptionId,
    string ClientName,
    int MonitoredItems,
    double PublishingIntervalMs,
    long DataChangeNotifications,
    long TotalNotifications,
    long PublishRequests,
    long LatePublishRequests);
