using System.Net;

namespace OpcBridge.App;

public static class UaEndpointGuard
{
    private const int DefaultOpcUaPort = 4840;

    // true if candidate would connect to our own UA server endpoint
    public static bool TargetsSelf(string candidateEndpointUrl, string serverEndpointUrl)
    {
        if (!TryParseOpcTcp(candidateEndpointUrl, out var candidate)
            || !TryParseOpcTcp(serverEndpointUrl, out var server))
        {
            return false;
        }

        var candidatePort = candidate.IsDefaultPort ? DefaultOpcUaPort : candidate.Port;
        var serverPort = server.IsDefaultPort ? DefaultOpcUaPort : server.Port;
        if (candidatePort != serverPort)
            return false;

        var candidatePath = NormalizePath(candidate.AbsolutePath);
        var serverPath = NormalizePath(server.AbsolutePath);
        if (!string.Equals(candidatePath, serverPath, StringComparison.OrdinalIgnoreCase))
            return false;

        return IsSelfHostPair(candidate.Host, server.Host);
    }

    private static bool TryParseOpcTcp(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return false;

        if (!string.Equals(parsed.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase))
            return false;

        uri = parsed;
        return true;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return string.Empty;

        return path.TrimEnd('/');
    }

    private static bool IsSelfHostPair(string candidateHost, string serverHost)
    {
        if (!IsCandidateLocalHost(candidateHost))
            return false;

        return IsServerLocalOrWildcard(serverHost)
            || string.Equals(candidateHost, serverHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCandidateLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var trimmed = host.Trim().Trim('[', ']');
        return trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("127.0.0.1", StringComparison.Ordinal)
            || trimmed.Equals("::1", StringComparison.Ordinal)
            || trimmed.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServerLocalOrWildcard(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var trimmed = host.Trim().Trim('[', ']');
        return trimmed.Equals("0.0.0.0", StringComparison.Ordinal)
            || trimmed.Equals("+", StringComparison.Ordinal)
            || trimmed.Equals("*", StringComparison.Ordinal)
            || trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("127.0.0.1", StringComparison.Ordinal)
            || trimmed.Equals("::1", StringComparison.Ordinal)
            || trimmed.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase);
    }
}
