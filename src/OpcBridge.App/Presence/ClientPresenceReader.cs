using System.Net;
using Microsoft.AspNetCore.Http;
using OpcBridge.App.Auth;

namespace OpcBridge.App.Presence;

/// <summary>One live <c>/hmi</c> connection, as read from the hub request.</summary>
internal sealed record ClientPresenceConnection(
    string ConnectionId,
    string ClientKey,
    string ClientId,
    string Kind,
    string MachineName,
    string Address,
    string Username,
    string Platform,
    string UserAgent,
    string AppVersion);

/// <summary>
/// Reads what a client said about itself on its hub connection and joins it with what the
/// request proves: the peer address and the signed-in session. Query values are claimed by
/// the client, so they are trimmed and length-capped, and never used for authorization.
/// </summary>
internal static class ClientPresenceReader
{
    private static readonly string[] KnownKinds = { "hmi", "logic", "browser", "mobile" };

    public static ClientPresenceConnection Read(string connectionId, HttpContext? http)
    {
        IQueryCollection query = http?.Request.Query ?? new QueryCollection();
        string userAgent = Limit(http?.Request.Headers.UserAgent.ToString(), 300);
        string clientId = Limit(Get(query, "clientId"), 64);
        if (clientId.Length == 0)
        {
            clientId = Limit(connectionId, 64);
        }

        string kind = NormalizeKind(Get(query, "client"), userAgent);
        string machine = Limit(Get(query, "machine"), 80);
        if (machine.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            machine = string.Empty;
        }

        string operatingSystem = CleanOs(Limit(Get(query, "os"), 120));
        string platform = operatingSystem.Length > 0 ? operatingSystem : ClientPlatform.Describe(userAgent);
        string username = http?.GetAuthCaller()?.Username ?? string.Empty;
        string address = FormatAddress(http?.Connection.RemoteIpAddress);

        // The same app instance can carry links from more than one account (a shared browser
        // profile): each signed-in account is its own client row.
        string clientKey = clientId + "\u001f" + kind + "\u001f" + username;

        return new ClientPresenceConnection(
            connectionId,
            clientKey,
            clientId,
            kind,
            machine,
            address,
            username,
            Limit(platform, 80),
            userAgent,
            Limit(Get(query, "version"), 40));
    }

    private static string Get(IQueryCollection query, string key) =>
        query.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues value)
            ? value.ToString()
            : string.Empty;

    private static string NormalizeKind(string claimed, string userAgent)
    {
        string kind = claimed.Trim().ToLowerInvariant();
        if (KnownKinds.Contains(kind, StringComparer.Ordinal))
        {
            return kind;
        }

        return userAgent.Contains("Mozilla", StringComparison.OrdinalIgnoreCase) ? "browser" : "unknown";
    }

    private static string CleanOs(string value) =>
        value.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
            ? value["Microsoft ".Length..]
            : value;

    private static string FormatAddress(IPAddress? address)
    {
        if (address is null)
        {
            return string.Empty;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    private static string Limit(string? value, int max)
    {
        string trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
