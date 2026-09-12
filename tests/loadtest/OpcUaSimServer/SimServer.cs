using Opc.Ua;
using Opc.Ua.Server;

namespace OpcUaSimServer;

/// <summary>One row of the tag dashboard: current value, effective rate, frozen state.</summary>
public sealed record TagRow(int Number, string Name, double Value, int RateMs, bool Frozen, bool Writeable);

/// <summary>Snapshot of the tag table for the control API.</summary>
public sealed record TagSnapshot(int Total, List<TagRow> Tags);

/// <summary>
/// An auto-toggling Boolean status node, e.g. a pump run signal. <paramref name="PeriodMs"/>
/// is the half-period: the value flips every PeriodMs, giving a square wave.
/// </summary>
public sealed record StatusTagSpec(string Name, int PeriodMs);

/// <summary>
/// The OPC UA server. Hosts N Double variables ("Tag00001".."Tag{N:00000}") under
/// Objects/Tags (ns=2). Each tag has its own update rate (defaults to the global rate);
/// a tag only gets a new value once its own interval has elapsed, so different tags can
/// tick at different cadences from a single timer. Optional Boolean status nodes live
/// under Objects/Status and toggle themselves (no write or client involvement).
/// </summary>
internal sealed class SimServer : StandardServer
{
    private readonly int node_count_;
    private readonly int writeable_count_;
    private readonly HashSet<int> bad_tags_;
    private readonly int bad_after_ms_;
    private readonly HashSet<int> extra_tags_;
    private readonly int extra_after_ms_;
    private readonly int global_rate_ms_;
    private readonly IReadOnlyList<StatusTagSpec> status_tags_;
    private SimNodeManager? node_manager_;

    public SimServer(
        int nodeCount,
        int writeableCount,
        HashSet<int> badTags,
        int badAfterMs,
        HashSet<int> extraTags,
        int extraAfterMs,
        int globalRateMs,
        string endpoint,
        IReadOnlyList<StatusTagSpec>? statusTags = null)
    {
        node_count_ = nodeCount;
        writeable_count_ = writeableCount;
        bad_tags_ = badTags;
        bad_after_ms_ = badAfterMs;
        extra_tags_ = extraTags;
        extra_after_ms_ = extraAfterMs;
        global_rate_ms_ = globalRateMs;
        status_tags_ = statusTags ?? Array.Empty<StatusTagSpec>();
        _ = endpoint;
    }

    /// <summary>Number of nodes that received a fresh value on this tick (for logging).</summary>
    public int UpdateAll(long tick, long elapsedMs)
    {
        return node_manager_?.UpdateAll(tick, elapsedMs) ?? 0;
    }

    // ---- control surface (called from the HTTP control server on other threads) ----

    public int NodeCount => node_count_;

    public int GlobalRateMs => node_manager_?.GlobalRateMs ?? 1000;

    public void SetGlobalRate(int rateMs) => node_manager_?.SetGlobalRate(rateMs);

    public void SetAllRates(int rateMs) => node_manager_?.SetAllRates(rateMs);

    /// <summary>Set the update rate of a 1-based tag number. Returns false if out of range.</summary>
    public bool SetTagRate(int number, int rateMs) => node_manager_?.SetTagRate(number, rateMs) ?? false;

    /// <summary>Smallest effective rate across all tags; the main loop ticks at this period.</summary>
    public int GetMinRateMs() => node_manager_?.GetMinRateMs() ?? 1000;

    public TagSnapshot GetTags(int offset, int limit, string? filter) =>
        node_manager_?.GetTags(offset, limit, filter) ?? new TagSnapshot(0, new List<TagRow>());

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        node_manager_ = new SimNodeManager(
            server,
            configuration,
            node_count_,
            writeable_count_,
            bad_tags_,
            bad_after_ms_,
            extra_tags_,
            extra_after_ms_,
            global_rate_ms_,
            status_tags_);
        return new MasterNodeManager(server, configuration, null, new INodeManager[] { node_manager_ });
    }

    protected override ServerProperties LoadServerProperties()
    {
        return new ServerProperties
        {
            ManufacturerName = "Oh My Pi",
            ProductName = "OpcUaSimServer (load test)",
            ProductUri = "urn:opcuasim:loadtest",
            SoftwareVersion = "1.1.0",
            BuildNumber = "0",
            BuildDate = DateTime.UtcNow
        };
    }
}

internal sealed class SimNodeManager : CustomNodeManager2
{
    private const string NamespaceUri = "urn:opcuasim:server";
    private readonly BaseDataVariableState[] nodes_;
    private readonly List<BaseDataVariableState> extra_nodes_ = new();
    private readonly List<long> extra_last_update_ms_ = new();
    private readonly bool[] written_;
    private readonly bool[] bad_;
    private readonly HashSet<int> bad_numbers_;
    private readonly HashSet<int> extra_numbers_;
    private readonly int writeable_count_;
    private readonly int bad_after_ms_;
    private readonly int extra_after_ms_;
    private readonly IReadOnlyList<StatusTagSpec> status_specs_;
    private readonly List<BaseDataVariableState> status_nodes_ = new();
    private readonly List<long> status_last_toggle_ms_ = new();
    private readonly List<bool> status_value_ = new();
    private bool bad_activated_;
    private bool extra_activated_;
    private FolderState? root_;
    private FolderState? status_root_;
    private ushort namespace_index_;

    // Per-tag rate control. The control server mutates these on its own threads while
    // UpdateAll (main loop thread) reads them, so all access goes through gate_.
    private readonly object gate_ = new();
    private readonly int[] tag_rates_ms_;
    private readonly long[] last_update_ms_;
    private int global_rate_ms_;

    public SimNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration,
        int nodeCount,
        int writeableCount,
        HashSet<int> badTags,
        int badAfterMs,
        HashSet<int> extraTags,
        int extraAfterMs,
        int globalRateMs = 1000,
        IReadOnlyList<StatusTagSpec>? statusTags = null)
        : base(server, configuration, NamespaceUri)
    {
        nodes_ = new BaseDataVariableState[nodeCount];
        written_ = new bool[nodeCount];
        bad_ = new bool[nodeCount];
        tag_rates_ms_ = new int[nodeCount];
        last_update_ms_ = new long[nodeCount];
        global_rate_ms_ = ClampRate(globalRateMs);
        for (int i = 0; i < tag_rates_ms_.Length; i++)
        {
            tag_rates_ms_[i] = global_rate_ms_;
        }

        bad_numbers_ = badTags;
        extra_numbers_ = extraTags;
        bad_after_ms_ = badAfterMs;
        extra_after_ms_ = extraAfterMs;
        writeable_count_ = writeableCount;
        status_specs_ = statusTags ?? Array.Empty<StatusTagSpec>();
    }

    private static int ClampRate(int rateMs) => Math.Clamp(rateMs, 10, 60000);

    /// <summary>Keep a status toggle period sane: fast enough to look live, never a busy loop.</summary>
    private static int ClampStatusPeriod(int periodMs) => Math.Clamp(periodMs, 250, 3_600_000);

    public int GlobalRateMs
    {
        get
        {
            lock (gate_)
            {
                return global_rate_ms_;
            }
        }
    }

    public void SetGlobalRate(int rateMs)
    {
        lock (gate_)
        {
            global_rate_ms_ = ClampRate(rateMs);
        }
    }

    /// <summary>
    /// Set every tag's rate to the same value (the dashboard "Apply to all" button).
    /// Unlike SetGlobalRate this overwrites the per-tag overrides, so the change is
    /// immediately visible on every tag.
    /// </summary>
    public void SetAllRates(int rateMs)
    {
        int clamped = ClampRate(rateMs);
        lock (gate_)
        {
            global_rate_ms_ = clamped;
            for (int i = 0; i < tag_rates_ms_.Length; i++)
            {
                tag_rates_ms_[i] = clamped;
            }
        }
    }

    public bool SetTagRate(int number, int rateMs)
    {
        lock (gate_)
        {
            int index = number - 1;
            if (index < 0 || index >= nodes_.Length)
            {
                return false;
            }

            tag_rates_ms_[index] = ClampRate(rateMs);
            return true;
        }
    }

    public int GetMinRateMs()
    {
        lock (gate_)
        {
            int min = global_rate_ms_;
            foreach (int rate in tag_rates_ms_)
            {
                if (rate < min)
                {
                    min = rate;
                }
            }

            return min;
        }
    }

    public TagSnapshot GetTags(int offset, int limit, string? filter)
    {
        lock (gate_)
        {
            List<TagRow> rows = new();
            int total = 0;
            for (int i = 0; i < nodes_.Length; i++)
            {
                string name = $"Tag{i + 1:00000}";
                if (!string.IsNullOrEmpty(filter)
                    && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (total >= offset && rows.Count < limit)
                {
                    rows.Add(BuildRow(i, name));
                }

                total++;
            }

            return new TagSnapshot(total, rows);
        }
    }

    private TagRow BuildRow(int index, string name)
    {
        BaseDataVariableState variable = nodes_[index];
        bool frozen = written_[index] || bad_[index];
        return new TagRow(
            index + 1,
            name,
            ReadDouble(variable),
            tag_rates_ms_[index],
            frozen,
            index < writeable_count_);
    }

    /// <summary>Unwrap the node's stored value (double, Variant, or DataValue) to a double.</summary>
    private static double ReadDouble(BaseDataVariableState variable)
    {
        object? raw = variable.Value;
        if (raw is DataValue dv)
        {
            raw = dv.Value;
        }

        if (raw is Variant v)
        {
            raw = v.Value;
        }

        return raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => double.NaN
        };
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
            root_ = root;

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

            CreateStatusNodes(references);
        }
    }

    /// <summary>
    /// Create the optional Objects/Status folder with one self-toggling Boolean variable per
    /// spec (read-only). Values are driven only by UpdateAll, so a bridge sees them change
    /// without anything ever writing to them.
    /// </summary>
    private void CreateStatusNodes(IList<IReference> objectsReferences)
    {
        if (status_specs_.Count == 0)
        {
            return;
        }

        FolderState status = new(null)
        {
            SymbolicName = "Status",
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId("Status", namespace_index_),
            BrowseName = new QualifiedName("Status", namespace_index_),
            DisplayName = new LocalizedText("Status"),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None
        };
        status.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        objectsReferences.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, status.NodeId));
        AddPredefinedNode(SystemContext, status);
        status_root_ = status;

        foreach (StatusTagSpec spec in status_specs_)
        {
            string browseName = string.IsNullOrWhiteSpace(spec.Name) ? "Status" : spec.Name.Trim();
            BaseDataVariableState variable = new(status)
            {
                SymbolicName = browseName,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId($"Status/{browseName}", namespace_index_),
                BrowseName = new QualifiedName(browseName, namespace_index_),
                DisplayName = new LocalizedText(browseName),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                DataType = DataTypeIds.Boolean,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Historizing = false,
                Value = new DataValue(new Variant(false)),
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow
            };
            status.AddChild(variable);
            AddPredefinedNode(SystemContext, variable);
            status_nodes_.Add(variable);
            status_last_toggle_ms_.Add(0);
            status_value_.Add(false);
        }
    }

    /// <summary>Update each tag only when its own rate interval has elapsed. Returns the count updated.</summary>
    public int UpdateAll(long tick, long elapsedMs)
    {
        if (!bad_activated_ && bad_numbers_.Count > 0
            && (bad_after_ms_ == 0 || elapsedMs >= bad_after_ms_))
        {
            ActivateBadTags();
        }

        if (!extra_activated_ && extra_numbers_.Count > 0
            && (extra_after_ms_ == 0 || elapsedMs >= extra_after_ms_))
        {
            ActivateExtraTags();
        }

        double t = elapsedMs / 1000.0;
        DateTime ts = DateTime.UtcNow;
        int updated = 0;
        for (int i = 0; i < nodes_.Length; i++)
        {
            if (written_[i] || bad_[i])
            {
                // UA client wrote this node, or it is fault-injected bad: keep the
                // current state frozen so a bridge can observe it.
                continue;
            }

            if (elapsedMs - last_update_ms_[i] < tag_rates_ms_[i])
            {
                continue;
            }

            double value = 100.0 + 10.0 * Math.Sin(t + (i * 0.001));
            BaseDataVariableState variable = nodes_[i];
            variable.Value = new Variant(value);
            variable.Timestamp = ts;
            variable.StatusCode = StatusCodes.Good;
            variable.ClearChangeMasks(SystemContext, false);
            last_update_ms_[i] = elapsedMs;
            updated++;
        }

        for (int i = 0; i < extra_nodes_.Count; i++)
        {
            if (elapsedMs - extra_last_update_ms_[i] < global_rate_ms_)
            {
                continue;
            }

            double value = 100.0 + 10.0 * Math.Sin(t + ((nodes_.Length + i) * 0.001));
            BaseDataVariableState variable = extra_nodes_[i];
            variable.Value = new Variant(value);
            variable.Timestamp = ts;
            variable.StatusCode = StatusCodes.Good;
            variable.ClearChangeMasks(SystemContext, false);
            extra_last_update_ms_[i] = elapsedMs;
            updated++;
        }

        for (int i = 0; i < status_nodes_.Count; i++)
        {
            if (elapsedMs - status_last_toggle_ms_[i] < ClampStatusPeriod(status_specs_[i].PeriodMs))
            {
                continue;
            }

            bool next = !status_value_[i];
            status_value_[i] = next;
            status_last_toggle_ms_[i] = elapsedMs;
            BaseDataVariableState variable = status_nodes_[i];
            variable.Value = new Variant(next);
            variable.Timestamp = ts;
            variable.StatusCode = StatusCodes.Good;
            variable.ClearChangeMasks(SystemContext, false);
            updated++;
        }

        return updated;
    }

    /// <summary>
    /// Add SIM_EXTRA_TAGS nodes to the address space at runtime (after SIM_EXTRA_AFTER_MS),
    /// simulating a tag that appears at the source later. A bridge whose monitored-item
    /// create failed earlier picks it up via its retry timer.
    /// </summary>
    private void ActivateExtraTags()
    {
        extra_activated_ = true;
        if (root_ is null)
        {
            return;
        }

        foreach (int number in extra_numbers_)
        {
            int index = number - 1;
            if (index >= 0 && index < nodes_.Length)
            {
                continue; // already part of the base address space
            }

            string name = $"Tag{number:00000}";
            BaseDataVariableState variable = new(root_)
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
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Historizing = false,
                Value = new DataValue(new Variant(100.0)),
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow
            };
            root_.AddChild(variable);
            AddPredefinedNode(SystemContext, variable);
            extra_nodes_.Add(variable);
            extra_last_update_ms_.Add(0);
        }
    }

    /// <summary>Flip fault-injected tags to BadOutOfService (frozen) so a bridge sees the quality transition.</summary>
    private void ActivateBadTags()
    {
        bad_activated_ = true;
        foreach (int number in bad_numbers_)
        {
            int index = number - 1;
            if (index < 0 || index >= nodes_.Length)
            {
                continue;
            }

            bad_[index] = true;
            BaseDataVariableState variable = nodes_[index];
            variable.StatusCode = StatusCodes.BadOutOfService;
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
