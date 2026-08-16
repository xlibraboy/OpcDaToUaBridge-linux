using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace OpcBridge.Ua;

/// <summary>
/// Short-lived OPC UA sessions for test-connection and address-space browse (not the hot path).
/// </summary>
public sealed class OpcUaBrowseService
{
    public const int DefaultMaxNodes = 200;
    public const int AbsoluteMaxNodes = 1000;
    public const int DefaultTimeoutMs = 15_000;
    public static readonly string DefaultNodeId = ObjectIds.ObjectsFolder.ToString();

    private readonly ILogger logger_;
    private readonly DefaultSessionFactory session_factory_ =
#pragma warning disable CS0618 // No ITelemetryContext injected yet.
        new();
#pragma warning restore CS0618

    public OpcUaBrowseService(ILogger<OpcUaBrowseService>? logger = null)
    {
        logger_ = logger ?? NullLogger<OpcUaBrowseService>.Instance;
    }

    public async Task<UaTestConnectionResult> TestConnectionAsync(
        OpcUaSourceClientOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var timeoutCts = CreateOperationTimeoutCts(options, cancellationToken);

        Session? session = null;
        try
        {
            session = await OpenSessionAsync(options, timeoutCts.Token).ConfigureAwait(false);
            string? productName = await TryReadServerProductNameAsync(session, timeoutCts.Token)
                .ConfigureAwait(false);
            string? sessionId = session.SessionId?.ToString();

            return new UaTestConnectionResult(
                Ok: true,
                Error: null,
                ServerProductName: productName,
                SessionId: sessionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller/request abort — do not map to a soft connection failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Nested operation timeout only.
            return new UaTestConnectionResult(false, "Connection timed out.", null, null);
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "UA test-connection failed for {Endpoint}", options.EndpointUrl);
            return new UaTestConnectionResult(false, FlattenMessage(ex), null, null);
        }
        finally
        {
            await SafeCloseAndDisposeAsync(session).ConfigureAwait(false);
        }
    }
    public async Task<UaDiscoverResult> DiscoverServersAsync(
        OpcUaSourceClientOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        string endpointUrl = options.EndpointUrl?.Trim() ?? string.Empty;
        if (endpointUrl.Length == 0)
        {
            return new UaDiscoverResult(Array.Empty<UaDiscoveredServerDto>(), "Endpoint URL is required.");
        }

        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase))
        {
            return new UaDiscoverResult(Array.Empty<UaDiscoveredServerDto>(),
                $"Endpoint URL must be an opc.tcp URL (got '{endpointUrl}').");
        }

        int timeoutMs = options.SessionTimeoutMs > 0
            ? Math.Min(options.SessionTimeoutMs, DefaultTimeoutMs)
            : DefaultTimeoutMs;

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        // Phase 1: FindServersOnNetwork — only works against a Local Discovery Server (LDS/LDS-ME).
        List<UaDiscoveredServerDto> servers = new();
        string? lastError = null;

        try
        {
            ApplicationConfiguration config = await BuildConfigurationAsync(options, timeoutCts.Token)
                .ConfigureAwait(false);

            using DiscoveryClient discovery = await DiscoveryClient.CreateAsync(
                    config,
                    uri,
                    DiagnosticsMasks.None,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            // Try network discovery first.
            try
            {
                (ServerOnNetworkCollection networkServers, DateTime _) =
                    await discovery.FindServersOnNetworkAsync(
                            startingRecordId: 0,
                            maxRecordsToReturn: 0,
                            serverCapabilityFilter: null,
                            ct: timeoutCts.Token)
                        .ConfigureAwait(false);

                if (networkServers is not null)
                {
                    foreach (ServerOnNetwork record in networkServers)
                    {
                        servers.Add(new UaDiscoveredServerDto(
                            ServerUri: null,
                            RecordId: record.RecordId,
                            DiscoveryUrl: record.DiscoveryUrl,
                            ServerName: record.ServerName,
                            ServerCapabilities: record.ServerCapabilities?.ToList(),
                            IsOnline: true));
                    }
                }
            }
            catch (Exception ex)
            {
                // FindServersOnNetwork not supported on this server — fall through.
                lastError = FlattenMessage(ex);
            }

            // Phase 2: FindServers — works against any UA server.
            FindServersResponse fsResponse =
                await discovery.FindServersAsync(
                        requestHeader: null,
                        endpointUrl: null,
                        localeIds: null,
                        serverUris: null,
                        ct: timeoutCts.Token)
                    .ConfigureAwait(false);

            if (fsResponse.Servers is not null)
            {
                HashSet<string> seen = new(servers.Count > 0
                    ? servers.Where(s => s.DiscoveryUrl is not null).Select(s => s.DiscoveryUrl!)
                    : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

                foreach (ApplicationDescription ad in fsResponse.Servers)
                {
                    if (ad.DiscoveryUrls is null) continue;
                    foreach (string url in ad.DiscoveryUrls)
                    {
                        if (string.IsNullOrWhiteSpace(url) || seen.Contains(url)) continue;
                        seen.Add(url);
                        servers.Add(new UaDiscoveredServerDto(
                            ServerUri: ad.ApplicationUri,
                            RecordId: null,
                            DiscoveryUrl: url,
                            ServerName: ad.ApplicationName?.Text ?? ad.ApplicationUri,
                            ServerCapabilities: null,
                            IsOnline: true));
                    }
                }
            }

            return new UaDiscoverResult(servers, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UaDiscoverResult(Array.Empty<UaDiscoveredServerDto>(), "Discovery timed out.");
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "UA discovery failed for {Endpoint}", endpointUrl);
            return new UaDiscoverResult(Array.Empty<UaDiscoveredServerDto>(),
                lastError ?? FlattenMessage(ex));
        }
    }

    public async Task<UaBrowseResult> BrowseAsync(
        OpcUaSourceClientOptions options,
        string? nodeId,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        int pageSize = ClampMaxNodes(maxNodes);
        string targetNodeId = string.IsNullOrWhiteSpace(nodeId) ? DefaultNodeId : nodeId.Trim();
        if (!NodeId.TryParse(targetNodeId, out NodeId? parsedNodeId) || parsedNodeId is null)
        {
            return new UaBrowseResult(
                Array.Empty<UaBrowseNodeDto>(),
                ContinuationPoint: null,
                Error: $"Invalid nodeId '{targetNodeId}'.");
        }

        using var timeoutCts = CreateOperationTimeoutCts(options, cancellationToken);

        Session? session = null;
        try
        {
            session = await OpenSessionAsync(options, timeoutCts.Token).ConfigureAwait(false);

            BrowseDescription description = new()
            {
                NodeId = parsedNodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = 0u,
                ResultMask = (uint)BrowseResultMask.All
            };

            BrowseDescriptionCollection nodesToBrowse = new() { description };
            BrowseResponse browseResponse = await session.BrowseAsync(
                    requestHeader: null,
                    view: null,
                    requestedMaxReferencesPerNode: (uint)pageSize,
                    nodesToBrowse: nodesToBrowse,
                    ct: timeoutCts.Token)
                .ConfigureAwait(false);
            BrowseResultCollection? results = browseResponse.Results;

            if (results is null || results.Count == 0)
            {
                return new UaBrowseResult(Array.Empty<UaBrowseNodeDto>(), null, null);
            }

            BrowseResult browseResult = results[0];
            if (StatusCode.IsBad(browseResult.StatusCode))
            {
                return new UaBrowseResult(
                    Array.Empty<UaBrowseNodeDto>(),
                    null,
                    $"Browse failed: {browseResult.StatusCode}");
            }

            List<UaBrowseNodeDto> nodes = new(browseResult.References?.Count ?? 0);
            if (browseResult.References is not null)
            {
                foreach (ReferenceDescription reference in browseResult.References)
                {
                    if (reference is null)
                    {
                        continue;
                    }

                    string childNodeId = ExpandedNodeIdToString(reference.NodeId, session.NamespaceUris);
                    string displayName = reference.DisplayName?.Text
                        ?? reference.BrowseName?.Name
                        ?? childNodeId;
                    string nodeClass = reference.NodeClass.ToString();
                    bool hasChildren = MayHaveChildren(reference.NodeClass);

                    nodes.Add(new UaBrowseNodeDto(childNodeId, displayName, nodeClass, hasChildren));
                }
            }

            string? continuation = null;
            if (browseResult.ContinuationPoint is { Length: > 0 })
            {
                continuation = Convert.ToBase64String(browseResult.ContinuationPoint);
                // Release server-side continuation — v1 returns a single page only.
                try
                {
                    await session.BrowseNextAsync(
                            requestHeader: null,
                            releaseContinuationPoints: true,
                            continuationPoints: new ByteStringCollection { browseResult.ContinuationPoint },
                            ct: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger_.LogDebug(ex, "Failed to release browse continuation point");
                }
            }

            return new UaBrowseResult(nodes, continuation, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller/request abort — do not map to a soft browse failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Nested operation timeout only.
            return new UaBrowseResult(Array.Empty<UaBrowseNodeDto>(), null, "Browse timed out.");
        }
        catch (Exception ex) when (IsTimeoutLike(ex))
        {
            return new UaBrowseResult(Array.Empty<UaBrowseNodeDto>(), null, "Browse timed out.");
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "UA browse failed for {Endpoint} node {NodeId}", options.EndpointUrl, targetNodeId);
            return new UaBrowseResult(Array.Empty<UaBrowseNodeDto>(), null, FlattenMessage(ex));
        }
        finally
        {
            await SafeCloseAndDisposeAsync(session).ConfigureAwait(false);
        }
    }

    public static int ClampMaxNodes(int maxNodes)
    {
        if (maxNodes <= 0)
        {
            return DefaultMaxNodes;
        }

        return Math.Min(maxNodes, AbsoluteMaxNodes);
    }

    private static CancellationTokenSource CreateOperationTimeoutCts(
        OpcUaSourceClientOptions options,
        CancellationToken cancellationToken)
    {
        int timeoutMs = options.SessionTimeoutMs > 0
            ? Math.Min(options.SessionTimeoutMs, DefaultTimeoutMs)
            : DefaultTimeoutMs;

        CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        return timeoutCts;
    }

    private async Task<Session> OpenSessionAsync(
        OpcUaSourceClientOptions options,
        CancellationToken cancellationToken)
    {
        string endpointUrl = options.EndpointUrl?.Trim() ?? string.Empty;
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

        MessageSecurityMode desiredMode = MapSecurityMode(options.SecurityMode);
        string desiredPolicy = MapSecurityPolicy(options.SecurityPolicy, desiredMode);

        ApplicationConfiguration configuration = await BuildConfigurationAsync(options, cancellationToken)
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
        IUserIdentity identity = CreateUserIdentity(options.Username, options.Password);

        int timeoutMs = options.SessionTimeoutMs > 0
            ? Math.Min(options.SessionTimeoutMs, DefaultTimeoutMs)
            : DefaultTimeoutMs;
        uint sessionTimeout = (uint)Math.Max(1000, timeoutMs);
        string sourceId = string.IsNullOrWhiteSpace(options.SourceId) ? "browse" : options.SourceId.Trim();
        string sessionName = $"{options.ApplicationName}:{sourceId}:browse";

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

        return session;
    }

    private async Task<ApplicationConfiguration> BuildConfigurationAsync(
        OpcUaSourceClientOptions options,
        CancellationToken cancellationToken)
    {
        string pkiRoot = Path.Combine(AppContext.BaseDirectory, options.PkiRoot);
        string applicationName = string.IsNullOrWhiteSpace(options.ApplicationName)
            ? "OpcDaToUaBridge.UaClient"
            : options.ApplicationName.Trim();
        // ApplicationUri must stay stable across sources/browse calls so the shared
        // pki/ua-client application certificate remains valid.
        string applicationUri = $"urn:ohmypi:{applicationName}";
        int operationTimeout = options.SessionTimeoutMs > 0
            ? Math.Clamp(options.SessionTimeoutMs, 5000, DefaultTimeoutMs)
            : DefaultTimeoutMs;

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
                AutoAcceptUntrustedCertificates = options.AutoAcceptUntrustedCertificates,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048,
                AddAppCertToTrustedStore = true
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = operationTimeout,
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
                DefaultSessionTimeout = operationTimeout
            },
            TraceConfiguration = new TraceConfiguration()
        };

        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken).ConfigureAwait(false);

        if (options.AutoAcceptUntrustedCertificates)
        {
            configuration.CertificateValidator.CertificateValidation += (_, e) =>
            {
                e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted
                    || e.Error.StatusCode == StatusCodes.BadCertificateChainIncomplete
                    || e.Error.StatusCode == StatusCodes.BadCertificateTimeInvalid
                    || e.Error.StatusCode == StatusCodes.BadCertificateHostNameInvalid;
            };
        }

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            bool useSecurity = desiredMode != MessageSecurityMode.None;
            try
            {
#pragma warning disable CS0618
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
            catch (OperationCanceledException)
            {
                throw;
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

    private static async Task<string?> TryReadServerProductNameAsync(
        Session session,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadValueIdCollection nodesToRead = new()
            {
                new ReadValueId
                {
                    NodeId = VariableIds.Server_ServerStatus_BuildInfo_ProductName,
                    AttributeId = Attributes.Value
                }
            };

            ReadResponse response = await session.ReadAsync(
                    requestHeader: null,
                    maxAge: 0,
                    timestampsToReturn: TimestampsToReturn.Neither,
                    nodesToRead: nodesToRead,
                    ct: cancellationToken)
                .ConfigureAwait(false);

            DataValueCollection? values = response.Results;
            if (values is { Count: > 0 } && StatusCode.IsGood(values[0].StatusCode))
            {
                return values[0].Value?.ToString();
            }

            // Fallback: session name if product name unavailable.
            return session.SessionName;
        }
        catch
        {
            return null;
        }
    }

    private async Task SafeCloseAndDisposeAsync(IDisposable? session)
    {
        if (session is null)
        {
            return;
        }

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
                    logger_.LogDebug(ex, "Error closing short-lived OPC UA browse session");
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
                    logger_.LogDebug(ex, "Error closing short-lived OPC UA browse session");
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

    private static string ExpandedNodeIdToString(ExpandedNodeId expanded, NamespaceTable namespaces)
    {
        try
        {
            NodeId local = ExpandedNodeId.ToNodeId(expanded, namespaces);
            return local.ToString();
        }
        catch
        {
            return expanded.ToString();
        }
    }

    private static bool MayHaveChildren(NodeClass nodeClass)
    {
        return nodeClass is NodeClass.Object or NodeClass.ObjectType or NodeClass.View
            or NodeClass.VariableType or NodeClass.DataType or NodeClass.ReferenceType;
    }

    private static bool IsTimeoutLike(Exception ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is OperationCanceledException or TimeoutException or TaskCanceledException)
            {
                return true;
            }

            string message = cur.Message ?? string.Empty;
            if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("BadTimeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("BadRequestTimeout", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FlattenMessage(Exception ex)
    {
        if (ex.InnerException is null)
        {
            return ex.Message;
        }

        return $"{ex.Message} ({ex.InnerException.Message})";
    }
}

public sealed record UaTestConnectionResult(
    bool Ok,
    string? Error,
    string? ServerProductName,
    string? SessionId);

public sealed record UaBrowseNodeDto(
    string NodeId,
    string DisplayName,
    string NodeClass,
    bool HasChildren);

public sealed record UaBrowseResult(
    IReadOnlyList<UaBrowseNodeDto> Nodes,
    string? ContinuationPoint,
    string? Error);

public sealed record UaDiscoverResult(
    IReadOnlyList<UaDiscoveredServerDto> Servers,
    string? Error);

public sealed record UaDiscoveredServerDto(
    string? ServerUri,
    uint? RecordId,
    string? DiscoveryUrl,
    string? ServerName,
    List<string>? ServerCapabilities,
    bool IsOnline);
