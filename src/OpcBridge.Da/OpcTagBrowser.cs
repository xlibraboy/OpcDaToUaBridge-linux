using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpcBridge.Da;

/// <summary>
/// Browses the address space of an OPC DA server using IOPCBrowseServerAddressSpace.
/// Supports both hierarchical and flat address spaces. Servers differ in how faithfully they
/// implement browsing, so a failure inside one browse call becomes a warning on the result
/// instead of vanishing — the Tag Browser shows it and the log carries it.
/// </summary>
public static class OpcTagBrowser
{
    // Browse types
    private const int OpcBranch = 1;
    private const int OpcLeaf = 2;
    private const int OpcFlat = 3;

    // ChangeBrowsePosition directions
    private const int OpcBrowseUp = 1;
    private const int OpcBrowseDown = 2;
    private const int OpcBrowseTo = 3;

    // Namespace organization
    private const int OpcNsFlat = 2;

    // How many names an enumerator is asked for per COM call.
    private const int EnumerationBatchSize = 64;

    // A recursive browse of a messy server can collect a warning per folder; keep the payload sane.
    private const int MaxWarnings = 25;

    /// <summary>
    /// Browse the address space at <paramref name="path" />.
    /// Non-recursive browsing returns one level; recursive browsing returns every leaf below the path.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static OpcTagBrowseResult Browse(
        string progId, string? host, string path, bool recursive,
        string? username, string? password, string? domain)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("OPC DA browsing requires Windows.");

        if (string.IsNullOrWhiteSpace(progId))
            throw new InvalidOperationException("ProgID is required.");

        string? normalizedHost = NormalizeHost(host);
        OpcTagBrowseResult result = new([], []);

        WindowsImpersonation.Run(
            normalizedHost is null ? null : username,
            password, domain, normalizedHost ?? string.Empty,
            () => result = BrowseCore(progId, normalizedHost, path ?? string.Empty, recursive));

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static OpcTagBrowseResult BrowseCore(string progId, string? host, string path, bool recursive)
    {
        Type? serverType = host is null
            ? Type.GetTypeFromProgID(progId, throwOnError: false)
            : Type.GetTypeFromProgID(progId, host, throwOnError: false);

        if (serverType is null)
            throw new InvalidOperationException(
                host is null
                    ? $"OPC DA server '{progId}' is not registered locally."
                    : $"OPC DA server '{progId}' is not available on host '{host}'.");

        object serverObject = Activator.CreateInstance(serverType)
            ?? throw new InvalidOperationException($"Failed to create OPC DA server '{progId}'.");

        object? groupObject = null;
        int serverGroupHandle = 0;
        List<string> warnings = new();
        try
        {
            if (serverObject is not IOPCBrowseServerAddressSpace browse)
                throw new InvalidOperationException(
                    "This OPC DA server does not support address-space browsing (IOPCBrowseServerAddressSpace).");

            IOPCItemMgt? itemManagement = TryCreateMetadataItemManagement(serverObject, out groupObject, out serverGroupHandle, warnings);

            int organization = 0;
            try
            {
                browse.QueryOrganization(out organization);
            }
            catch (Exception exception)
            {
                warnings.Add($"QueryOrganization failed ({Describe(exception)}); assuming a hierarchical address space.");
            }

            string normalizedPath = NormalizePath(path);

            if (organization == OpcNsFlat)
            {
                List<OpcTagNode> flatTags = EnumerateLeaves(browse, itemManagement, OpcFlat, string.Empty, warnings);
                return new OpcTagBrowseResult([], flatTags, CapWarnings(warnings));
            }

            MoveToRoot(browse, warnings);
            MoveToPath(browse, normalizedPath);

            if (recursive)
            {
                List<OpcTagNode> allTags = new();
                CollectLeavesRecursive(browse, itemManagement, normalizedPath, allTags, new HashSet<string>(StringComparer.OrdinalIgnoreCase), warnings);

                return allTags.Count == 0 && normalizedPath.Length == 0
                    ? WithFlatFallback(browse, itemManagement, allTags, warnings)
                    : new OpcTagBrowseResult([], allTags, CapWarnings(warnings));
            }

            List<string> branches = EnumerateBranches(browse, warnings);
            List<OpcTagNode> tags = EnumerateLeaves(browse, itemManagement, OpcLeaf, normalizedPath, warnings);

            return branches.Count == 0 && tags.Count == 0 && normalizedPath.Length == 0
                ? WithFlatFallback(browse, itemManagement, tags, warnings)
                : new OpcTagBrowseResult(branches, tags, CapWarnings(warnings));
        }
        finally
        {
            ReleaseMetadataItemManagement(serverObject as IOPCServer, groupObject, serverGroupHandle);
            Marshal.FinalReleaseComObject(serverObject);
        }
    }

    /// <summary>
    /// A server that reports a hierarchical address space but returns nothing at its root may
    /// really only support flat browsing, so try OPC_FLAT before reporting an empty namespace.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static OpcTagBrowseResult WithFlatFallback(
        IOPCBrowseServerAddressSpace browse, IOPCItemMgt? itemManagement, List<OpcTagNode> found, List<string> warnings)
    {
        List<OpcTagNode> flatTags = EnumerateLeaves(browse, itemManagement, OpcFlat, string.Empty, warnings);
        if (flatTags.Count == 0)
        {
            return new OpcTagBrowseResult([], found, CapWarnings(warnings));
        }

        warnings.Add("Hierarchical browse returned no items; used OPC_FLAT instead.");
        return new OpcTagBrowseResult([], flatTags, CapWarnings(warnings));
    }

    [SupportedOSPlatform("windows")]
    private static void MoveToRoot(IOPCBrowseServerAddressSpace browse, List<string> warnings)
    {
        int hr = browse.ChangeBrowsePosition(OpcBrowseTo, string.Empty);
        if (hr < 0)
        {
            hr = browse.ChangeBrowsePosition(OpcBrowseTo, null);
        }

        if (hr < 0)
        {
            // A fresh activation starts at the root, so this is only worth a note, not a failure.
            warnings.Add($"ChangeBrowsePosition(OPC_BROWSE_TO root) failed: HRESULT 0x{hr:X8}; browsing from the server's current position.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void MoveToPath(IOPCBrowseServerAddressSpace browse, string path)
    {
        string normalizedPath = NormalizePath(path);
        if (normalizedPath.Length == 0)
        {
            return;
        }

        string[] segments = normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            int hr = browse.ChangeBrowsePosition(OpcBrowseDown, segments[i]);
            if (hr < 0)
            {
                throw new InvalidOperationException($"OPC DA browse path '{normalizedPath}' is not available.", Marshal.GetExceptionForHR(hr));
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> EnumerateBranches(IOPCBrowseServerAddressSpace browse, List<string> warnings)
    {
        List<string> names = new();
        try
        {
            browse.BrowseOPCItemIDs(OpcBranch, string.Empty, 0, 0, out IEnumString enumerator);
            DrainEnumerator(enumerator, names, warnings, "OPC_BRANCH enumeration");
        }
        catch (Exception exception)
        {
            warnings.Add($"OPC_BRANCH browse failed: {Describe(exception)}");
        }
        return names;
    }

    [SupportedOSPlatform("windows")]
    private static List<OpcTagNode> EnumerateLeaves(IOPCBrowseServerAddressSpace browse, IOPCItemMgt? itemManagement, int browseType, string path, List<string> warnings)
    {
        List<string> names = new();
        try
        {
            browse.BrowseOPCItemIDs(browseType, string.Empty, 0, 0, out IEnumString enumerator);
            DrainEnumerator(enumerator, names, warnings, $"{BrowseTypeName(browseType)} enumeration");
        }
        catch (Exception exception)
        {
            warnings.Add($"{BrowseTypeName(browseType)} browse failed: {Describe(exception)}");
        }

        string[] itemIds = new string[names.Count];
        int getItemIdFailures = 0;
        for (int i = 0; i < names.Count; i++)
        {
            itemIds[i] = names[i];
            try { browse.GetItemID(names[i], out itemIds[i]); } catch { getItemIdFailures++; }
        }

        if (getItemIdFailures > 0)
        {
            warnings.Add($"GetItemID failed for {getItemIdFailures} of {names.Count} item(s); showing the browse names as item IDs.");
        }

        OpcTagMetadata[] metadata = ReadNativeMetadata(itemManagement, itemIds);
        List<OpcTagNode> result = new(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            string displayName = path.Length == 0 ? names[i] : string.Concat(path, ".", names[i]);
            result.Add(new OpcTagNode(displayName, itemIds[i], metadata[i].CanonicalDataType, metadata[i].AccessRights));
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static void CollectLeavesRecursive(IOPCBrowseServerAddressSpace browse, IOPCItemMgt? itemManagement, string path, List<OpcTagNode> tags, HashSet<string> visitedPaths, List<string> warnings)
    {
        if (!visitedPaths.Add(path))
        {
            return;
        }

        tags.AddRange(EnumerateLeaves(browse, itemManagement, OpcLeaf, path, warnings));

        foreach (string branch in EnumerateBranches(browse, warnings))
        {
            string childPath = path.Length == 0 ? branch : string.Concat(path, ".", branch);
            int downHr = browse.ChangeBrowsePosition(OpcBrowseDown, branch);
            if (downHr < 0)
            {
                warnings.Add($"Skipped folder '{childPath}': OPC_BROWSE_DOWN failed: HRESULT 0x{downHr:X8}.");
                continue;
            }

            try
            {
                CollectLeavesRecursive(browse, itemManagement, childPath, tags, visitedPaths, warnings);
            }
            finally
            {
                int upHr = browse.ChangeBrowsePosition(OpcBrowseUp, string.Empty);
                if (upHr < 0)
                {
                    Marshal.ThrowExceptionForHR(upHr);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DrainEnumerator(IEnumString enumerator, List<string> output, List<string> warnings, string context)
    {
        if (enumerator is null) return;

        try
        {
            DrainStrings(EnumerationBatchSize, (batch, fetched) => enumerator.Next(batch.Length, batch, fetched), output, warnings, context);
        }
        finally
        {
            Marshal.FinalReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Drains an OPC enumerator in batches. Every item a call reports is consumed, including the
    /// ones some servers hand back together with S_FALSE — the loop only stops at the end of the
    /// sequence or a hard failure, never on an HRESULT that still carries data.
    /// </summary>
    internal static void DrainStrings(int batchSize, Func<string[], int[], int> next, List<string> output, List<string> warnings, string context)
    {
        string[] batch = new string[batchSize];
        int[] fetched = new int[1];

        while (true)
        {
            int hr = next(batch, fetched);
            for (int i = 0; i < fetched[0] && i < batch.Length; i++)
            {
                if (!string.IsNullOrEmpty(batch[i]))
                {
                    output.Add(batch[i]);
                }
            }

            if (hr < 0)
            {
                warnings.Add($"{context} failed: HRESULT 0x{hr:X8}.");
                break;
            }

            if (fetched[0] == 0)
            {
                break;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IOPCItemMgt? TryCreateMetadataItemManagement(object serverObject, out object? groupObject, out int serverGroupHandle, List<string> warnings)
    {
        groupObject = null;
        serverGroupHandle = 0;

        if (serverObject is not IOPCServer server)
        {
            warnings.Add("The server does not expose IOPCServer; data types and access rights are unavailable.");
            return null;
        }

        Guid itemManagementGuid = typeof(IOPCItemMgt).GUID;

        try
        {
            int hresult = server.AddGroup(
                "OpcBridge_BrowseMetadata",
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
            if (hresult < 0)
            {
                warnings.Add($"The metadata group could not be created (HRESULT 0x{hresult:X8}); data types and access rights are unavailable.");
                ReleaseMetadataItemManagement(server, groupObject, serverGroupHandle);
                groupObject = null;
                serverGroupHandle = 0;
                return null;
            }

            if (groupObject is not IOPCItemMgt itemManagement)
            {
                warnings.Add("The server's group does not expose IOPCItemMgt; data types and access rights are unavailable.");
                ReleaseMetadataItemManagement(server, groupObject, serverGroupHandle);
                groupObject = null;
                serverGroupHandle = 0;
                return null;
            }

            return itemManagement;
        }
        catch (Exception exception)
        {
            warnings.Add($"The metadata group could not be created ({Describe(exception)}); data types and access rights are unavailable.");
            ReleaseMetadataItemManagement(server, groupObject, serverGroupHandle);
            groupObject = null;
            serverGroupHandle = 0;
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static OpcTagMetadata[] ReadNativeMetadata(IOPCItemMgt? itemManagement, IReadOnlyList<string> itemIds)
    {
        OpcTagMetadata[] metadata = new OpcTagMetadata[itemIds.Count];
        if (itemManagement is null || itemIds.Count == 0)
        {
            return metadata;
        }

        OpcItemDefinition[] definitions = new OpcItemDefinition[itemIds.Count];
        for (int i = 0; i < itemIds.Count; i++)
        {
            definitions[i] = new OpcItemDefinition
            {
                AccessPath = string.Empty,
                ItemId = itemIds[i],
                IsActive = 0,
                ClientHandle = i + 1,
                RequestedDataType = 0
            };
        }

        IntPtr resultsPointer = IntPtr.Zero;
        IntPtr errorsPointer = IntPtr.Zero;
        List<int>? cleanupHandles = null;

        try
        {
            int hresult = itemManagement.AddItems(definitions.Length, definitions, out resultsPointer, out errorsPointer);
            if (hresult < 0 || resultsPointer == IntPtr.Zero || errorsPointer == IntPtr.Zero)
            {
                return metadata;
            }

            int[] itemErrors = new int[definitions.Length];
            Marshal.Copy(errorsPointer, itemErrors, 0, definitions.Length);

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

                if (itemErrors[i] < 0)
                {
                    continue;
                }

                metadata[i] = new OpcTagMetadata(result.CanonicalDataType, result.AccessRights);
                if (result.ServerHandle != 0)
                {
                    cleanupHandles.Add(result.ServerHandle);
                }
            }
        }
        catch
        {
            return metadata;
        }
        finally
        {
            if (cleanupHandles is { Count: > 0 })
            {
                RemoveItems(itemManagement, cleanupHandles.ToArray());
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

        return metadata;
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseMetadataItemManagement(IOPCServer? server, object? groupObject, int serverGroupHandle)
    {
        try
        {
            if (server is not null && serverGroupHandle != 0)
            {
                server.RemoveGroup(serverGroupHandle, 0);
            }
        }
        catch { }

        if (groupObject is not null)
        {
            Marshal.FinalReleaseComObject(groupObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveItems(IOPCItemMgt itemManagement, int[] serverHandles)
    {
        if (serverHandles.Length == 0)
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

    private readonly record struct OpcTagMetadata(short? CanonicalDataType, int? AccessRights);

    private static string Describe(Exception exception) => $"HRESULT 0x{exception.HResult:X8}: {exception.Message}";

    private static string BrowseTypeName(int browseType) => browseType switch
    {
        OpcBranch => "OPC_BRANCH",
        OpcLeaf => "OPC_LEAF",
        OpcFlat => "OPC_FLAT",
        _ => $"browse type {browseType}"
    };

    private static IReadOnlyList<string> CapWarnings(List<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return Array.Empty<string>();
        }

        List<string> distinct = warnings.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count <= MaxWarnings)
        {
            return distinct;
        }

        string trailer = $"…{distinct.Count - MaxWarnings} more warning(s) not shown.";
        List<string> capped = distinct.Take(MaxWarnings).ToList();
        capped.Add(trailer);
        return capped;
    }

    private static string NormalizePath(string? path)
    {
        return path?.Trim().Trim('.') ?? string.Empty;
    }

    private static string? NormalizeHost(string? host)
    {
        string t = host?.Trim() ?? string.Empty;
        if (t.Length == 0 ||
            string.Equals(t, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, ".", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return null;
        return t;
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
    [Guid("39C13A4F-011E-11D0-9675-0020AFD8ADB3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOPCBrowseServerAddressSpace
    {
        void QueryOrganization(out int nameSpaceType);
        [PreserveSig] int ChangeBrowsePosition(int browseDirection, [MarshalAs(UnmanagedType.LPWStr)] string? itemId);
        void BrowseOPCItemIDs(int browseFilterType,
            [MarshalAs(UnmanagedType.LPWStr)] string filterCriteria,
            short variantDataTypeFilter, int accessRightsFilter,
            [MarshalAs(UnmanagedType.Interface)] out IEnumString ppIEnumString);
        void GetItemID([MarshalAs(UnmanagedType.LPWStr)] string itemDataId,
            [MarshalAs(UnmanagedType.LPWStr)] out string szItemId);
        void BrowseAccessPaths([MarshalAs(UnmanagedType.LPWStr)] string itemId,
            [MarshalAs(UnmanagedType.Interface)] out IEnumString ppIEnumString);
    }

    [ComImport]
    [Guid("00000101-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumString
    {
        [PreserveSig]
        int Next(int celt,
            [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 0)] string[] rgelt,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] pceltFetched);
        [PreserveSig] int Skip(int celt);
        [PreserveSig] int Reset();
        void Clone(out IEnumString ppenum);
    }
}

public sealed record OpcTagNode(string Name, string ItemId, short? CanonicalDataType = null, int? AccessRights = null);

public sealed record OpcTagBrowseResult(
    IReadOnlyList<string> Branches,
    IReadOnlyList<OpcTagNode> Tags,
    IReadOnlyList<string>? Warnings = null);
