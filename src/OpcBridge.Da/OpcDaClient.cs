using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using OpcBridge.Core;

namespace OpcBridge.Da;

public sealed class OpcDaClient : IDaClient
{
    private const int OpcDataSourceDevice = 2;
    private static readonly int ItemStateSize = Marshal.SizeOf<OpcItemState>();
    private static readonly int ItemValueOffset = (int)Marshal.OffsetOf<OpcItemState>(nameof(OpcItemState.Value));

    private readonly DaClientOptions options_;
    private OpcComThread? com_thread_;
    private object? server_com_object_;
    private IOPCServer? server_;
    private readonly Dictionary<int, RateGroup> rate_groups_ = new();
    private bool subscriptions_active_;

    /// <summary>
    /// Raised when a DA subscription delivers values via IOPCDataCallback.
    /// Subscribed to once per session by BridgeWorker.
    /// </summary>
    public event Action<IReadOnlyList<BridgeValue>>? OnCallbackValues;

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

        if (hasCredentials)
        {
            ConnectWithImpersonation(progId, host!);
        }
        else
        {
            ConnectDirect(progId, host);
        }

    }

    [SupportedOSPlatform("windows")]
    private void ConnectWithImpersonation(string progId, string host)
    {
        string username = options_.RemoteUsername!.Trim();
        string password = options_.RemotePassword ?? string.Empty;
        string domain   = string.IsNullOrWhiteSpace(options_.RemoteDomain)
            ? host  // use host as domain for local accounts on remote machine
            : options_.RemoteDomain.Trim();

        // LOGON32_LOGON_NEW_CREDENTIALS (9) — best for network/DCOM impersonation
        // LOGON32_PROVIDER_WINNT50 (3)
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
            WindowsIdentity.RunImpersonated(identity.AccessToken, () => ConnectDirect(progId, host));
        }
        finally
        {
            CloseHandle(token);
        }
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

            object serverObject = Activator.CreateInstance(serverType)
                ?? throw new InvalidOperationException($"Failed to create OPC DA server '{progId}'.");
            IOPCServer server = serverObject as IOPCServer
                ?? throw new InvalidOperationException($"COM server '{progId}' does not expose IOPCServer.");

            server_com_object_ = serverObject;
            server_ = server;
            return;
        }

        // Remote DCOM activation via native CoCreateInstanceEx with explicit COAUTHINFO.
        int clsidHr = CLSIDFromProgID(progId, out Guid clsid);
        if (clsidHr < 0)
        {
            throw new InvalidOperationException($"OPC DA server ProgID '{progId}' is not registered (CLSIDFromProgID failed 0x{clsidHr:X8}).");
        }

        // Auth is established by ConnectWithImpersonation's LogonUser + RunImpersonated
        // wrapper, so COSERVERINFO.pAuthInfo = IntPtr.Zero (use the thread's impersonation token).
        COSERVERINFO serverInfo = new()
        {
            pwszName = host,
            pAuthInfo = IntPtr.Zero
        };

        Guid iid = typeof(IOPCServer).GUID;
        MULTI_QI[] results = new MULTI_QI[1];
        results[0].pIID = iid;
        results[0].pItf = IntPtr.Zero;
        results[0].hr = 0;

        int hr = CoCreateInstanceEx(
            ref clsid,
            IntPtr.Zero,
            CLSCTX_REMOTE_SERVER,
            ref serverInfo,
            1,
            results);

        if (hr < 0)
        {
            string hint = hr == E_ACCESSDENIED
                ? " — access denied. Verify DCOM permissions, credentials, and authentication level on the remote host."
                : string.Empty;
            throw new InvalidOperationException(
                $"CoCreateInstanceEx for '{progId}' on host '{host}' failed (0x{hr:X8}).{hint}");
        }

        if (results[0].hr < 0)
        {
            throw new InvalidOperationException(
                $"Remote activation of '{progId}' on host '{host}' failed (QueryInterface 0x{results[0].hr:X8}).");
        }

        object serverObj = Marshal.GetObjectForIUnknown(results[0].pItf);
        IOPCServer remoteServer = serverObj as IOPCServer
            ?? throw new InvalidOperationException($"Remote COM server '{progId}' does not expose IOPCServer.");

        server_com_object_ = serverObj;
        server_ = remoteServer;
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
    public Task<bool> WriteAsync(string daItemId, object? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(daItemId) || value is null)
        {
            return Task.FromResult(false);
        }

        EnsureConnected();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA requires Windows.");
        }

        bool success = com_thread_!.EnqueueAndWait(() => WriteOnStaThread(daItemId, value));
        return Task.FromResult(success);
    }

    private bool WriteOnStaThread(string daItemId, object value)
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
                if (string.Equals(group.Bindings[i].DaItemId, daItemId, StringComparison.OrdinalIgnoreCase))
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

            // When a subscription is active, values flow via OnCallbackValues; only device-read
            // when subscriptions are off or never established.
            if (!subscriptions_active_ || group.ConnectionPoint is null)
            {
                IReadOnlyList<BridgeValue> groupValues = ReadGroup(group);
                values.AddRange(groupValues);
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

        try
        {
            if (group.ComObject is not IConnectionPointContainer cpc)
            {
                subscriptions_active_ = false;
                return;
            }

            Guid callbackIid = typeof(IOPCDataCallback).GUID;
            int hr = cpc.FindConnectionPoint(ref callbackIid, out IConnectionPoint cp);
            if (hr < 0)
            {
                subscriptions_active_ = false;
                return;
            }

            // Build client-handle → item-id map for the callback to unpack notifications.
            Dictionary<int, string> handleMap = new(group.Bindings.Length);
            for (int i = 0; i < group.Bindings.Length; i++)
            {
                handleMap[i + 1] = group.Bindings[i].DaItemId;
            }

            Action<IReadOnlyList<BridgeValue>> handler = OnCallbackValues ?? (_ => { });
            OpcDaCallbackSink sink = new(options_.SourceId, handleMap, handler);
            hr = cp.Advise(sink, out int cookie);
            if (hr < 0)
            {
                subscriptions_active_ = false;
                return;
            }

            group.Sink = sink;
            group.ConnectionPoint = cp;
            group.CallbackCookie = cookie;
            subscriptions_active_ = true;
        }
        catch (Exception)
        {
            subscriptions_active_ = false;
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


    private static void EnsureGroupItemsConfigured(RateGroup group, IReadOnlyList<TagMapping> mappings)
    {
        if (group.Bindings.Length != 0)
        {
            if (group.Bindings.Length != mappings.Count)
            {
                throw new InvalidOperationException("OPC DA mappings changed after the client was connected.");
            }

            for (int i = 0; i < group.Bindings.Length; i++)
            {
                if (!string.Equals(group.Bindings[i].DaItemId, mappings[i].DaItemId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("OPC DA mappings changed after the client was connected.");
                }
            }

            return;
        }

        IOPCItemMgt itemManagement = group.ItemManagement!;
        OpcItemDefinition[] definitions = new OpcItemDefinition[mappings.Count];
        for (int i = 0; i < mappings.Count; i++)
        {
            definitions[i] = new OpcItemDefinition
            {
                AccessPath = string.Empty,
                ItemId = mappings[i].DaItemId,
                IsActive = 1,
                ClientHandle = i + 1,
                RequestedDataType = (short)MapVarType(mappings[i].DataType)
            };
        }

        IntPtr resultsPointer = IntPtr.Zero;
        IntPtr errorsPointer = IntPtr.Zero;
        List<int>? cleanupHandles = null;

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

            ItemBinding[] bindings = new ItemBinding[definitions.Length];
            cleanupHandles = new List<int>(definitions.Length);
            int resultSize = Marshal.SizeOf<OpcItemResult>();

            for (int i = 0; i < definitions.Length; i++)
            {
                IntPtr resultPointer = IntPtr.Add(resultsPointer, i * resultSize);
                OpcItemResult result = Marshal.PtrToStructure<OpcItemResult>(resultPointer);

                if (result.BlobPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(result.BlobPointer);
                }

                ThrowOnFailed(itemErrors[i], $"Failed to add OPC DA item '{mappings[i].DaItemId}'.");

                bindings[i] = new ItemBinding(mappings[i].DaItemId, result.ServerHandle);
                cleanupHandles.Add(result.ServerHandle);
            }

            group.Bindings = bindings;
        }
        catch
        {
            if (cleanupHandles is { Count: > 0 })
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

        int[] serverHandles = new int[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            serverHandles[i] = bindings[i].ServerHandle;
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

            int[] itemErrors = new int[bindings.Length];
            Marshal.Copy(errorsPointer, itemErrors, 0, bindings.Length);

            BridgeValue[] values = new BridgeValue[bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
            {
                IntPtr itemStatePointer = IntPtr.Add(itemStatesPointer, i * ItemStateSize);
                OpcItemState itemState = Marshal.PtrToStructure<OpcItemState>(itemStatePointer);

                try
                {
                    ThrowOnFailed(itemErrors[i], $"OPC DA item read failed for '{bindings[i].DaItemId}'.");

                    int quality = (ushort)itemState.Quality;
                    values[i] = new BridgeValue(
                        options_.SourceId,
                        bindings[i].DaItemId,
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
                server_.RemoveGroup(group.ServerGroupHandle, 0);
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

    private static void RemoveGroupItems(RateGroup group)
    {
        if (group.Bindings.Length == 0)
        {
            return;
        }

        int[] serverHandles = new int[group.Bindings.Length];
        for (int i = 0; i < group.Bindings.Length; i++)
        {
            serverHandles[i] = group.Bindings[i].ServerHandle;
        }

        RemoveItems(group.ItemManagement, serverHandles);
        group.Bindings = [];
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
    private const int CLSCTX_REMOTE_SERVER = 0x10;
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstanceEx(
        ref Guid clsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        ref COSERVERINFO pServerInfo,
        uint dwCount,
        [In, Out] MULTI_QI[] pResults);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COSERVERINFO
    {
        public IntPtr dwReserved;
        public string pwszName;
        public IntPtr pAuthInfo; // pointer to COAUTHINFO
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct MULTI_QI
    {
        public Guid pIID;
        public IntPtr pItf;
        public int hr;
    }


    private sealed record ItemBinding(string DaItemId, int ServerHandle);

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

    [ComImport]
    [Guid("B196B284-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnectionPointContainer
    {
        int EnumConnectionPoints(out IntPtr ppEnum);

        int FindConnectionPoint(ref Guid riid, out IConnectionPoint ppCP);
    }

    [ComImport]
    [Guid("B196B286-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnectionPoint
    {
        int GetConnectionInterface(out Guid pIID);

        int GetConnectionPointContainer(out IConnectionPointContainer ppCPC);

        int Advise([MarshalAs(UnmanagedType.IUnknown)] object pUnkSink, out int pdwCookie);

        int Unadvise(int dwCookie);

        int EnumConnections(out IntPtr ppEnum);
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
