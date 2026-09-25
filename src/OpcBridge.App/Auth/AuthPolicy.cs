namespace OpcBridge.App.Auth;

/// <summary>
/// The role policy table: which paths are reachable without a session and the
/// minimum role each endpoint needs. Kept as a pure static class (no ASP.NET
/// types) so the table is unit-testable without starting the host.
///
/// Defaults: reads need Viewer, any mutation needs Engineer, and named endpoints
/// override that (Operator may write tag values; Admin manages users).
/// </summary>
public static class AuthPolicy
{
    /// <summary>Never behind a session: health probes, the dashboard shell, and the auth endpoints themselves.</summary>
    private static readonly string[] PublicPaths =
    {
        "/health",
        "/",
        "/favicon.ico",
        "/api/auth/login",
        "/api/auth/logout",
        "/api/auth/me",
        "/api/app-info",
        "/api/version"
    };

    /// <summary>
    /// Endpoints the Avalonia HMI apps call. They hold no credentials, so with
    /// <see cref="AuthOptions.TrustHmi"/> (the default) these stay open on the LAN.
    /// </summary>
    private static readonly string[] HmiPaths =
    {
        "/hmi",
        "/api/values",
        "/api/hmi/tags",
        "/api/hmi/trends",
        "/api/hmi/displays",
        "/api/hmi/write"
    };

    /// <summary>Minimum role per endpoint, checked before the method default. Null method = any method.</summary>
    private static readonly (string Path, string? Method, UserRole Role)[] Overrides =
    {
        ("/api/auth/users", null, UserRole.Admin),
        ("/api/session/resolve", "POST", UserRole.Admin),
        ("/api/hmi/write", "POST", UserRole.Operator),
        ("/api/hmi/displays", "PUT", UserRole.Engineer),
        ("/api/hmi/displays", "DELETE", UserRole.Engineer)
    };

    public static bool IsPublic(string path) => MatchesAny(path, PublicPaths);

    public static bool IsHmiPath(string path) => MatchesAny(path, HmiPaths);

    /// <summary>
    /// Minimum role for a request. Callers must already have established that the
    /// path is neither public nor (with TrustHmi) an HMI path.
    /// </summary>
    public static UserRole RequiredRole(string method, string path)
    {
        foreach ((string overridePath, string? overrideMethod, UserRole role) in Overrides)
        {
            // Segment-aware: "/api/auth/users" also covers its sub-resources
            // (/api/auth/users/remove, /api/auth/users/password, ...).
            if (IsMatch(path, overridePath)
                && (overrideMethod is null || string.Equals(method, overrideMethod, StringComparison.OrdinalIgnoreCase)))
            {
                return role;
            }
        }

        // Reads are open to any signed-in user; changing anything is engineering work.
        return string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            ? UserRole.Viewer
            : UserRole.Engineer;
    }

    private static bool MatchesAny(string path, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (IsMatch(path, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Segment-aware path match: the candidate must be the whole path or be
    /// followed by a '/' — so "/api/hmi/tags" matches "/api/hmi/tags" and
    /// "/api/hmi/displays/foo", but never "/api/hmi/tagsFoo".
    /// </summary>
    private static bool IsMatch(string path, string candidate)
    {
        if (!path.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Length == candidate.Length)
        {
            return true;
        }

        // Trailing separators are their own sub-path; anything else must be one.
        return path[candidate.Length] == '/';
    }
}
