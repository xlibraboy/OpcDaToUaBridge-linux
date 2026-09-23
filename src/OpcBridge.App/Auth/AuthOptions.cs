namespace OpcBridge.App.Auth;

/// <summary>
/// Dashboard authentication settings, bound from the <c>Auth</c> section of
/// appsettings.json. Authentication is opt-in: with the section absent (or
/// <see cref="Enabled"/> false) the bridge behaves exactly as before and every
/// endpoint is reachable without a login.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Master switch. When false no endpoint requires a session.</summary>
    public bool Enabled { get; set; }

    /// <summary>Sliding session lifetime in hours (clamped 1–168).</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>
    /// Trust the Avalonia HMI apps on the LAN: leave the endpoints they call
    /// (/hmi hub, /api/hmi/*, /api/values) reachable without a dashboard login.
    /// Set false to require a session for them too (the HMI apps would then need
    /// credentials, which they do not have today).
    /// </summary>
    public bool TrustHmi { get; set; } = true;

    /// <summary>Cookie carrying the session token. HttpOnly; the token is opaque.</summary>
    public const string CookieName = "opcbridge_session";

    public TimeSpan SessionLifetime => TimeSpan.FromHours(Math.Clamp(SessionHours, 1, 168));
}
