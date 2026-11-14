using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace OpcBridge.Client;

/// <summary>
/// What an app tells the bridge about itself when it opens the <c>/hmi</c> hub: a stable
/// per-process id, the PC/device name, its OS and its app version. The bridge shows these in
/// the dashboard's Bridge Clients card, so an operator can tell which PC a live link belongs
/// to. A browser cannot read its PC's name, so the Logic page sends only its id — the bridge
/// fills in the address, browser and OS from the request.
/// </summary>
public static class ClientIdentity
{
    /// <summary>Stable for the life of this process: a reconnect replaces its old link instead of adding a client.</summary>
    public static string CurrentId { get; } = Guid.NewGuid().ToString("N");

    public static string MachineName
    {
        get
        {
            try
            {
                return Environment.MachineName;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public static string OsDescription
    {
        get
        {
            try
            {
                return RuntimeInformation.OSDescription;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>The app's informational version, without the SDK's <c>+commit</c> suffix.</summary>
    public static string AppVersion(Assembly? assembly)
    {
        if (assembly is null)
        {
            return string.Empty;
        }

        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;
        int metadata = version.IndexOf('+');
        return metadata >= 0 ? version[..metadata] : version;
    }

    /// <summary>
    /// The query the hub URL carries. Blank values are omitted, so a client that cannot read a
    /// value does not send an empty claim the bridge would have to filter.
    /// </summary>
    public static string HubQuery(
        string client,
        string? clientId = null,
        string? machine = null,
        string? os = null,
        string? version = null)
    {
        var query = new StringBuilder();
        Append(query, "client", client);
        Append(query, "clientId", clientId ?? CurrentId);
        Append(query, "machine", machine ?? MachineName);
        Append(query, "os", os ?? OsDescription);
        Append(query, "version", version);
        return query.ToString();
    }

    public static string AppendQuery(string url, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return url;
        }

        return url + (url.Contains('?') ? '&' : '?') + query;
    }

    private static void Append(StringBuilder query, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (query.Length > 0)
        {
            query.Append('&');
        }

        query.Append(key).Append('=').Append(Uri.EscapeDataString(value.Trim()));
    }
}
