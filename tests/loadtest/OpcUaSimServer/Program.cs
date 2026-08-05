using System.Diagnostics;
using System.Net;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace OpcUaSimServer;

/// <summary>
/// Load-test OPC UA server: hosts N Double variables ("Tag00001".."Tag{N:00000}")
/// under Objects/Tags (ns=2), all updated every SIM_UPDATE_MS milliseconds.
/// The first SIM_WRITEABLE nodes are writeable (AccessLevel Read|Write): a UA write
/// is accepted and the node is then frozen (no longer overwritten by UpdateAll) so
/// the written value persists and can be read back through a bridge.
/// Env: SIM_NODES (default 20000), SIM_UPDATE_MS (default 1000), SIM_PORT (default 4840),
///      SIM_WRITEABLE (default 10).
/// Endpoint: opc.tcp://0.0.0.0:{SIM_PORT}/opcuasim/  (SecurityMode None, anonymous).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int nodeCount = ParseEnv("SIM_NODES", 20000);
        int updateMs = ParseEnv("SIM_UPDATE_MS", 1000);
        int port = ParseEnv("SIM_PORT", 4840);
        int writeableCount = ParseEnv("SIM_WRITEABLE", 10);
        if (writeableCount > nodeCount)
        {
            writeableCount = nodeCount;
        }

        try
        {
            return RunAsync(nodeCount, updateMs, port, writeableCount).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Exception? current = ex;
            int depth = 0;
            while (current is not null && depth < 10)
            {
                Console.WriteLine($"FATAL[{depth}] {current.GetType().Name}: {current.Message}");
                current = current.InnerException;
                depth++;
            }
            return 1;
        }
    }

    private static int ParseEnv(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
            ? value
            : fallback;
    }

    private static async Task<int> RunAsync(int nodeCount, int updateMs, int port, int writeableCount)
    {
        string endpoint = $"opc.tcp://0.0.0.0:{port}/opcuasim/";
        Console.WriteLine($"Starting sim: {nodeCount} nodes ({writeableCount} writeable), {updateMs} ms update, {endpoint}");

        ApplicationConfiguration configuration = BuildConfiguration(endpoint);
        await configuration.ValidateAsync(ApplicationType.Server).ConfigureAwait(false);

        SimServer server = new(nodeCount, writeableCount, endpoint);
        ApplicationInstance application = new()
        {
            ApplicationName = "OpcUaSimServer",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration
        };
        bool certificateOk = await application.CheckApplicationInstanceCertificatesAsync(false).ConfigureAwait(false);
        if (!certificateOk)
        {
            Console.WriteLine("FATAL: application certificate invalid");
            return 1;
        }
        await application.StartAsync(server).ConfigureAwait(false);

        Console.WriteLine("Sim server started");
        Console.WriteLine($"ENDPOINT {endpoint}");
        Console.WriteLine($"NODES {nodeCount}");

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(updateMs));
        Stopwatch sw = Stopwatch.StartNew();
        long tick = 0;
        while (true)
        {
            try
            {
                await timer.WaitForNextTickAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            tick++;
            server.UpdateAll(tick, sw.ElapsedMilliseconds);
            if (tick % 10 == 0)
            {
                Console.WriteLine($"tick {tick}: {nodeCount} nodes updated");
            }
        }

        server.Dispose();
        return 0;
    }

    private static ApplicationConfiguration BuildConfiguration(string endpoint)
    {
        return new ApplicationConfiguration
        {
            ApplicationName = "OpcUaSimServer",
            ApplicationUri = $"urn:opcuasim:{Dns.GetHostName()}",
            ProductUri = "urn:opcuasim:loadtest",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/own",
                    SubjectName = "OpcUaSimServer"
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
                AutoAcceptUntrustedCertificates = true,
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
                BaseAddresses = new StringCollection { endpoint },
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
                MaxSessionCount = 50,
                MaxSubscriptionCount = 50,
                MaxMessageQueueSize = 1000,
                MaxNotificationQueueSize = 10000,
                MaxPublishRequestCount = 100
            },
            TraceConfiguration = new TraceConfiguration()
        };
    }

    private sealed class SimServer : StandardServer
    {
        private readonly int node_count_;
        private readonly int writeable_count_;
        private SimNodeManager? node_manager_;

        public SimServer(int nodeCount, int writeableCount, string endpoint)
        {
            node_count_ = nodeCount;
            writeable_count_ = writeableCount;
            _ = endpoint;
        }

        public void UpdateAll(long tick, long elapsedMs)
        {
            node_manager_?.UpdateAll(tick, elapsedMs);
        }

        protected override MasterNodeManager CreateMasterNodeManager(
            IServerInternal server,
            ApplicationConfiguration configuration)
        {
            node_manager_ = new SimNodeManager(server, configuration, node_count_, writeable_count_);
            return new MasterNodeManager(server, configuration, null, new INodeManager[] { node_manager_ });
        }

        protected override ServerProperties LoadServerProperties()
        {
            return new ServerProperties
            {
                ManufacturerName = "Oh My Pi",
                ProductName = "OpcUaSimServer (load test)",
                ProductUri = "urn:opcuasim:loadtest",
                SoftwareVersion = "1.0.0",
                BuildNumber = "0",
                BuildDate = DateTime.UtcNow
            };
        }
    }

    private sealed class SimNodeManager : CustomNodeManager2
    {
        private const string NamespaceUri = "urn:opcuasim:server";
        private readonly BaseDataVariableState[] nodes_;
        private readonly bool[] written_;
        private readonly int writeable_count_;
        private ushort namespace_index_;

        public SimNodeManager(IServerInternal server, ApplicationConfiguration configuration, int nodeCount, int writeableCount)
            : base(server, configuration, NamespaceUri)
        {
            nodes_ = new BaseDataVariableState[nodeCount];
            written_ = new bool[nodeCount];
            writeable_count_ = writeableCount;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                namespace_index_ = Server.NamespaceUris.GetIndexOrAppend(NamespaceUri);

                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference>? references))
                {
                    references = new List<IReference>();
                    externalReferences[ObjectIds.ObjectsFolder] = references;
                }

                FolderState root = new(null)
                {
                    SymbolicName = "Tags",
                    ReferenceTypeId = ReferenceTypeIds.Organizes,
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                    NodeId = new NodeId("Tags", namespace_index_),
                    BrowseName = new QualifiedName("Tags", namespace_index_),
                    DisplayName = new LocalizedText("Tags"),
                    WriteMask = AttributeWriteMask.None,
                    UserWriteMask = AttributeWriteMask.None,
                    EventNotifier = EventNotifiers.None
                };
                root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));
                AddPredefinedNode(SystemContext, root);

                for (int i = 0; i < nodes_.Length; i++)
                {
                    string name = $"Tag{i + 1:00000}";
                    bool writeable = i < writeable_count_;
                    byte accessLevel = writeable
                        ? (byte)(AccessLevels.CurrentRead | AccessLevels.CurrentWrite)
                        : AccessLevels.CurrentRead;
                    BaseDataVariableState variable = new(root)
                    {
                        SymbolicName = name,
                        ReferenceTypeId = ReferenceTypeIds.Organizes,
                        TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                        NodeId = new NodeId(name, namespace_index_),
                        BrowseName = new QualifiedName(name, namespace_index_),
                        DisplayName = new LocalizedText(name),
                        WriteMask = AttributeWriteMask.None,
                        UserWriteMask = AttributeWriteMask.None,
                        DataType = DataTypeIds.Double,
                        ValueRank = ValueRanks.Scalar,
                        AccessLevel = accessLevel,
                        UserAccessLevel = accessLevel,
                        Historizing = false,
                        Value = new DataValue(new Variant(0.0)),
                        StatusCode = StatusCodes.Good,
                        Timestamp = DateTime.UtcNow
                    };
                    if (writeable)
                    {
                        variable.OnWriteValue = HandleWriteValue;
                    }

                    root.AddChild(variable);
                    AddPredefinedNode(SystemContext, variable);
                    nodes_[i] = variable;
                }
            }
        }

        public void UpdateAll(long tick, long elapsedMs)
        {
            double t = elapsedMs / 1000.0;
            DateTime ts = DateTime.UtcNow;
            for (int i = 0; i < nodes_.Length; i++)
            {
                if (written_[i])
                {
                    // UA client wrote this node; keep the written value frozen so a
                    // bridge can read it back and prove the write landed.
                    continue;
                }

                double value = 100.0 + 10.0 * Math.Sin(t + (i * 0.001));
                BaseDataVariableState variable = nodes_[i];
                variable.Value = new Variant(value);
                variable.Timestamp = ts;
                variable.StatusCode = StatusCodes.Good;
                variable.ClearChangeMasks(SystemContext, false);
            }
        }

        private ServiceResult HandleWriteValue(
            ISystemContext context,
            NodeState node,
            NumericRange range,
            QualifiedName componentName,
            ref object value,
            ref StatusCode statusCode,
            ref DateTime timestamp)
        {
            for (int i = 0; i < nodes_.Length; i++)
            {
                if (ReferenceEquals(nodes_[i], node))
                {
                    written_[i] = true;
                    break;
                }
            }

            if (node is BaseDataVariableState variable)
            {
                variable.Value = new DataValue(new Variant(value), statusCode, timestamp);
            }

            return ServiceResult.Good;
        }
    }
}
