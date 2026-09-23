using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpcBridge.App.Auth;

/// <summary>Keys the gate stores the resolved caller on, for endpoints that need to know who is acting.</summary>
public static class AuthContextKeys
{
    public const string Username = "opcbridge.auth.username";
    public const string Role = "opcbridge.auth.role";
}

/// <summary>The signed-in caller of the current request, if any.</summary>
public readonly record struct AuthCaller(string Username, UserRole Role);

/// <summary>
/// Session + role gate. Runs before every endpoint and answers 401/403 itself, so
/// enforcement lives in one testable table (<see cref="AuthPolicy"/>) instead of
/// being duplicated across ~70 endpoint registrations.
/// </summary>
public sealed class RoleGateMiddleware
{
    private readonly RequestDelegate next_;
    private readonly AuthOptions options_;
    private readonly AuthSessionStore sessions_;
    private readonly ILogger<RoleGateMiddleware> logger_;

    public RoleGateMiddleware(
        RequestDelegate next,
        IOptions<AuthOptions> options,
        AuthSessionStore sessions,
        ILogger<RoleGateMiddleware> logger)
    {
        next_ = next;
        options_ = options.Value;
        sessions_ = sessions;
        logger_ = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!options_.Enabled)
        {
            await next_(context);
            return;
        }

        string path = context.Request.Path.Value ?? "/";
        string method = context.Request.Method;

        // Public surface: health probes, the dashboard shell (it renders the login
        // form itself), and the auth endpoints. HMI paths stay open while the
        // Avalonia apps are trusted on the LAN.
        if (AuthPolicy.IsPublic(path) || (options_.TrustHmi && AuthPolicy.IsHmiPath(path)))
        {
            // A signed-in caller is still identified, so public endpoints can act on their behalf.
            if (TryResolve(context, out AuthCaller caller))
            {
                SetCaller(context, caller);
            }

            await next_(context);
            return;
        }

        if (!TryResolve(context, out AuthCaller resolved))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Sign in required.");
            return;
        }

        UserRole required = AuthPolicy.RequiredRole(method, path);
        if ((int)resolved.Role < (int)required)
        {
            logger_.LogWarning(
                "Denied {Method} {Path} for user {User} with role {Role} (requires {Required})",
                method,
                path,
                resolved.Username,
                UserRoles.Format(resolved.Role),
                UserRoles.Format(required));
            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                $"Your role ({UserRoles.Format(resolved.Role)}) does not allow this action; it requires {UserRoles.Format(required)}.");
            return;
        }

        SetCaller(context, resolved);
        await next_(context);
    }

    private bool TryResolve(HttpContext context, out AuthCaller caller)
    {
        caller = default;
        string? token = context.Request.Cookies[AuthOptions.CookieName];
        if (!sessions_.TryValidate(token, out string username, out UserRole role))
        {
            return false;
        }

        caller = new AuthCaller(username, role);
        return true;
    }

    private static void SetCaller(HttpContext context, AuthCaller caller)
    {
        context.Items[AuthContextKeys.Username] = caller.Username;
        context.Items[AuthContextKeys.Role] = caller.Role;
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }
}

public static class AuthCallerExtensions
{
    /// <summary>The caller the gate resolved, or null for anonymous requests.</summary>
    public static AuthCaller? GetAuthCaller(this HttpContext context)
    {
        if (context.Items.TryGetValue(AuthContextKeys.Username, out object? username)
            && username is string name
            && context.Items.TryGetValue(AuthContextKeys.Role, out object? role)
            && role is UserRole parsed)
        {
            return new AuthCaller(name, parsed);
        }

        return null;
    }
}
