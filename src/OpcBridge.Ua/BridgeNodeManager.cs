using Opc.Ua;
using Opc.Ua.Server;
using OpcBridge.Core;

namespace OpcBridge.Ua;

internal sealed class BridgeNodeManager : CustomNodeManager2
{
    private const string NamespaceUri = "urn:ohmypi:opc-da-to-ua-bridge:tags";
    private readonly IReadOnlyList<TagMapping> mappings_;
    private readonly Dictionary<string, BaseDataVariableState> variables_by_mapping_key_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<NodeId, (string SourceId, string DaItemId)> node_to_mapping_ = new();
    private Action<BridgeValue, TaskCompletionSource<bool>>? write_handler_;
    private FolderState? root_folder_;
    private ushort namespace_index_;
    private DateTime? last_value_update_utc_;
    private long total_notifications_;
    private long notifications_window_start_ticks_;
    private long notifications_in_window_;

    private static readonly double TicksPerSecond = TimeSpan.FromSeconds(1).Ticks;

    public BridgeNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration,
        IReadOnlyList<TagMapping> mappings)
        : base(server, configuration, NamespaceUri)
    {
        mappings_ = mappings;
        SystemContext.NodeIdFactory = this;
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

            FolderState root = CreateFolder(null, "OpcDaTags", "OPC DA Tags");
            root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));
            AddPredefinedNode(SystemContext, root);
            root_folder_ = root;

            for (int i = 0; i < mappings_.Count; i++)
            {
                TagMapping mapping = mappings_[i];
                BaseDataVariableState variable = CreateVariable(root, mapping);
                variables_by_mapping_key_[GetMappingKey(mapping.SourceId, mapping.DaItemId)] = variable;
                node_to_mapping_[variable.NodeId] = (mapping.SourceId, mapping.DaItemId);
                AddPredefinedNode(SystemContext, variable);
            }
        }
    }

    public void AddMapping(TagMapping mapping)
    {
        lock (Lock)
        {
            string key = GetMappingKey(mapping.SourceId, mapping.DaItemId);
            if (root_folder_ is null || variables_by_mapping_key_.ContainsKey(key))
            {
                return;
            }

            BaseDataVariableState variable = CreateVariable(root_folder_, mapping);
            variables_by_mapping_key_[key] = variable;
            node_to_mapping_[variable.NodeId] = (mapping.SourceId, mapping.DaItemId);
            AddPredefinedNode(SystemContext, variable);
        }
    }

    public void RemoveMapping(string sourceId, string daItemId)
    {
        lock (Lock)
        {
            string key = GetMappingKey(sourceId, daItemId);
            if (!variables_by_mapping_key_.TryGetValue(key, out BaseDataVariableState? variable))
            {
                return;
            }

            variable.Parent?.RemoveChild(variable);
            DeleteNode(SystemContext, variable.NodeId);
            variables_by_mapping_key_.Remove(key);
        }
    }

    public IReadOnlyCollection<string> GetMappedKeys()
    {
        lock (Lock)
        {
            return variables_by_mapping_key_.Keys.ToArray();
        }
    }

    public int GetMappedNodeCount()
    {
        lock (Lock)
        {
            return variables_by_mapping_key_.Count;
        }
    }

    public DateTime? GetLastValueUpdateUtc()
    {
        lock (Lock)
        {
            return last_value_update_utc_;
        }
    }

    public void UpdateValue(BridgeValue value)
    {
        lock (Lock)
        {
            if (!variables_by_mapping_key_.TryGetValue(GetMappingKey(value.SourceId, value.DaItemId), out BaseDataVariableState? variable))
            {
                return;
            }

            variable.Value = value.Value;
            variable.Timestamp = value.TimestampUtc;
            variable.StatusCode = value.IsGood ? StatusCodes.Good : StatusCodes.Bad;
            variable.ClearChangeMasks(SystemContext, false);
            last_value_update_utc_ = DateTime.UtcNow;
            Interlocked.Increment(ref total_notifications_);

            long nowTicks = DateTime.UtcNow.Ticks;
            long elapsed = nowTicks - Interlocked.Read(ref notifications_window_start_ticks_);
            if (elapsed >= TicksPerSecond)
            {
                Interlocked.Exchange(ref notifications_window_start_ticks_, nowTicks);
                Interlocked.Exchange(ref notifications_in_window_, 0);
            }
            Interlocked.Increment(ref notifications_in_window_);
        }
    }

    public (long TotalNotifications, double NotificationsPerSec) GetBandwidthEstimate()
    {
        long total = Interlocked.Read(ref total_notifications_);
        long inWindow = Interlocked.Read(ref notifications_in_window_);
        return (total, inWindow);
    }
    public void SetWriteHandler(Action<BridgeValue, TaskCompletionSource<bool>> handler)
    {
        write_handler_ = handler;
    }


    private FolderState CreateFolder(NodeState? parent, string path, string name)
    {
        FolderState folder = new(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(path, namespace_index_),
            BrowseName = new QualifiedName(name, namespace_index_),
            DisplayName = new LocalizedText(name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None
        };

        parent?.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(FolderState parent, TagMapping mapping)
    {
        NodeId dataType = ToDataTypeId(mapping.DataType);
        byte accessLevel = mapping.Writeable
            ? (byte)(AccessLevels.CurrentRead | AccessLevels.CurrentWrite)
            : AccessLevels.CurrentRead;
        AttributeWriteMask writeMask = AttributeWriteMask.None;

        BaseDataVariableState variable = new(parent)
        {
            SymbolicName = mapping.DisplayName,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(ToNodeIdentifier(mapping), namespace_index_),
            BrowseName = new QualifiedName(mapping.DisplayName, namespace_index_),
            DisplayName = new LocalizedText(mapping.DisplayName),
            Description = new LocalizedText($"{mapping.SourceId}:{mapping.DaItemId}"),
            WriteMask = writeMask,
            UserWriteMask = writeMask,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = accessLevel,
            UserAccessLevel = accessLevel,
            Historizing = false,
            Value = CreateInitialValue(mapping.DataType),
            StatusCode = StatusCodes.BadWaitingForInitialData,
            Timestamp = DateTime.UtcNow
        };

        parent.AddChild(variable);

        if (mapping.Writeable)
        {
            variable.OnWriteValue = HandleWriteValue;
        }

        return variable;
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
        if (write_handler_ is null || !node_to_mapping_.TryGetValue(node.NodeId, out (string SourceId, string DaItemId) mapping))
        {
            return StatusCodes.BadWriteNotSupported;
        }

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BridgeValue bridgeValue = new(mapping.SourceId, mapping.DaItemId, value, DateTime.UtcNow, 192, true);
        write_handler_(bridgeValue, tcs);

        if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            return StatusCodes.BadRequestTimeout;
        }

        bool success = tcs.Task.Result;
        if (!success)
        {
            return StatusCodes.BadNoCommunication;
        }

        return ServiceResult.Good;
    }


    private static string GetMappingKey(string sourceId, string daItemId)
    {
        return string.Concat(sourceId.Trim(), "::", daItemId.Trim());
    }

    private static string ToNodeIdentifier(TagMapping mapping)
    {
        string nodeId = mapping.UaNodeId.Trim();
        if (nodeId.Length == 0)
        {
            return $"{mapping.SourceId}/{mapping.DaItemId}";
        }

        int stringMarker = nodeId.IndexOf(";s=", StringComparison.OrdinalIgnoreCase);
        if (stringMarker >= 0)
        {
            return nodeId[(stringMarker + 3)..];
        }

        return nodeId;
    }

    private static NodeId ToDataTypeId(string dataType)
    {
        return dataType.Trim().ToUpperInvariant() switch
        {
            "BOOL" or "BOOLEAN" => DataTypeIds.Boolean,
            "BYTE" => DataTypeIds.Byte,
            "INT16" or "SHORT" => DataTypeIds.Int16,
            "INT32" or "INT" => DataTypeIds.Int32,
            "INT64" or "LONG" => DataTypeIds.Int64,
            "FLOAT" or "SINGLE" => DataTypeIds.Float,
            "DOUBLE" or "REAL8" => DataTypeIds.Double,
            "STRING" => DataTypeIds.String,
            _ => DataTypeIds.BaseDataType
        };
    }

    private static object CreateInitialValue(string dataType)
    {
        return dataType.Trim().ToUpperInvariant() switch
        {
            "BOOL" or "BOOLEAN" => false,
            "BYTE" => (byte)0,
            "INT16" or "SHORT" => (short)0,
            "INT32" or "INT" => 0,
            "INT64" or "LONG" => 0L,
            "FLOAT" or "SINGLE" => 0.0f,
            "DOUBLE" or "REAL8" => 0.0,
            "STRING" => string.Empty,
            _ => 0.0
        };
    }
}
