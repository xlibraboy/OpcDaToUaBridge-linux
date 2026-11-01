using Newtonsoft.Json.Linq;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Reads and writes the port settings the bridge persists between runs
/// (<c>Bridge:HttpPort</c> / <c>Bridge:OpcUaPort</c> in the data directory's appsettings.json).
/// Startup's auto-assignment and the Monitor → Port Configuration card share this one
/// implementation, so a port the card saves is the port startup probes on the next run.
/// </summary>
public static class PortConfigStore
{
    public static string SettingsPath => DataDirectory.Combine("appsettings.json");

    private static string UaSettingsPath => DataDirectory.Combine("ua-settings.json");

    /// <summary>
    /// Bridge ports from the data directory's appsettings.json, or the given defaults when
    /// the file (or the keys) are absent. This is what startup probes before binding.
    /// </summary>
    public static (int HttpPort, int UaPort) ReadBridgePorts(int httpDefault, int uaDefault)
    {
        JObject? settings = TryLoad(SettingsPath);
        int httpPort = settings?["Bridge"]?["HttpPort"]?.Value<int>() ?? httpDefault;
        int uaPort = settings?["Bridge"]?["OpcUaPort"]?.Value<int>() ?? uaDefault;
        return (httpPort, uaPort);
    }

    /// <summary>
    /// The port blocks this install auto-assigns from
    /// (<c>Bridge:HttpPortRange</c> / <c>Bridge:OpcUaPortRange</c>, strings like
    /// <c>"9000-9010"</c>), or the given defaults when the keys are absent. A missing key is
    /// not a problem. A malformed value falls back to its default and is reported, as is a
    /// pair of blocks that overlap — startup logs them and the Port Configuration card shows
    /// them, so a typo in a plant config is visible instead of silently ignored.
    /// </summary>
    public static (PortRange Http, PortRange Ua, IReadOnlyList<string> Problems) ReadBridgePortRanges(
        PortRange httpDefault,
        PortRange uaDefault)
    {
        List<string> problems = new();
        JObject? settings = TryLoad(SettingsPath);
        PortRange httpRange = ReadRange(settings, "HttpPortRange", httpDefault, problems);
        PortRange uaRange = ReadRange(settings, "OpcUaPortRange", uaDefault, problems);

        if (httpRange.Overlaps(uaRange))
        {
            problems.Add(
                $"Bridge:HttpPortRange ({httpRange}) overlaps Bridge:OpcUaPortRange ({uaRange}); "
                + "the OPC UA scan skips the HTTP port, but each protocol should own its own block.");
        }

        return (httpRange, uaRange, problems);
    }

    private static PortRange ReadRange(
        JObject? settings,
        string key,
        PortRange fallback,
        List<string> problems)
    {
        JToken? token = settings?["Bridge"]?[key];
        if (token is null)
        {
            return fallback;
        }

        if (token.Type != JTokenType.String || !PortRange.TryParse(token.ToString(), out PortRange range))
        {
            problems.Add(
                $"Bridge:{key} is {token.ToString(Newtonsoft.Json.Formatting.None)}, which is not a port "
                + $"range (expected \"start-end\", e.g. \"{fallback}\") — using {fallback}.");
            return fallback;
        }

        return range;
    }

    /// <summary>
    /// The UA endpoint URL the next start will bind. ua-settings.json wins over appsettings.json
    /// because <see cref="OpcBridge.Ua.UaServerHost"/> deserializes it over the bound options,
    /// so the card has to read it to report the saved UA port that will actually stick.
    /// </summary>
    public static string? ReadUaEndpointUrl()
    {
        string? fromUaSettings = TryLoad(UaSettingsPath)?["EndpointUrl"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(fromUaSettings))
        {
            return fromUaSettings;
        }

        return TryLoad(SettingsPath)?["Ua"]?["EndpointUrl"]?.Value<string>();
    }

    /// <summary>The TCP port inside an endpoint URL, or null when it has none / will not parse.</summary>
    public static int? PortOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            int port = new Uri(url).Port;
            return port is > 0 and <= 65535 ? port : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Persists the ports to the data directory's appsettings.json — the same file startup
    /// reads — creating the file (and the Bridge section) when absent. <c>Ua:EndpointUrl</c>'s
    /// port is patched when that key is present, because the endpoint URL, not
    /// <c>Bridge:OpcUaPort</c>, is what the UA server binds. The auto-assign blocks are written
    /// too when given, so a save and a startup move leave ports and blocks in step.
    /// </summary>
    public static void Save(int httpPort, int uaPort, PortRange? httpRange = null, PortRange? uaRange = null)
    {
        JObject settings = TryLoad(SettingsPath) ?? new JObject();
        if (settings["Bridge"] is not JObject bridge)
        {
            bridge = new JObject();
            settings["Bridge"] = bridge;
        }

        bridge["HttpPort"] = httpPort;
        bridge["OpcUaPort"] = uaPort;
        if (httpRange is PortRange http)
        {
            bridge["HttpPortRange"] = http.ToString();
        }

        if (uaRange is PortRange ua)
        {
            bridge["OpcUaPortRange"] = ua.ToString();
        }

        if (settings["Ua"]?["EndpointUrl"] is not null)
        {
            settings["Ua"]!["EndpointUrl"] = PatchPortInUrl(settings["Ua"]!["EndpointUrl"]!.ToString(), uaPort);
        }

        File.WriteAllText(SettingsPath, settings.ToString(Newtonsoft.Json.Formatting.Indented));
    }

    /// <summary>
    /// Deletes pki/own/cert.der so the UA stack re-issues the own certificate on the next
    /// start. Startup does this whenever auto-assignment moves the UA port; a port change
    /// from the dashboard does the same. Clients must re-trust the new certificate.
    /// Returns true when a file was deleted.
    /// </summary>
    public static bool DeleteUaCertificateIfPresent()
    {
        string certDer = Path.Combine(DataDirectory.Value, "pki", "own", "cert.der");
        if (!File.Exists(certDer))
        {
            return false;
        }

        File.Delete(certDer);
        return true;
    }

    /// <summary>
    /// Replaces the port in a URL string (e.g. opc.tcp://0.0.0.0:4840/...).
    /// Handles URLs with or without explicit port.
    /// </summary>
    public static string PatchPortInUrl(string url, int port)
    {
        if (string.IsNullOrEmpty(url)) return url;
        try
        {
            var uri = new Uri(url);
            var builder = new UriBuilder(uri) { Port = port };
            return builder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            // Fallback: manual replacement
            int lastColon = url.LastIndexOf(':');
            int lastSlash = url.LastIndexOf('/');
            if (lastColon > lastSlash && int.TryParse(url[(lastColon + 1)..], out _))
                return url[..(lastColon + 1)] + port + url[(url.IndexOf('/', lastColon)..)];
            // No port in URL — append it
            return url.TrimEnd('/') + $":{port}";
        }
    }

    private static JObject? TryLoad(string path)
    {
        try
        {
            return JObject.Parse(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
