using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpcBridge.App.Auth;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record CreateUserRequest(string? Username, string? Password, string? Role, string? DisplayName);
public sealed record UpdateUserRequest(string? Username, string? Role, string? DisplayName, bool? Enabled);
public sealed record ResetPasswordRequest(string? Username, string? NewPassword);
public sealed record RemoveUserRequest(string? Username);

/// <summary>
/// Authentication and user administration. Login is the only endpoint that turns
/// credentials into a session cookie; everything else reads the caller the
/// <see cref="RoleGateMiddleware"/> resolved.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/api/auth/login", (LoginRequest request, HttpContext context, UserStore users,
            AuthSessionStore sessions, LoginThrottle throttle, IOptions<AuthOptions> options,
            ILogger<AuthOptions> logger) =>
        {
            string key = LoginThrottle.BuildKey(request.Username, context.Connection.RemoteIpAddress?.ToString());
            if (throttle.IsBlocked(key, out int retryAfterSeconds))
            {
                return Results.Json(
                    new { error = $"Too many failed sign-in attempts. Try again in {retryAfterSeconds}s." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            UserAccount? account = users.Authenticate(request.Username, request.Password);
            if (account is null)
            {
                throttle.RecordFailure(key);
                logger.LogWarning(
                    "Failed dashboard sign-in for {User} from {Client}",
                    request.Username ?? "(none)",
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return Results.Json(
                    new { error = "Invalid username or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Reset(key);
            string token = sessions.Create(account.Username, account.Role);
            AppendSessionCookie(context, token, options.Value.SessionLifetime);
            logger.LogInformation("Dashboard sign-in: {User} ({Role})", account.Username, UserRoles.Format(account.Role));

            return Results.Json(new
            {
                authenticated = true,
                username = account.Username,
                displayName = account.DisplayName,
                role = UserRoles.Format(account.Role),
                authEnabled = options.Value.Enabled
            });
        });

        app.MapPost("/api/auth/logout", (HttpContext context, AuthSessionStore sessions) =>
        {
            sessions.Remove(context.Request.Cookies[AuthOptions.CookieName]);
            context.Response.Cookies.Delete(AuthOptions.CookieName);
            return Results.Json(new { authenticated = false });
        });

        // Public: the dashboard asks this on load to decide between the shell and the login form.
        app.MapGet("/api/auth/me", (HttpContext context, UserStore users, IOptions<AuthOptions> options) =>
        {
            AuthCaller? caller = context.GetAuthCaller();
            if (caller is null)
            {
                return Results.Json(new { authenticated = false, authEnabled = options.Value.Enabled });
            }

            UserAccount? account = users.TryGet(caller.Value.Username);
            return Results.Json(new
            {
                authenticated = true,
                username = caller.Value.Username,
                displayName = account?.DisplayName ?? caller.Value.Username,
                role = UserRoles.Format(caller.Value.Role),
                authEnabled = options.Value.Enabled
            });
        });

        // Any signed-in user changes their own password; the caller stays signed in.
        app.MapPost("/api/auth/password", (ChangePasswordRequest request, HttpContext context,
            UserStore users, AuthSessionStore sessions) =>
        {
            AuthCaller? caller = context.GetAuthCaller();
            if (caller is null)
            {
                return Results.Json(new { error = "Sign in required." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (users.Authenticate(caller.Value.Username, request.CurrentPassword) is null)
            {
                return Results.BadRequest(new { error = "Current password is incorrect." });
            }

            (bool ok, string? error) = users.SetPassword(caller.Value.Username, request.NewPassword);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            // Rotate: every other session of this user dies, the current one is re-issued.
            sessions.RemoveUser(caller.Value.Username);
            context.Response.Cookies.Append(AuthOptions.CookieName, sessions.Create(caller.Value.Username, caller.Value.Role));
            return Results.Json(new { ok = true });
        });

        // ---- Admin: user management (the gate requires UserRole.Admin for /api/auth/users*) ----

        app.MapGet("/api/auth/users", (UserStore users, HttpContext context) => Results.Json(new
        {
            users = users.List().Select(UserDto.FromAccount),
            roles = UserRoles.Assignable.Select(UserRoles.Format),
            minPasswordLength = UserStore.MinPasswordLength,
            me = context.GetAuthCaller()?.Username
        }));

        app.MapPost("/api/auth/users", (CreateUserRequest request, UserStore users) =>
        {
            (bool ok, string? error) = users.Create(request.Username, request.Password, request.Role, request.DisplayName);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            UserAccount? created = users.TryGet(request.Username);
            return created is null
                ? Results.BadRequest(new { error = "User was not created." })
                : Results.Json(new { user = UserDto.FromAccount(created) });
        });

        app.MapPost("/api/auth/users/update", (UpdateUserRequest request, UserStore users, AuthSessionStore sessions) =>
        {
            if (users.TryGet(request.Username) is null)
            {
                return Results.BadRequest(new { error = "User not found." });
            }

            if (request.DisplayName is not null)
            {
                (bool nameOk, string? nameError) = users.SetDisplayName(request.Username!, request.DisplayName);
                if (!nameOk)
                {
                    return Results.BadRequest(new { error = nameError });
                }
            }

            bool roleChanged = false;
            if (request.Role is not null)
            {
                (bool roleOk, string? roleError) = users.SetRole(request.Username!, request.Role);
                if (!roleOk)
                {
                    return Results.BadRequest(new { error = roleError });
                }

                roleChanged = true;
            }

            if (request.Enabled is bool enabled)
            {
                (bool enabledOk, string? enabledError) = users.SetEnabled(request.Username!, enabled);
                if (!enabledOk)
                {
                    return Results.BadRequest(new { error = enabledError });
                }

                if (!enabled)
                {
                    sessions.RemoveUser(request.Username!);
                }
            }
            else if (roleChanged)
            {
                UserAccount? account = users.TryGet(request.Username);
                if (account is not null)
                {
                    sessions.UpdateRole(account.Username, account.Role);
                }
            }

            UserAccount? updated = users.TryGet(request.Username);
            return updated is null
                ? Results.BadRequest(new { error = "User not found." })
                : Results.Json(new { user = UserDto.FromAccount(updated) });
        });

        app.MapPost("/api/auth/users/password", (ResetPasswordRequest request, HttpContext context,
            UserStore users, AuthSessionStore sessions) =>
        {
            UserAccount? account = users.TryGet(request.Username);
            if (account is null)
            {
                return Results.BadRequest(new { error = "User not found." });
            }

            (bool ok, string? error) = users.SetPassword(account.Username, request.NewPassword);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            sessions.RemoveUser(account.Username);
            bool targetIsCaller = string.Equals(
                context.GetAuthCaller()?.Username,
                account.Username,
                StringComparison.OrdinalIgnoreCase);
            if (targetIsCaller)
            {
                // Changing your own password from the user list should not sign you out.
                context.Response.Cookies.Append(AuthOptions.CookieName, sessions.Create(account.Username, account.Role));
            }

            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/auth/users/remove", (RemoveUserRequest request, HttpContext context,
            UserStore users, AuthSessionStore sessions) =>
        {
            string? caller = context.GetAuthCaller()?.Username;
            if (string.Equals(caller, request.Username, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "You cannot delete your own account." });
            }

            (bool ok, string? error) = users.Delete(request.Username!);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            sessions.RemoveUser(request.Username!);
            return Results.Json(new { ok = true });
        });
    }

    /// <summary>
    /// Session cookie. Not marked Secure on purpose: the dashboard is served over plain
    /// http on a plant LAN, and a Secure cookie would silently never be sent back.
    /// SameSite=Strict + HttpOnly keeps it out of cross-site requests and scripts.
    /// </summary>
    private static void AppendSessionCookie(HttpContext context, string token, TimeSpan lifetime) =>
        context.Response.Cookies.Append(AuthOptions.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Path = "/",
            MaxAge = lifetime
        });
}
