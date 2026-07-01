using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Configuration;
using OpcBridge.Core;

namespace OpcBridge.Ua;

public sealed class UaServerHost : IAsyncDisposable
{
    private readonly UaServerOptions options_;
    private readonly ILogger<UaServerHost> logger_;
    private readonly ILoggerFactory logger_factory_;
    private BridgeUaServer? server_;

    public UaServerHost(
        IOptions<UaServerOptions> options,
        ILogger<UaServerHost> logger,
        ILoggerFactory loggerFactory)
    {
        options_ = options.Value;
        logger_ = logger;
        logger_factory_ = loggerFactory;
    }

    public async Task StartAsync(IReadOnlyList<TagMapping> mappings, CancellationToken cancellationToken)
    {
        ApplicationConfiguration configuration = CreateConfiguration();
        await configuration.ValidateAsync(ApplicationType.Server).ConfigureAwait(false);

        ApplicationInstance application = new(new BridgeTelemetryContext(logger_factory_))
        {
            ApplicationName = options_.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration
        };

        bool certificateOk = await application
            .CheckApplicationInstanceCertificatesAsync(false)
            .ConfigureAwait(false);
        if (!certificateOk)
        {
            throw new InvalidOperationException("OPC UA application certificate is invalid.");
        }

        server_ = new BridgeUaServer(mappings);
        await application.StartAsync(server_).ConfigureAwait(false);
        logger_.LogInformation("OPC UA server started at {EndpointUrl}", options_.EndpointUrl);
    }

    public void UpdateValue(BridgeValue value)
    {
        server_?.UpdateValue(value);
    }

    public void SyncMappings(IReadOnlyList<TagMapping> mappings)
    {
        server_?.SyncMappings(mappings);
    }
    public void SetWriteHandler(Action<BridgeValue, TaskCompletionSource<bool>> handler)
    {
        server_?.SetWriteHandler(handler);
    }


    public UaServerStatus GetStatus()
    {
        BridgeUaServer? server = server_;
        return new UaServerStatus(
            server is not null ? "Running" : "Stopped",
            options_.EndpointUrl,
            server?.GetConnectedSessionCount() ?? 0,
            server?.GetMappedNodeCount() ?? 0,
            server?.GetLastValueUpdateUtc());
    }

    public IReadOnlyList<UaSessionDiagnostic> GetSessionDiagnostics()
    {
        return server_?.GetSessionDiagnostics() ?? Array.Empty<UaSessionDiagnostic>();
    }

    public IReadOnlyList<UaSubscriptionDiagnostic> GetSubscriptionDiagnostics()
    {
        return server_?.GetSubscriptionDiagnostics() ?? Array.Empty<UaSubscriptionDiagnostic>();
    }

    public (long TotalNotifications, double NotificationsPerSec) GetBandwidthEstimate()
    {
        return server_?.GetBandwidthEstimate() ?? (0, 0);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (server_ is null)
        {
            return;
        }

        await server_.StopAsync(cancellationToken).ConfigureAwait(false);
        server_ = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (server_ is not null)
        {
            await server_.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private ApplicationConfiguration CreateConfiguration()
    {
        string applicationUri = $"urn:ohmypi:{options_.ApplicationName}";

        return new ApplicationConfiguration
        {
            ApplicationName = options_.ApplicationName,
            ApplicationUri = applicationUri,
            ProductUri = "urn:ohmypi:opc-da-to-ua-bridge",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/own",
                    SubjectName = options_.ApplicationName
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/trusted"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/issuers"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/rejected"
                },
                AutoAcceptUntrustedCertificates = options_.AutoAcceptUntrustedCertificates,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15000,
                MaxStringLength = 1048576,
                MaxByteStringLength = 1048576,
                MaxArrayLength = 65535,
                MaxMessageSize = 4194304,
                MaxBufferSize = 65535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = new StringCollection { options_.EndpointUrl },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None
                    }
                },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxSessionCount = 100,
                MaxSubscriptionCount = 100,
                MaxMessageQueueSize = 100,
                MaxNotificationQueueSize = 100,
                MaxPublishRequestCount = 20
            },
            TraceConfiguration = new TraceConfiguration()
        };
    }

    private sealed class BridgeTelemetryContext : TelemetryContextBase
    {
        public BridgeTelemetryContext(ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
        }
    }
}

public sealed record UaServerStatus(
    string State,
    string EndpointUrl,
    int ConnectedClientCount,
    int MappedNodeCount,
    DateTime? LastValueUpdateUtc);
