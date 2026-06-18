using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpcBridge.Da;

/// <summary>
/// Browses the address space of an OPC DA server using IOPCBrowseServerAddressSpace.
/// Supports both hierarchical and flat address spaces.
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
    private const int OpcNsHierarchial = 1;
    private const int OpcNsFlat = 2;

    /// <summary>
    /// Browse one level of the address space at <paramref name="path"/> (empty = root).
    /// Returns branches (folders) and leaves (tags).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static OpcTagBrowseResult Browse(
        string progId, string? host, string path,
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
            () => result = BrowseCore(progId, normalizedHost, path ?? string.Empty));

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static OpcTagBrowseResult BrowseCore(string progId, string? host, string path)
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

        try
        {
            if (serverObject is not IOPCBrowseServerAddressSpace browse)
                throw new InvalidOperationException(
                    "This OPC DA server does not support address-space browsing (IOPCBrowseServerAddressSpace).");

            browse.QueryOrganization(out int organization);

            // Flat address space: just enumerate all leaves
            if (organization == OpcNsFlat)
            {
                var flatTags = EnumerateLeaves(browse, OpcFlat);
                return new OpcTagBrowseResult([], flatTags);
            }

            // Hierarchical: navigate to the requested path first
            browse.ChangeBrowsePosition(OpcBrowseTo, string.Empty); // reset to root
            if (!string.IsNullOrEmpty(path))
            {
                browse.ChangeBrowsePosition(OpcBrowseTo, path);
            }

            var branches = EnumerateBranches(browse);
            var tags     = EnumerateLeaves(browse, OpcLeaf);

            return new OpcTagBrowseResult(branches, tags);
        }
        finally
        {
            Marshal.FinalReleaseComObject(serverObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> EnumerateBranches(IOPCBrowseServerAddressSpace browse)
    {
        var names = new List<string>();
        try
        {
            browse.BrowseOPCItemIDs(OpcBranch, string.Empty, 0, 0, out IEnumString enumerator);
            CollectStrings(enumerator, names);
        }
        catch { /* no branches */ }
        return names;
    }

    [SupportedOSPlatform("windows")]
    private static List<OpcTagNode> EnumerateLeaves(IOPCBrowseServerAddressSpace browse, int browseType)
    {
        var names = new List<string>();
        try
        {
            browse.BrowseOPCItemIDs(browseType, string.Empty, 0, 0, out IEnumString enumerator);
            CollectStrings(enumerator, names);
        }
        catch { /* none */ }

        var result = new List<OpcTagNode>(names.Count);
        foreach (string n in names)
        {
            string itemId = n;
            try { browse.GetItemID(n, out itemId); } catch { /* keep */ }
            result.Add(new OpcTagNode(n, itemId));
        }
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static void CollectStrings(IEnumString enumerator, List<string> output)
    {
        if (enumerator is null) return;
        string[] batch = new string[1];
        var fetched = new int[1];
        while (true)
        {
            int hr = enumerator.Next(1, batch, fetched);
            if (fetched[0] == 0 || hr != 0) break;
            if (!string.IsNullOrEmpty(batch[0])) output.Add(batch[0]);
        }
        Marshal.FinalReleaseComObject(enumerator);
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

    // EnumLeaves helper resolves item IDs inline

    [ComImport]
    [Guid("39C13A4F-011E-11D0-9675-0020AFD8ADB3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOPCBrowseServerAddressSpace
    {
        void QueryOrganization(out int nameSpaceType);
        [PreserveSig] int ChangeBrowsePosition(int browseDirection, [MarshalAs(UnmanagedType.LPWStr)] string itemId);
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

public sealed record OpcTagNode(string Name, string ItemId);

public sealed record OpcTagBrowseResult(
    IReadOnlyList<string> Branches,
    IReadOnlyList<OpcTagNode> Tags);
