using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using OpcBridge.Core;

namespace OpcBridge.Da;

public sealed class OpcDaClient : ISourceClient, ISubscribableSourceClient, ISubscriptionActiveSource
{
    private const int OpcDataSourceDevice = 2;
    private static readonly int ItemStateSize = Marshal.SizeOf<OpcItemState>();
    private static readonly int ItemValueOffset = (int)Marshal.OffsetOf<OpcItemState>(nameof(OpcItemState.Value));

    private readonly DaClientOptions options_;

    /// <summary>Client options; exposed for tests and diagnostics.</summary>
    public DaClientOptions Options => options_;
    private OpcComThread? com_thread_;
    private object? server_com_object_;
    private IOPCServer? server_;
    private OpcDaServerInfo? server_info_;
    private readonly Dictionary<int, RateGroup> rate_groups_ = new();
    private bool subscriptions_active_;
    private readonly HashSet<int> subscription_fallback_warned_rates_ = new();

    /// <summary>
    /// Raised when a DA subscription delivers values via IOPCDataCallback.
    /// Subscribed to once per session by BridgeWorker via <see cref="ISubscribableSourceClient"/>.
    /// </summary>
    public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived;

    /// <summary>
    /// Raised on non-fatal operational warnings (e.g. subscription setup failing so a
    /// group silently falls back to polling). Subscribed by BridgeWorker for logging.
    /// </summary>
    public event Action<string>? Warning;

    /// <summary>
    /// Detected OPC DA server identity (spec level, server version, vendor) after a
    /// successful connect. Null before connect or when detection is unavailable.
    /// </summary>
    public OpcDaServerInfo? ServerInfo => server_info_;

    /// <summary>
    /// True when DA subscriptions (IOPCDataCallback) are established so values arrive
    /// via callbacks and <see cref="ReadAsync"/> performs no device reads; false when
    /// the source is polling via IOPCSyncIO.Read (subscriptions disabled or unsupported).
    /// </summary>
    public bool IsSubscriptionActive => subscriptions_active_;

    public OpcDaClient(DaClientOptions options)
    {
        options_ = options;
    }

    public (bool Alive, int QueuedItems, DateTime? LastActionUtc)? GetStaThreadStats()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return com_thread_?.GetStats();
    }

    [SupportedOSPlatform("windows")]
    public bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights)
    {
        canonicalDataType = null;
        accessRights = null;

        if (!OperatingSystem.IsWindows() || com_thread_ is null || string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        (bool found, short? canonicalType, int? rights) = com_thread_.EnqueueAndWait(
            () => TryGetTagMetadataOnStaThread(itemId.Trim()));
        canonicalDataType = canonicalType;
        accessRights = rights;
        return found;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (server_ is not null)
        {
            return Task.CompletedTask;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA mode requires Windows because it uses COM/DCOM.");
        }

        string progId = options_.ProgId.Trim();
        if (progId.Length == 0)
        {
            throw new InvalidOperationException("Da:ProgId must be configured when Da:Mode is OpcDa.");
        }

        string? host = NormalizeHost(options_.Host);

        // Pin all COM work for this source to a dedicated STA thread.
        com_thread_ = new OpcComThread($"OpcDa-{options_.SourceId}");
        com_thread_.Start();

        // Use impersonation for remote connections with explicit credentials
        bool hasCredentials = host is not null
            && !string.IsNullOrWhiteSpace(options_.RemoteUsername);

        if (OperatingSystem.IsWindows())
        {
            com_thread_.EnqueueAndWait(() => ConnectOnStaThread(progId, host, hasCredentials));
        }

        return Task.CompletedTask;
    }

    private void ConnectOnStaThread(string progId, string? host, bool hasCredentials)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // ConnectDirect handles both local and remote DCOM.
        // For remote with credentials, it passes COAUTHINFO/COAUTHIDENTITY directly
        // to CoCreateInstanceEx — no LogonUser/impersonation needed (which causes
        // 0xC0000005 access violations when combined with COAUTHINFO).
        ConnectDirect(progId, host);

    }

    [SupportedOSPlatform("windows")]
    private void ConnectDirect(string progId, string? host)
    {
        if (host is null)
        {
            // Local COM activation: no COSERVERINFO needed.
            Type? serverType = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (serverType is null)
            {
                throw new InvalidOperationException($"OPC DA server ProgID '{progId}' is not registered on this machine.");
            }

            object serverObject;
            try
            {
                serverObject = Activator.CreateInstance(serverType)
                    ?? throw new InvalidOperationException($"Failed to create OPC DA server '{progId}'.");
            }
            catch (Exception ex) when (DaConnectErrorClassifier.IsActivationRetryable(ex))
            {
                // Registered but not launchable (server process killed, crashed, RPC dead):
                // transient — the coordinator retries with backoff.
                throw new SourceConnectionLostException(
                    $"Failed to create OPC DA server '{progId}': {ex.Message}",
                    ex);
            }

            IOPCServer server = serverObject as IOPCServer
                ?? throw new InvalidOperationException($"COM server '{progId}' does not expose IOPCServer.");

            server_com_object_ = serverObject;
            server_ = server;
            server_info_ = DetectServerInfo(serverObject);
            return;
        }

        // Remote DCOM activation. With explicit credentials: LogonUser + impersonation
        // so the COM machinery activates with that identity (also makes per-user HKCU
        // registrations on the remote host visible). Without credentials: activate
        // directly with the process identity (null COAUTHINFO -> default credentials).
        string username = options_.RemoteUsername ?? string.Empty;
        string password = options_.RemotePassword ?? string.Empty;
        string domain = string.IsNullOrWhiteSpace(options_.RemoteDomain) ? host! : options_.RemoteDomain!;

        if (!string.IsNullOrWhiteSpace(username))
        {
            if (!LogonUser(username, domain, password, 9, 3, out nint token))
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"Logon failed for '{domain}\\{username}' (Win32 error {error}). " +
                    "Check RemoteUsername, RemotePassword, RemoteDomain.");
            }

            try
            {
                using var identity = new WindowsIdentity(token);
                WindowsIdentity.RunImpersonated(identity.AccessToken, () => ConnectRemote(progId, host));
            }
            finally
            {
                CloseHandle(token);
            }

            return;
        }

        ConnectRemote(progId, host);
    }

    [SupportedOSPlatform("windows")]
    private void ConnectRemote(string progId, string host)
    {
        Type? serverType;
        try
        {
            serverType = Type.GetTypeFromProgID(progId, host, throwOnError: false);
            if (serverType is null)
            {
                throw new InvalidOperationException(
                    $"OPC DA server '{progId}' is not available on host '{host}'.");
            }
        }
        catch (Exception ex) when (DaConnectErrorClassifier.IsRetryable(ex, isRemote: true))
        {
            // Server down / unreachable — transient. The coordinator retries with backoff
            // instead of marking the source Faulted forever.
            throw new SourceConnectionLostException(
                $"OPC DA server '{progId}' is not reachable on host '{host}': {ex.Message}",
                ex);
        }

        object serverObject;
        try
        {
            serverObject = Activator.CreateInstance(serverType)
                ?? throw new InvalidOperationException($"Failed to create OPC DA server '{progId}' on host '{host}'.");
        }
        catch (Exception ex) when (DaConnectErrorClassifier.IsActivationRetryable(ex))
        {
            throw new SourceConnectionLostException(
                $"Failed to create OPC DA server '{progId}' on host '{host}': {ex.Message}",
                ex);
        }

        IOPCServer server = serverObject as IOPCServer
            ?? throw new InvalidOperationException($"Remote COM server '{progId}' does not expose IOPCServer.");

        server_com_object_ = serverObject;
        server_ = server;
        server_info_ = DetectServerInfo(serverObject);
    }

    [SupportedOSPlatform("windows")]
    private OpcDaServerInfo DetectServerInfo(object serverObject)
    {
        string specVersion = DetectSpecVersion(serverObject);
        try
        {
            int hr = server_!.GetStatus(out IntPtr statusPtr);
            if (hr != 0 || statusPtr == IntPtr.Zero)
            {
                return new OpcDaServerInfo(specVersion, 0, 0, 0, null, "Unknown");
            }

            // The OPCSERVERSTATUS block (and the LPWSTR it embeds) is server/proxy
            // memory: some servers hand back pointers that are not valid in this
            // process, and an AccessViolation raised while reading one is not
            // reliably catchable. Probe readability before touching the pointer so
            // a misbehaving server can never take the whole bridge down.
            if (!IsRangeReadable(statusPtr, Marshal.SizeOf<OpcServerStatus>()))
            {
                return new OpcDaServerInfo(specVersion, 0, 0, 0, null, "Unknown");
            }

            try
            {
                OpcServerStatus status = Marshal.PtrToStructure<OpcServerStatus>(statusPtr);
                return new OpcDaServerInfo(
                    specVersion,
                    status.MajorVersion,
                    status.MinorVersion,
                    status.BuildNumber,
                    SafeReadUnicodeString(status.VendorInfo),
                    OpcDaServerInfo.DescribeState(status.State));
            }
            finally
            {
                // OPC DA requires the client to free the status block with
                // CoTaskMemFree; only reached when the pointer passed the probe.
                Marshal.FreeCoTaskMem(statusPtr);
            }
        }
        catch
        {
            // Best-effort detection: a server that fails GetStatus must never block the
            // connection, so fall back to spec-level-only info.
            return new OpcDaServerInfo(specVersion, 0, 0, 0, null, "Unknown");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string DetectSpecVersion(object serverObject)
    {
        // OPC DA splits its interfaces between the server object and group objects.
        // The async I/O interfaces (IOPCAsyncIO/IOPCAsyncIO2/IOPCAsyncIO3) live on
        // GROUP objects, so probing them here (against the server object) always
        // fails even for compliant servers. The spec-level markers on the SERVER
        // object are:
        //   IOPCItemIO                 -> DA 3.0 (the server-based DA 3.0 addition)
        //   IOPCItemProperties         -> DA 2.0 (introduced with DA 2.0)
        //   IOPCBrowseServerAddressSpace -> DA 2.0 (DA 2.0 browsing, optional but common)
        // `is` on a COM RCW performs a QueryInterface for the interface GUID.
        if (serverObject is IOPCItemIO)
        {
            return "3.0";
        }

        if (serverObject is IOPCItemProperties)
        {
            return "2.0";
        }

        if (serverObject is IOPCBrowseServerAddressSpace)
        {
            return "2.0";
        }

        return "Unknown";
    }

    public Task<IReadOnlyList<BridgeValue>> ReadAsync(
        IReadOnlyList<TagMapping> mappings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (mappings.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<BridgeValue>>(Array.Empty<BridgeValue>());
        }

        EnsureConnected();

        int defaultRate = Math.Max(100, options_.UpdateRateMs);

        Dictionary<int, List<TagMapping>> byRate = new();
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            int rate = mapping.PollRateMs > 0 ? mapping.PollRateMs : defaultRate;
            if (!byRate.TryGetValue(rate, out List<TagMapping>? list))
            {
                list = new();
                byRate[rate] = list;
            }
            list.Add(mapping);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA requires Windows.");
        }

        IReadOnlyList<BridgeValue> allValues = com_thread_!.EnqueueAndWait(() => ReadOnStaThread(byRate, mappings.Count));

        return Task.FromResult(allValues);
    }
    public Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(itemId) || value is null)
        {
            return Task.FromResult(false);
        }

        EnsureConnected();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA requires Windows.");
        }

        bool success = com_thread_!.EnqueueAndWait(() => WriteOnStaThread(itemId, value));
        return Task.FromResult(success);
    }

    private bool WriteOnStaThread(string itemId, object value)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA requires Windows.");
        }

        // Locate the server handle for this item across all rate groups.
        int serverHandle = 0;
        IOPCSyncIO? syncIo = null;
        foreach (RateGroup group in rate_groups_.Values)
        {
            for (int i = 0; i < group.Bindings.Length; i++)
            {
                if (string.Equals(group.Bindings[i].ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    serverHandle = group.Bindings[i].ServerHandle;
                    syncIo = group.SyncIo;
                    break;
                }
            }

            if (serverHandle != 0)
            {
                break;
            }
        }

        if (serverHandle == 0 || syncIo is null)
        {
            return false;
        }

        IntPtr handlesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<int>());
        IntPtr valuesPtr = Marshal.AllocHGlobal(16); // VARIANT is 16 bytes
        IntPtr errorsPtr = IntPtr.Zero;

        try
        {
            Marshal.WriteInt32(handlesPtr, serverHandle);
            Marshal.GetNativeVariantForObject(value, valuesPtr);

            int hr = syncIo.Write(1, handlesPtr, valuesPtr, out errorsPtr);
            if (hr < 0)
            {
                return false;
            }

            int[] errors = new int[1];
            Marshal.Copy(errorsPtr, errors, 0, 1);
            return errors[0] >= 0;
        }
        finally
        {
            VariantClear(valuesPtr);
            if (errorsPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(errorsPtr);
            Marshal.FreeHGlobal(valuesPtr);
            Marshal.FreeHGlobal(handlesPtr);
        }
    }


    private IReadOnlyList<BridgeValue> ReadOnStaThread(Dictionary<int, List<TagMapping>> byRate, int mappingCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA requires Windows.");
        }

        List<BridgeValue> values = new(mappingCount);
        foreach ((int rate, List<TagMapping> rateMappings) in byRate)
        {
            if (!rate_groups_.TryGetValue(rate, out RateGroup? group))
            {
                float deadband = ComputeGroupDeadband(rateMappings);
                group = CreateRateGroup(rate, deadband);
                rate_groups_[rate] = group;
            }

            EnsureGroupItemsConfigured(group, rateMappings);

            // Establish a subscription so values arrive via IOPCDataCallback instead of polling.
            // If the server doesn't support it, fall back silently to device reads below.
            if (options_.UseSubscriptions && group.ConnectionPoint is null && group.Sink is null)
            {
                TrySetupSubscription(group, rateMappings);
            }

            // When a subscription is active, values flow via ValuesReceived; only device-read
            // when subscriptions are off or never established.
            if (!subscriptions_active_ || group.ConnectionPoint is null)
            {
                IReadOnlyList<BridgeValue> groupValues = ReadGroup(group);
                values.AddRange(groupValues);
            }
            else
            {
                // Items that failed AddItems never arrive via callbacks — surface them
                // as BAD until the server accepts them again (retried each poll cycle).
                values.AddRange(ReadUnboundValues(group));
            }
        }

        return values;
    }

    [SupportedOSPlatform("windows")]
    private RateGroup CreateRateGroup(int rate, float deadbandPct)
    {
        IOPCServer server = server_!;
        Guid itemManagementGuid = typeof(IOPCItemMgt).GUID;

        IntPtr deadbandPtr = IntPtr.Zero;
        if (deadbandPct > 0f)
        {
            deadbandPtr = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(deadbandPtr, BitConverter.SingleToInt32Bits(deadbandPct));
        }

        int addGroupHresult = server.AddGroup(
            $"OpcBridge_{rate}",
            1,
            Math.Max(100, rate),
            rate,
            IntPtr.Zero,
            deadbandPtr,
            0,
            out int serverGroupHandle,
            out _,
            ref itemManagementGuid,
            out object groupObject);
        ThrowOnFailed(addGroupHresult, $"Failed to create OPC DA group for rate {rate}ms.");

        IOPCItemMgt itemManagement = groupObject as IOPCItemMgt
            ?? throw new InvalidOperationException("OPC DA group does not expose IOPCItemMgt.");
        IOPCSyncIO syncIo = groupObject as IOPCSyncIO
            ?? throw new InvalidOperationException("OPC DA group does not expose IOPCSyncIO.");

        return new RateGroup
        {
            Rate = rate,
            ComObject = groupObject,
            ItemManagement = itemManagement,
            SyncIo = syncIo,
            ServerGroupHandle = serverGroupHandle,
            Bindings = [],
            DeadbandPtr = deadbandPtr
        };
    }
    private static float ComputeGroupDeadband(IReadOnlyList<TagMapping> mappings)
    {
        float max = 0f;
        for (int i = 0; i < mappings.Count; i++)
        {
            float d = mappings[i].DeadbandPct;
            if (d > max) max = d;
        }
        return max > 100f ? 100f : max;
    }

    private void TrySetupSubscription(RateGroup group, IReadOnlyList<TagMapping> mappings)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // When the user forced Async I/O 2.0 for this source, the fallback warning
        // is loud so the mismatch (requested push, server can't) is never silent.
        string forcedMode = string.Equals(options_.IoMode, "Async20", StringComparison.OrdinalIgnoreCase)
            ? "Forced Async I/O 2.0: "
            : string.Empty;

        // Warn once per client per rate group; the attempt repeats every poll cycle
        // while the subscription stays unavailable, so re-warning would flood logs.
        bool WarnOnce(string message)
        {
            if (!subscription_fallback_warned_rates_.Add(group.Rate))
            {
                return false;
            }

            Warning?.Invoke(message);
            return true;
        }

        try
        {
            if (group.ComObject is not IConnectionPointContainer cpc)
            {
                subscriptions_active_ = false;
                WarnOnce(
                    $"{forcedMode}OPC DA group for rate {group.Rate}ms does not expose IConnectionPointContainer; " +
                    "subscription unavailable, falling back to polling.");
                return;
            }

            Guid callbackIid = typeof(IOPCDataCallback).GUID;
            int hr = cpc.FindConnectionPoint(ref callbackIid, out IConnectionPoint cp);
            if (hr < 0)
            {
                subscriptions_active_ = false;
                WarnOnce(
                    $"{forcedMode}OPC DA callback connection point unavailable for rate {group.Rate}ms " +
                    $"(0x{hr:X8}); falling back to polling.");
                return;
            }

            // Build client-handle → item-id map for the callback to unpack notifications.
            Dictionary<int, string> handleMap = new(group.Bindings.Length);
            for (int i = 0; i < group.Bindings.Length; i++)
            {
                handleMap[i + 1] = group.Bindings[i].ItemId;
            }

            Action<IReadOnlyList<BridgeValue>> handler = ValuesReceived ?? (_ => { });
            OpcDaCallbackSink sink = new(options_.SourceId, handleMap, handler);
            hr = cp.Advise(sink, out int cookie);
            if (hr < 0)
            {
                subscriptions_active_ = false;
                WarnOnce(
                    $"{forcedMode}OPC DA callback Advise failed for rate {group.Rate}ms (0x{hr:X8}); " +
                    "falling back to polling.");
                return;
            }

            group.Sink = sink;
            group.ConnectionPoint = cp;
            group.CallbackCookie = cookie;
            subscriptions_active_ = true;
        }
        catch (Exception ex)
        {
            // Never fail silently: an unexpected error while establishing the
            // subscription must surface (the user may have forced Async I/O 2.0).
            subscriptions_active_ = false;
            WarnOnce(
                $"{forcedMode}OPC DA subscription setup failed for rate {group.Rate}ms: {ex.Message}; " +
                "falling back to polling.");
        }
    }

    private static void UnadviseCallback(RateGroup group)
    {
        if (!OperatingSystem.IsWindows() || group.ConnectionPoint is null)
        {
            return;
        }

        try
        {
            group.ConnectionPoint.Unadvise(group.CallbackCookie);
        }
        catch
        {
            // Best-effort during teardown.
        }

        if (group.Sink is not null)
        {
            try { Marshal.ReleaseComObject(group.Sink); } catch { }
            group.Sink = null;
        }

        try { Marshal.ReleaseComObject(group.ConnectionPoint); } catch { }
        group.ConnectionPoint = null;
        group.CallbackCookie = 0;
    }


    private void EnsureGroupItemsConfigured(RateGroup group, IReadOnlyList<TagMapping> mappings)
    {
        if (group.Bindings.Length != 0)
        {
            if (group.Bindings.Length != mappings.Count)
            {
                throw new InvalidOperationException("OPC DA mappings changed after the client was connected.");
            }

            for (int i = 0; i < group.Bindings.Length; i++)
            {
                if (!string.Equals(group.Bindings[i].ItemId, mappings[i].ItemId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("OPC DA mappings changed after the client was connected.");
                }
            }
        }

        // (Re)add items whose AddItems failed on an earlier cycle — the server may now
        // know them again (e.g. it was restarted and the tag is back). Individual
        // failures are isolated: the item is reported BAD and retried, the source stays up.
        AddMissingItems(group, mappings);
    }

    private void AddMissingItems(RateGroup group, IReadOnlyList<TagMapping> mappings)
    {
        IOPCItemMgt itemManagement = group.ItemManagement!;

        List<int> missing = new();
        if (group.Bindings.Length == 0)
        {
            for (int i = 0; i < mappings.Count; i++)
            {
                missing.Add(i);
            }
        }
        else
        {
            for (int i = 0; i < group.Bindings.Length; i++)
            {
                if (!group.Bindings[i].IsBound)
                {
                    missing.Add(i);
                }
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        OpcItemDefinition[] definitions = new OpcItemDefinition[missing.Count];
        for (int k = 0; k < missing.Count; k++)
        {
            int mappingIndex = missing[k];
            TagMapping mapping = mappings[mappingIndex];
            definitions[k] = new OpcItemDefinition
            {
                AccessPath = string.Empty,
                ItemId = mapping.ItemId,
                IsActive = 1,
                ClientHandle = mappingIndex + 1,
                RequestedDataType = (short)MapVarType(mapping.DataType)
            };
        }

        IntPtr resultsPointer = IntPtr.Zero;
        IntPtr errorsPointer = IntPtr.Zero;
        List<int> cleanupHandles = new(definitions.Length);

        try
        {
            int addItemsHresult = itemManagement.AddItems(
                definitions.Length,
                definitions,
                out resultsPointer,
                out errorsPointer);
            ThrowOnFailed(addItemsHresult, "Failed to add OPC DA items.");

            int[] itemErrors = new int[definitions.Length];
            Marshal.Copy(errorsPointer, itemErrors, 0, definitions.Length);

            if (group.Bindings.Length == 0)
            {
                group.Bindings = new ItemBinding[mappings.Count];
            }

            ItemBinding[] bindings = group.Bindings;
            int resultSize = Marshal.SizeOf<OpcItemResult>();

            for (int k = 0; k < definitions.Length; k++)
            {
                int mappingIndex = missing[k];
                IntPtr resultPointer = IntPtr.Add(resultsPointer, k * resultSize);
                OpcItemResult result = Marshal.PtrToStructure<OpcItemResult>(resultPointer);

                if (result.BlobPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(result.BlobPointer);
                }

                string itemId = mappings[mappingIndex].ItemId;
                if (itemErrors[k] < 0)
                {
                    // Per-item AddItems failure (item unknown to the server, access denied, …):
                    // keep the source alive, report the item as BAD, retry on the next cycle.
                    bindings[mappingIndex] = new ItemBinding(itemId, 0, IsBound: false);
                    if (group.MissingItemWarnings.Add(itemId))
                    {
                        Warning?.Invoke(
                            $"OPC DA item '{itemId}' could not be added (0x{itemErrors[k]:X8}); " +
                            "reporting BAD and retrying.");
                    }

                    continue;
                }

                bindings[mappingIndex] = new ItemBinding(itemId, result.ServerHandle, IsBound: true);
                if (group.MissingItemWarnings.Remove(itemId))
                {
                    Warning?.Invoke($"OPC DA item '{itemId}' recovered.");
                }

                cleanupHandles.Add(result.ServerHandle);
            }
        }
        catch
        {
            if (cleanupHandles.Count > 0)
            {
                RemoveItems(group.ItemManagement, cleanupHandles.ToArray());
            }

            throw;
        }
        finally
        {
            if (errorsPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(errorsPointer);
            }

            if (resultsPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(resultsPointer);
            }
        }
    }

    private IReadOnlyList<BridgeValue> ReadGroup(RateGroup group)
    {
        IOPCSyncIO syncIo = group.SyncIo!;
        ItemBinding[] bindings = group.Bindings;

        if (bindings.Length == 0)
        {
            return Array.Empty<BridgeValue>();
        }

        // Items that failed AddItems (e.g. deleted on the server) are reported as BAD
        // without a server round-trip; AddMissingItems retries them on every poll cycle.
        int boundCount = 0;
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].IsBound)
            {
                boundCount++;
            }
        }

        BridgeValue[] values = new BridgeValue[bindings.Length];
        if (boundCount == 0)
        {
            for (int i = 0; i < bindings.Length; i++)
            {
                values[i] = new BridgeValue(
                    options_.SourceId,
                    bindings[i].ItemId,
                    null,
                    DateTime.UtcNow,
                    0,
                    false);
            }

            return values;
        }

        int[] serverHandles = new int[boundCount];
        int[] boundToIndex = new int[boundCount];
        int boundIndex = 0;
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].IsBound)
            {
                serverHandles[boundIndex] = bindings[i].ServerHandle;
                boundToIndex[boundIndex] = i;
                boundIndex++;
            }
            else
            {
                values[i] = new BridgeValue(
                    options_.SourceId,
                    bindings[i].ItemId,
                    null,
                    DateTime.UtcNow,
                    0,
                    false);
            }
        }

        IntPtr itemStatesPointer = IntPtr.Zero;
        IntPtr errorsPointer = IntPtr.Zero;

        try
        {
            int readHresult = syncIo.Read(
                OpcDataSourceDevice,
                serverHandles.Length,
                serverHandles,
                out itemStatesPointer,
                out errorsPointer);
            ThrowOnFailed(readHresult, "OPC DA read failed.");

            int[] itemErrors = new int[serverHandles.Length];
            Marshal.Copy(errorsPointer, itemErrors, 0, serverHandles.Length);

            for (int j = 0; j < serverHandles.Length; j++)
            {
                int i = boundToIndex[j];
                IntPtr itemStatePointer = IntPtr.Add(itemStatesPointer, j * ItemStateSize);
                OpcItemState itemState = Marshal.PtrToStructure<OpcItemState>(itemStatePointer);

                try
                {
                    if (itemErrors[j] < 0)
                    {
                        // Per-item read failure (e.g. a write-only or fault-injected item):
                        // mirror it as a BAD value instead of failing the whole group,
                        // so one bad tag cannot take down the source.
                        values[i] = new BridgeValue(
                            options_.SourceId,
                            bindings[i].ItemId,
                            null,
                            DateTime.UtcNow,
                            0,
                            false);
                        continue;
                    }

                    int quality = (ushort)itemState.Quality;
                    values[i] = new BridgeValue(
                        options_.SourceId,
                        bindings[i].ItemId,
                        itemState.Value,
                        FileTimeToUtc(itemState.Timestamp),
                        quality,
                        QualityMapper.IsGoodDaQuality(quality));
                }
                finally
                {
                    VariantClear(IntPtr.Add(itemStatePointer, ItemValueOffset));
                }
            }

            return values;
        }
        finally
        {
            if (errorsPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(errorsPointer);
            }

            if (itemStatesPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(itemStatesPointer);
            }
        }
    }

    private IReadOnlyList<BridgeValue> ReadUnboundValues(RateGroup group)
    {
        List<BridgeValue>? unbound = null;
        for (int i = 0; i < group.Bindings.Length; i++)
        {
            if (!group.Bindings[i].IsBound)
            {
                (unbound ??= new List<BridgeValue>()).Add(new BridgeValue(
                    options_.SourceId,
                    group.Bindings[i].ItemId,
                    null,
                    DateTime.UtcNow,
                    0,
                    false));
            }
        }

        return unbound ?? (IReadOnlyList<BridgeValue>)Array.Empty<BridgeValue>();
    }

    public ValueTask DisposeAsync()
    {
        // Unadvise callbacks and release all COM objects on the STA thread that owns them,
        // then stop the thread. If the client never connected (no thread), nothing to do.
        OpcComThread? thread = com_thread_;
        com_thread_ = null;

        if (thread is not null)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    thread.EnqueueAndWait(DisposeGroupsOnStaThread);
                }
            }
            catch (ObjectDisposedException)
            {
                // Thread already torn down.
            }
            finally
            {
                if (OperatingSystem.IsWindows())
                {
                    thread.Dispose();
                }
            }
        }
        else
        {
            server_ = null;
            server_com_object_ = null;
        }

        return ValueTask.CompletedTask;
    }
    private void DisposeGroupsOnStaThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RateGroup[] groups = rate_groups_.Values.ToArray();
        foreach (RateGroup group in groups)
        {
            UnadviseCallback(group);
            RemoveGroupItems(group);

            if (server_ is not null && group.ServerGroupHandle != 0)
            {
                try
                {
                    server_.RemoveGroup(group.ServerGroupHandle, 0);
                }
                catch (Exception)
                {
                    // Server may be gone — group removal is best-effort teardown.
                }
            }

            if (group.DeadbandPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(group.DeadbandPtr);
                group.DeadbandPtr = IntPtr.Zero;
            }
        }

        rate_groups_.Clear();

        foreach (RateGroup group in groups)
        {
            ReleaseComObject(ref group.ComObject);
        }

        ReleaseComObject(ref server_com_object_);
        server_ = null;
    }


    private void EnsureConnected()
    {
        if (server_ is null)
        {
            throw new InvalidOperationException("OPC DA client is not connected.");
        }
    }

    [SupportedOSPlatform("windows")]
    private (bool Found, short? CanonicalDataType, int? AccessRights) TryGetTagMetadataOnStaThread(string itemId)
    {
        EnsureConnected();

        Guid itemManagementGuid = typeof(IOPCItemMgt).GUID;
        object? groupObject = null;
        int serverGroupHandle = 0;

        try
        {
            int addGroupHresult = server_!.AddGroup(
                "OpcBridge_MetadataLookup",
                0,
                1000,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                out serverGroupHandle,
                out _,
                ref itemManagementGuid,
                out groupObject);
            ThrowOnFailed(addGroupHresult, $"Failed to create OPC DA group for metadata lookup '{itemId}'.");

            if (groupObject is not IOPCItemMgt itemManagement)
            {
                return (false, null, null);
            }

            OpcItemDefinition[] definitions =
            [
                new OpcItemDefinition
                {
                    AccessPath = string.Empty,
                    ItemId = itemId,
                    IsActive = 0,
                    ClientHandle = 1,
                    RequestedDataType = 0
                }
            ];

            IntPtr resultsPointer = IntPtr.Zero;
            IntPtr errorsPointer = IntPtr.Zero;
            int serverHandle = 0;

            try
            {
                int addItemsHresult = itemManagement.AddItems(definitions.Length, definitions, out resultsPointer, out errorsPointer);
                ThrowOnFailed(addItemsHresult, $"Failed to add OPC DA item '{itemId}' for metadata lookup.");

                int[] itemErrors = new int[definitions.Length];
                Marshal.Copy(errorsPointer, itemErrors, 0, definitions.Length);
                ThrowOnFailed(itemErrors[0], $"Failed to resolve OPC DA item '{itemId}' for metadata lookup.");

                OpcItemResult result = Marshal.PtrToStructure<OpcItemResult>(resultsPointer);
                if (result.BlobPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(result.BlobPointer);
                }

                serverHandle = result.ServerHandle;
                return (true, result.CanonicalDataType, result.AccessRights);
            }
            finally
            {
                if (serverHandle != 0)
                {
                    RemoveItems(itemManagement, [serverHandle]);
                }

                if (errorsPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(errorsPointer);
                }

                if (resultsPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(resultsPointer);
                }
            }
        }
        catch
        {
            return (false, null, null);
        }
        finally
        {
            if (groupObject is not null)
            {
                ReleaseComObject(ref groupObject);
            }

            if (server_ is not null && serverGroupHandle != 0)
            {
                try
                {
                    server_.RemoveGroup(serverGroupHandle, 0);
                }
                catch
                {
                }
            }
        }
    }

    private static void RemoveGroupItems(RateGroup group)
    {
        if (group.Bindings.Length == 0)
        {
            return;
        }

        List<int> boundHandles = new(group.Bindings.Length);
        for (int i = 0; i < group.Bindings.Length; i++)
        {
            if (group.Bindings[i].IsBound)
            {
                boundHandles.Add(group.Bindings[i].ServerHandle);
            }
        }

        RemoveItems(group.ItemManagement, boundHandles.ToArray());
        group.Bindings = [];
        group.MissingItemWarnings.Clear();
    }

    private static void RemoveItems(IOPCItemMgt? itemManagement, int[] serverHandles)
    {
        if (itemManagement is null || serverHandles.Length == 0)
        {
            return;
        }

        IntPtr errorsPointer = IntPtr.Zero;
        try
        {
            itemManagement.RemoveItems(serverHandles.Length, serverHandles, out errorsPointer);
        }
        catch (Exception)
        {
            // The server may be gone (RPC dead) — item removal is best-effort cleanup
            // and must never take down the coordinator's teardown path.
        }
        finally
        {
            if (errorsPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(errorsPointer);
            }
        }
    }

    private static string? NormalizeHost(string host)
    {
        string trimmed = host.Trim();
        if (trimmed.Length == 0 ||
            string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, ".", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static VarEnum MapVarType(string dataType)
    {
        return dataType.Trim().ToUpperInvariant() switch
        {
            "BOOL" or "BOOLEAN" => VarEnum.VT_BOOL,
            "BYTE" => VarEnum.VT_UI1,
            "SBYTE" => VarEnum.VT_I1,
            "INT16" or "SHORT" => VarEnum.VT_I2,
            "UINT16" => VarEnum.VT_UI2,
            "INT32" or "INT" => VarEnum.VT_I4,
            "UINT32" => VarEnum.VT_UI4,
            "INT64" or "LONG" => VarEnum.VT_I8,
            "UINT64" => VarEnum.VT_UI8,
            "FLOAT" or "SINGLE" => VarEnum.VT_R4,
            "DOUBLE" or "REAL8" => VarEnum.VT_R8,
            "STRING" => VarEnum.VT_BSTR,
            _ => VarEnum.VT_EMPTY
        };
    }

    internal static DateTime FileTimeToUtc(FILETIME value)
    {
        long fileTime = ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
        return fileTime <= 0 ? DateTime.UtcNow : DateTime.FromFileTimeUtc(fileTime);
    }

    private static void ThrowOnFailed(int hresult, string message)
    {
        if (hresult < 0)
        {
            throw new COMException(message, hresult);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(ref object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        Marshal.FinalReleaseComObject(comObject);
        comObject = null;
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(IntPtr pvarg);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string username, string domain, string password,
        int logonType, int logonProvider, out nint token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
    private const int CLSCTX_LOCAL_SERVER = 0x4;
    private const int CLSCTX_REMOTE_SERVER = 0x10;
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int RPC_C_AUTHN_WINNT = 10;
    private const int RPC_C_AUTHZ_NONE = 0;
    private const int RPC_C_AUTHN_LEVEL_CONNECT = 2;
    private const int RPC_C_AUTHN_LEVEL_PKT_PRIVACY = 6;
    private const int RPC_C_IMP_LEVEL_IMPERSONATE = 3;
    private const int EOAC_NONE = 0;

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstanceEx(
        ref Guid clsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        ref COSERVERINFO pServerInfo,
        uint dwCount,
        IntPtr pResults);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COSERVERINFO
    {
        public IntPtr dwReserved;
        public string pwszName;
        public IntPtr pAuthInfo; // pointer to COAUTHINFO
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COAUTHINFO
    {
        public int dwAuthnSvc;
        public int dwAuthzSvc;
        public IntPtr pwszServerPrincipalName;
        public int dwAuthnLevel;
        public int dwImpersonationLevel;
        public IntPtr pAuthIdentityData; // pointer to COAUTHIDENTITY
        public int dwCapabilities;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COAUTHIDENTITY
    {
        public IntPtr User;
        public int UserLength;
        public IntPtr Domain;
        public int DomainLength;
        public IntPtr Password;
        public int PasswordLength;
        public int Flags; // SEC_WINNT_AUTH_IDENTITY_UNICODE = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MULTI_QI
    {
        public IntPtr pIID;
        public IntPtr pItf;
        public int hr;
    }


    private sealed record ItemBinding(string ItemId, int ServerHandle, bool IsBound = true);

    private sealed class RateGroup
    {
        public int Rate;
        public object? ComObject;
        public IOPCItemMgt? ItemManagement;
        public IOPCSyncIO? SyncIo;
        public int ServerGroupHandle;
        public ItemBinding[] Bindings = [];
        public IntPtr DeadbandPtr;
        public IConnectionPoint? ConnectionPoint;
        public int CallbackCookie;
        public OpcDaCallbackSink? Sink;

        /// <summary>Item ids whose AddItems failed; used to warn once per failure/recovery transition.</summary>
        public HashSet<string> MissingItemWarnings = new(StringComparer.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpcItemDefinition
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string AccessPath;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string ItemId;

        public int IsActive;
        public int ClientHandle;
        public int BlobSize;
        public IntPtr BlobPointer;
        public short RequestedDataType;
        public short Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpcItemResult
    {
        public int ServerHandle;
        public short CanonicalDataType;
        public short Reserved;
        public int AccessRights;
        public int BlobSize;
        public IntPtr BlobPointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpcItemState
    {
        public int ClientHandle;
        public FILETIME Timestamp;
        public short Quality;
        public short Reserved;

        [MarshalAs(UnmanagedType.Struct)]
        public object? Value;
    }

    // OPC DA OPCSERVERSTATUS: 3×FILETIME, 4×DWORD, then LPWSTR vendor info.
    [StructLayout(LayoutKind.Sequential)]
    private struct OpcServerStatus
    {
        public FILETIME StartTime;
        public FILETIME CurrentTime;
        public FILETIME LastUpdateTime;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint State;
        public IntPtr VendorInfo;
    }

    // ---- Safe reads of GetStatus output -------------------------------------
    // OPCSERVERSTATUS is allocated by the server/proxy. Before dereferencing it
    // (or the vendor string it embeds) probe the range with VirtualQuery: a wild
    // pointer would otherwise raise an AccessViolation that is not reliably
    // catchable and would crash the whole bridge process.

    [DllImport("kernel32.dll")]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;

    [SupportedOSPlatform("windows")]
    private static bool TryGetReadableRegion(IntPtr address, out long regionEnd)
    {
        regionEnd = 0;

        MemoryBasicInformation mbi = default;
        if (VirtualQuery(address, out mbi, (UIntPtr)Marshal.SizeOf<MemoryBasicInformation>()) == UIntPtr.Zero)
        {
            return false;
        }

        if (mbi.State != MemCommit || (mbi.Protect & PageGuard) != 0)
        {
            return false;
        }

        uint protect = mbi.Protect & 0xFF;
        bool readable = protect is PageReadOnly or PageReadWrite or PageWriteCopy
            or PageExecuteRead or PageExecuteReadWrite or PageExecuteWriteCopy;
        if (!readable)
        {
            return false;
        }

        regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize.ToUInt64();
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRangeReadable(IntPtr address, int byteCount)
    {
        if (address == IntPtr.Zero || byteCount <= 0)
        {
            return false;
        }

        return TryGetReadableRegion(address, out long regionEnd)
            && (long)address + byteCount <= regionEnd;
    }

    [SupportedOSPlatform("windows")]
    private static string? SafeReadUnicodeString(IntPtr address)
    {
        if (address == IntPtr.Zero || !TryGetReadableRegion(address, out long regionEnd))
        {
            return null;
        }

        // Walk the wide string within the committed region only, so a string that
        // runs off into unmapped memory truncates instead of crashing.
        const int maxChars = 256;
        char[] chars = new char[maxChars];
        int count = 0;
        long cursor = (long)address;
        while (count < maxChars && cursor + 2 <= regionEnd)
        {
            char c = (char)Marshal.ReadInt16((IntPtr)cursor);
            if (c == '\0')
            {
                break;
            }

            chars[count++] = c;
            cursor += 2;
        }

        return count == 0 ? null : new string(chars, 0, count);
    }

    [ComImport]
    [Guid("39C13A4D-011E-11D0-9675-0020AFD8ADB3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOPCServer
    {
        int AddGroup(
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            int active,
            int requestedUpdateRate,
            int clientGroupHandle,
            IntPtr timeBias,
            IntPtr percentDeadband,
            int lcid,
            out int serverGroupHandle,
            out int revisedUpdateRate,
            ref Guid requestedInterface,
            [MarshalAs(UnmanagedType.IUnknown)] out object groupInterface);

        int GetErrorString(int error, int locale, [MarshalAs(UnmanagedType.LPWStr)] out string errorString);

        int GetGroupByName(
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            ref Guid requestedInterface,
            [MarshalAs(UnmanagedType.IUnknown)] out object groupInterface);

        int GetStatus(out IntPtr serverStatus);

        int RemoveGroup(int serverGroupHandle, int force);

        int CreateGroupEnumerator(
            int scope,
            ref Guid requestedInterface,
            [MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
    }

    [ComImport]
    [Guid("39C13A54-011E-11D0-9675-0020AFD8ADB3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOPCItemMgt
    {
        int AddItems(int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] OpcItemDefinition[] itemDefinitions, out IntPtr results, out IntPtr errors);

        int ValidateItems(int count, IntPtr itemDefinitions, int blobUpdate, out IntPtr validationResults, out IntPtr errors);

        int RemoveItems(int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] serverHandles, out IntPtr errors);

        int SetActiveState(int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] serverHandles, int active, out IntPtr errors);

        int SetClientHandles(int count, IntPtr serverHandles, IntPtr clientHandles, out IntPtr errors);

        int SetDatatypes(int count, IntPtr serverHandles, IntPtr requestedDatatypes, out IntPtr errors);

        int CreateEnumerator(ref Guid requestedInterface, [MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
    }

    [ComImport]
    [Guid("39C13A52-011E-11D0-9675-0020AFD8ADB3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOPCSyncIO
    {
        int Read(int dataSource, int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] int[] serverHandles, out IntPtr itemValues, out IntPtr errors);

        int Write(int count, IntPtr serverHandles, IntPtr values, out IntPtr errors);
    }

    // Probe-only declarations: never invoked. `is` on a COM RCW performs a
    // QueryInterface for the interface GUID, so these detect which OPC DA spec
    // level a server implements. These are SERVER-object interfaces (the async
    // I/O interfaces live on group objects and can't be probed here). GUIDs are
    // the spec-defined ones (opcda.idl / opcda30.idl).
    [ComImport]
    [Guid("85C0B427-2893-4CBC-BD78-E5FC5146F08F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOPCItemIO
    {
    }

    [ComImport]
    [Guid("39C13A72-011E-11D0-9675-0020AFD8ADB3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOPCItemProperties
    {
    }

    [ComImport]
    [Guid("39C13A4F-011E-11D0-9675-0020AFD8ADB3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOPCBrowseServerAddressSpace
    {
    }

    [ComImport]
    [Guid("B196B284-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnectionPointContainer
    {
        [PreserveSig] int EnumConnectionPoints(out IntPtr ppEnum);

        [PreserveSig] int FindConnectionPoint(ref Guid riid, out IConnectionPoint ppCP);
    }

    [ComImport]
    [Guid("B196B286-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnectionPoint
    {
        [PreserveSig] int GetConnectionInterface(out Guid pIID);

        [PreserveSig] int GetConnectionPointContainer(out IConnectionPointContainer ppCPC);

        [PreserveSig] int Advise([MarshalAs(UnmanagedType.IUnknown)] object pUnkSink, out int pdwCookie);

        [PreserveSig] int Unadvise(int dwCookie);

        [PreserveSig] int EnumConnections(out IntPtr ppEnum);
    }

    [ComImport]
    [Guid("39C13A71-011E-11D0-9675-0020AFD8ADB3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOPCDataCallback
    {
        int OnDataChange(
            int dwTransid,
            int hGroup,
            int hrMasterquality,
            int hrQuality,
            int dwCount,
            IntPtr phClientItems,
            IntPtr pvValues,
            IntPtr pwQualities,
            IntPtr pftTimeStamps,
            IntPtr pErrors);

        int OnReadComplete(
            int dwTransid,
            int hGroup,
            int hrMasterquality,
            int hrQuality,
            int dwCount,
            IntPtr phClientItems,
            IntPtr pvValues,
            IntPtr pwQualities,
            IntPtr pftTimeStamps,
            IntPtr pErrors);

        int OnWriteComplete(
            int dwTransid,
            int hGroup,
            int hrMasterquality,
            int hrQuality,
            int dwCount,
            IntPtr phClientItems,
            IntPtr pvValues,
            IntPtr pwQualities,
            IntPtr pftTimeStamps,
            IntPtr pErrors);

        int OnCancelComplete(int dwTransid, int hGroup);
    }
}
