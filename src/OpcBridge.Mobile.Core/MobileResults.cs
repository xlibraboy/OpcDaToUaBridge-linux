namespace OpcBridge.Mobile.Core;

/// <summary>
/// The result of one mobile client call: either a value or the error the UI should show.
/// The clients never throw for an unreachable bridge — the app is expected to be driven on a
/// plant Wi-Fi, so a failed call renders as a message instead of an exception.
/// </summary>
public readonly record struct MobileResult<T>(bool Ok, T? Value, string? Error, int StatusCode)
{
    public static MobileResult<T> Success(T value) => new(true, value, null, 200);

    /// <summary>A failed call; <paramref name="statusCode"/> is 0 when the bridge never answered.</summary>
    public static MobileResult<T> Fail(string error, int statusCode = 0) => new(false, default, error, statusCode);

    /// <summary>True when the bridge answered but wants a session first.</summary>
    public bool IsSignInRequired => StatusCode is 401 or 403;
}

/// <summary>The signed-in session, as the mobile app's settings screen shows it.</summary>
public sealed record MobileSession(
    bool Authenticated,
    string Username,
    string DisplayName,
    string Role,
    bool AuthEnabled)
{
    public bool IsOperator => RoleRank(Role) >= RoleRank("Operator");

    /// <summary>Engineer or Admin: the control users that may provision the phone and edit logic.</summary>
    public bool IsControlUser => RoleRank(Role) >= RoleRank("Engineer");

    private static int RoleRank(string? role) => role switch
    {
        "Admin" => 3,
        "Engineer" => 2,
        "Operator" => 1,
        _ => 0
    };
}

/// <summary>
/// The viewer's access rule: the Logic, Elements and Settings screens open for a signed-in
/// bridge account, or for the read-only offline view the operator starts from Sign in when the
/// bridge is unreachable. A bridge that runs with <c>Auth:Enabled=false</c> still has accounts
/// in <c>users.json</c>, and the app asks for one there too — online access is never anonymous,
/// and the offline view ends the moment the bridge answers again.
/// </summary>
public static class MobileAccess
{
    public static bool IsGranted(BridgeConnection? bridge) =>
        bridge is not null
        && (bridge.Session is { Authenticated: true }
            || (bridge.IsOfflineViewer && bridge.IsOffline));
}

/// <summary>Constants shared by the mobile client surfaces.</summary>
public static class MobileBridge
{
    /// <summary>The shared tag cache key of one saved bridge (the phone keeps several).</summary>
    public static string CacheKey(string bridgeId) => "mobile:" + bridgeId;
}
