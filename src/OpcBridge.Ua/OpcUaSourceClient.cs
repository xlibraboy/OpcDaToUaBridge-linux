using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OpcBridge.Core;
using OpcBridge.Da;

namespace OpcBridge.Ua;

public sealed class OpcUaSourceClient : IDaClient, ISubscribableSourceClient
{
    private const int ReadChunkSize = 500;

    private readonly OpcUaSourceClientOptions options_;
    private readonly ILogger logger_;
    private readonly object gate_ = new();
    private readonly DefaultSessionFactory session_factory_ =
#pragma warning disable CS0618 // No ITelemetryContext on source client yet.
        new();
#pragma warning restore CS0618

    private ApplicationConfiguration? configuration_;
    private Session? session_;
    private bool disposed_;

    /// <summary>
    /// Raised when a UA subscription delivers values. Wired in Task 5.
    /// </summary>
#pragma warning disable CS0067 // Event reserved for Task 5 subscription notifications.
    public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived;
#pragma warning restore CS0067

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
                created.Dispose();
                throw new InvalidOperationException(
                    $"OPC UA session factory returned unexpected type '{created.GetType().FullName}'.");
            }

            lock (gate_)
            {
                ObjectDisposedException.ThrowIf(disposed_, this);
                session_ = session;
                configuration_ = configuration;
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
                if (string.IsNullOrWhiteSpace(mapping.DaItemId))
                {
                    continue;
                }

                if (!NodeId.TryParse(mapping.DaItemId.Trim(), out NodeId? nodeId) || nodeId is null)
                {
                    results.Add(new BridgeValue(
                        options_.SourceId,
                        mapping.DaItemId,
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

                results.Add(ToBridgeValue(mapping.DaItemId, dataValue));
            }
        }

        return results;
    }

    public async Task<bool> WriteAsync(string daItemId, object? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(daItemId))
        {
            return false;
        }

        Session session = GetConnectedSession();
        if (!NodeId.TryParse(daItemId.Trim(), out NodeId? nodeId) || nodeId is null)
        {
            return false;
        }

        WriteValue writeValue = new()
        {
            NodeId = nodeId,
            AttributeId = Attributes.Value,
            Value = new DataValue
            {
                Value = value,
                StatusCode = StatusCodes.Good,
                SourceTimestamp = DateTime.UtcNow
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

    public bool TryGetTagMetadata(string daItemId, out short? canonicalDataType, out int? accessRights)
    {
        canonicalDataType = null;
        accessRights = null;

        if (string.IsNullOrWhiteSpace(daItemId))
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

        if (!NodeId.TryParse(daItemId.Trim(), out NodeId? nodeId) || nodeId is null)
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
            logger_.LogDebug(ex, "TryGetTagMetadata failed for {NodeId}", daItemId);
            return false;
        }
    }

    /// <summary>
    /// Task 5 fills subscription reconcile. Stub keeps poll path active for now.
    /// </summary>
    public Task ReconcileMonitoredItemsAsync(
        IReadOnlyList<TagMapping> desiredMappings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = desiredMappings;
        // Subscriptions not active until Task 5.
        return Task.CompletedTask;
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
        }

        if (session is null)
        {
            return;
        }

        try
        {
            await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "Error closing OPC UA session for source {SourceId}", options_.SourceId);
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

    private BridgeValue ToBridgeValue(string daItemId, DataValue dataValue)
    {
        (int daQuality, bool isGood) = UaQualityMapper.FromStatusCode(dataValue.StatusCode.Code);
        DateTime timestamp = ResolveTimestamp(dataValue);
        return new BridgeValue(options_.SourceId, daItemId, dataValue.Value, timestamp, daQuality, isGood);
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
        string applicationUri = $"urn:ohmypi:{applicationName}:{options_.SourceId}";

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
