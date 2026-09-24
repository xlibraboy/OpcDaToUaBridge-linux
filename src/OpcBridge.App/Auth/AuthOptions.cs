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
    /// Sign a dashboard session out after this many minutes without a request
    /// (clamped to 1 minute – 24 hours); 0 disables the idle check and leaves only
    /// <see cref="SessionLifetime"/>. The dashboard enforces the same window itself,
    /// because its 1-second poll would otherwise count as activity forever on a
    /// workstation nobody is using.
    /// </summary>
    public int IdleMinutes { get; set; } = 30;

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

    /// <summary>Effective idle window, or <see cref="TimeSpan.Zero"/> when idle sign-out is disabled.</summary>
    public TimeSpan IdleTimeout =>
        IdleMinutes > 0 ? TimeSpan.FromMinutes(Math.Clamp(IdleMinutes, 1, 1440)) : TimeSpan.Zero;
}
