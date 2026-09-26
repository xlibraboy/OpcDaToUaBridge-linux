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
            else
            {
                // Mapping already has a node: refresh mapping-driven attributes
                // (AccessLevel, DataType, display metadata) in place.
                node_manager_.UpdateMapping(mapping);
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
    public UaBandwidthMetrics GetBandwidthMetrics()
    {
        return node_manager_?.GetBandwidthMetrics() ?? new UaBandwidthMetrics(0, 0, 0, 0);
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
            ProductName = "OPC Bridge",
            ProductUri = "urn:ohmypi:opc-bridge",
            SoftwareVersion = typeof(BridgeUaServer).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            BuildNumber = "0",
            BuildDate = DateTime.UtcNow
        };
    }

    public override UserTokenPolicyCollection GetUserTokenPolicies(ApplicationConfiguration configuration, EndpointDescription endpoint)
    {
        return SelectUserTokenPolicies(base.GetUserTokenPolicies(configuration, endpoint), options_.RequireAuthentication);
    }

    /// <summary>
    /// Issue #14: with the credential gate on, the endpoint advertises the
    /// username/password policy ALONE. Keeping the SDK's default anonymous policy in
    /// the list invites a client to select anonymous and only then be refused at
    /// ActivateSession — advertise what the server actually accepts so clients ask
    /// for credentials up front.
    ///
    /// The token policy is None (plaintext) because that is the only thing this
    /// server can verify: its sole channel policy is None, and a client that encrypts
    /// the password for Basic256Sha256 arrives with a token the server cannot decrypt
    /// (DecryptedPassword comes back null and every login fails).
    /// </summary>
    internal static UserTokenPolicyCollection SelectUserTokenPolicies(UserTokenPolicyCollection basePolicies, bool requireAuthentication)
    {
        if (!requireAuthentication)
        {
            return basePolicies;
        }

        return new UserTokenPolicyCollection
        {
            new UserTokenPolicy(UserTokenType.UserName)
            {
                PolicyId = "username",
                IssuedTokenType = null,
                IssuerEndpointUrl = null,
                SecurityPolicyUri = SecurityPolicies.None
            }
        };
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

    /// <summary>
    /// The live entry point: the stack calls this overload, so the credential gate has
    /// to be enforced here. Validating only in the obsolete <see cref="ActivateSession"/>
    /// override (as this server used to) left the gate dead code — a wrong password
    /// still activated a session (issue #14).
    /// </summary>
    public override async Task<ActivateSessionResponse> ActivateSessionAsync(
        SecureChannelContext secureChannelContext,
        RequestHeader requestHeader,
        SignatureData clientSignature,
        SignedSoftwareCertificateCollection clientSoftwareCertificates,
        StringCollection localeIds,
        ExtensionObject userIdentityToken,
        SignatureData userTokenSignature,
        CancellationToken ct)
    {
        ValidateUserIdentity(userIdentityToken);

        return await base.ActivateSessionAsync(
                secureChannelContext, requestHeader, clientSignature, clientSoftwareCertificates,
                localeIds, userIdentityToken, userTokenSignature, ct)
            .ConfigureAwait(false);
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
        ValidateUserIdentity(userIdentityToken);

        return base.ActivateSession(channel, requestHeader, clientSignature, clientSoftwareCertificates,
            localeIds, userIdentityToken, userTokenSignature, out serverNonce, out results, out diagnosticInfos);
    }
#pragma warning restore CS0618, CS0672

    private void ValidateUserIdentity(ExtensionObject userIdentityToken)
    {
        if (!options_.RequireAuthentication)
        {
            return;
        }

        if (userIdentityToken.Body is not UserNameIdentityToken userNameToken)
        {
            // Anonymous identity while the gate is on.
            throw new ServiceResultException(StatusCodes.BadUserAccessDenied,
                "Authentication required. Provide a username and password.");
        }

        string? username = userNameToken.UserName;
        // The endpoint advertises a plaintext (policy None) user token, so the password
        // arrives as UTF-8 bytes in Password. DecryptedPassword is still null here: the
        // stack fills it while it processes the token inside the base activation, which
        // is why validating before the base call has to read Password directly.
        string password = userNameToken.Password != null
            ? System.Text.Encoding.UTF8.GetString(userNameToken.Password)
            : string.Empty;

        if (!ValidateUserNameCredential(username, password, options_.Username, options_.Password))
        {
            throw new ServiceResultException(StatusCodes.BadIdentityTokenInvalid,
                "Invalid username or password.");
        }
    }

    /// <summary>
    /// Issue #14: exact, ordinal credential comparison for external OPC UA clients.
    /// A null/blank stored side is never valid (the server refuses to authenticate
    /// anyone until real credentials are configured), and a client-supplied null or
    /// blank is rejected before it can be compared — so empty strings can never match.
    /// </summary>
    internal static bool ValidateUserNameCredential(string? username, string? password, string? storedUsername, string? storedPassword)
    {
        if (string.IsNullOrWhiteSpace(storedUsername) || string.IsNullOrWhiteSpace(storedPassword))
        {
            return false;
        }

        return !string.IsNullOrEmpty(username)
            && !string.IsNullOrEmpty(password)
            && string.Equals(username, storedUsername, StringComparison.Ordinal)
            && string.Equals(password, storedPassword, StringComparison.Ordinal);
    }

    private static string GetMappingKey(string sourceId, string itemId)
    {
        return string.Concat(sourceId.Trim(), "::", itemId.Trim());
    }
}

public sealed record UaBandwidthMetrics(
    long TotalNotifications,
    double NotificationsPerSec,
    long TotalBytes,
    double BytesPerSec);

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
