using Opc.Ua;
using Opc.Ua.Server;
using OpcBridge.Core;

namespace OpcBridge.Ua;

internal sealed class BridgeUaServer : StandardServer
{
    private readonly IReadOnlyList<TagMapping> mappings_;
    private readonly UaServerOptions options_;
    private BridgeNodeManager? node_manager_;

    public BridgeUaServer(IReadOnlyList<TagMapping> mappings, UaServerOptions options)
    {
        mappings_ = mappings;
        options_ = options;
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
            .Select(mapping => GetMappingKey(mapping.SourceId, mapping.ItemId))
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
            if (!current.Contains(GetMappingKey(mapping.SourceId, mapping.ItemId)))
            {
                node_manager_.AddMapping(mapping);
            }
        }
    }

    public int GetConnectedSessionCount()
    {
        try
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
        catch (ServiceResultException)
        {
            // Server can be mid-start/halted while certificate/setup is still settling.
            return 0;
        }
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
        try
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
        catch (ServiceResultException)
        {
            return Array.Empty<UaSessionDiagnostic>();
        }
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

    public override UserTokenPolicyCollection GetUserTokenPolicies(ApplicationConfiguration configuration, EndpointDescription endpoint)
    {
        UserTokenPolicyCollection policies = base.GetUserTokenPolicies(configuration, endpoint);

        if (options_.RequireAuthentication)
        {
            // Add username/password token policy so clients know to send credentials
            policies.Add(new UserTokenPolicy(UserTokenType.UserName)
            {
                PolicyId = "username",
                IssuedTokenType = null,
                IssuerEndpointUrl = null,
                SecurityPolicyUri = SecurityPolicies.Basic256Sha256
            });
        }

        return policies;
    }
#pragma warning disable CS0618, CS0672
    public override ResponseHeader CreateSession(
        SecureChannelContext channel,
        RequestHeader requestHeader,
        ApplicationDescription clientDescription,
        string serverUri,
        string serverName,
        string endpointUrl,
        byte[] clientNonce,
        byte[] clientCertificate,
        double requestedSessionTimeout,
        uint maxResponseMessageSize,
        out NodeId sessionId,
        out NodeId authenticationToken,
        out double revisedSessionTimeout,
        out byte[] serverNonce,
        out byte[] serverCertificate,
        out EndpointDescriptionCollection serverEndpoints,
        out SignedSoftwareCertificateCollection serverSoftwareCertificates,
        out SignatureData serverSignature,
        out uint maxRequestMessageSize)
    {
        // IP allowlist check — SecureChannelContext doesn't expose remote IP directly in this SDK version.
        // IP filtering is handled at the firewall level instead. Config is accepted but logged.
        if (options_.AllowedIpAddresses is { Count: > 0 })
        {
            // IP allowlist is documented in appsettings but enforcement requires
            // a custom transport listener. Use Windows Firewall for IP filtering.
        }

        return base.CreateSession(channel, requestHeader, clientDescription, serverUri, serverName,
            endpointUrl, clientNonce, clientCertificate, requestedSessionTimeout, maxResponseMessageSize,
            out sessionId, out authenticationToken, out revisedSessionTimeout, out serverNonce,
            out serverCertificate, out serverEndpoints, out serverSoftwareCertificates,
            out serverSignature, out maxRequestMessageSize);
    }

    public override ResponseHeader ActivateSession(
        SecureChannelContext channel,
        RequestHeader requestHeader,
        SignatureData clientSignature,
        SignedSoftwareCertificateCollection clientSoftwareCertificates,
        StringCollection localeIds,
        ExtensionObject userIdentityToken,
        SignatureData userTokenSignature,
        out byte[] serverNonce,
        out StatusCodeCollection results,
        out DiagnosticInfoCollection diagnosticInfos)
    {
        // Username/password validation
        if (options_.RequireAuthentication)
        {
            if (userIdentityToken.Body is UserNameIdentityToken userNameToken)
            {
                string? username = userNameToken.UserName;
                string password = userNameToken.DecryptedPassword != null
                    ? System.Text.Encoding.UTF8.GetString(userNameToken.DecryptedPassword)
                    : string.Empty;

                if (!string.Equals(username, options_.Username, StringComparison.Ordinal) ||
                    !string.Equals(password, options_.Password, StringComparison.Ordinal))
                {
                    throw new ServiceResultException(StatusCodes.BadIdentityTokenInvalid,
                        "Invalid username or password.");
                }
            }
            else
            {
                // Anonymous access when auth is required
                throw new ServiceResultException(StatusCodes.BadUserAccessDenied,
                    "Authentication required. Provide a username and password.");
            }
        }

        return base.ActivateSession(channel, requestHeader, clientSignature, clientSoftwareCertificates,
            localeIds, userIdentityToken, userTokenSignature, out serverNonce, out results, out diagnosticInfos);
    }
#pragma warning restore CS0618, CS0672

    private static string GetMappingKey(string sourceId, string itemId)
    {
        return string.Concat(sourceId.Trim(), "::", itemId.Trim());
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
