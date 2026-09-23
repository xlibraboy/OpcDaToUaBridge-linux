using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace OpcBridge.App.Auth;

/// <summary>
/// In-memory dashboard sessions keyed by an opaque 256-bit token. Sessions are
/// deliberately not persisted: a bridge restart (or the single-instance lock
/// releasing) signs everyone out, and the token never has to be trusted offline.
/// Lookups slide the expiry so an active dashboard does not get logged out mid-shift.
/// </summary>
public sealed class AuthSessionStore
{
    private sealed record Session(string Username, UserRole Role, DateTime ExpiresUtc);

    private readonly ConcurrentDictionary<string, Session> sessions_ = new(StringComparer.Ordinal);
    private readonly TimeSpan lifetime_;

    public AuthSessionStore(IOptions<AuthOptions> options)
    {
        lifetime_ = options.Value.SessionLifetime;
    }

    public int Count => sessions_.Count;

    /// <summary>Creates a session and returns its token. The role is captured at login time.</summary>
    public string Create(string username, UserRole role)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        sessions_[token] = new Session(username, role, DateTime.UtcNow.Add(lifetime_));
        return token;
    }

    /// <summary>Validates a token and slides its expiry. Unknown/expired tokens are rejected and dropped.</summary>
    public bool TryValidate(string? token, out string username, out UserRole role)
    {
        username = string.Empty;
        role = UserRole.None;
        if (string.IsNullOrEmpty(token) || !sessions_.TryGetValue(token, out Session? session))
        {
            return false;
        }

        if (session.ExpiresUtc <= DateTime.UtcNow)
        {
            sessions_.TryRemove(token, out _);
            return false;
        }

        sessions_[token] = session with { ExpiresUtc = DateTime.UtcNow.Add(lifetime_) };
        username = session.Username;
        role = session.Role;
        return true;
    }

    public void Remove(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            sessions_.TryRemove(token, out _);
        }
    }

    /// <summary>
    /// Drops every session of a user. Called when the account is deleted, disabled,
    /// or has its role/password changed, so an open dashboard cannot keep the
    /// privileges it logged in with.
    /// </summary>
    public void RemoveUser(string username)
    {
        foreach (KeyValuePair<string, Session> entry in sessions_)
        {
            if (string.Equals(entry.Value.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                sessions_.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>Refreshes the cached role of a user's live sessions after a role change.</summary>
    public void UpdateRole(string username, UserRole role)
    {
        foreach (KeyValuePair<string, Session> entry in sessions_)
        {
            if (string.Equals(entry.Value.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                sessions_[entry.Key] = entry.Value with { Role = role };
            }
        }
    }
}
