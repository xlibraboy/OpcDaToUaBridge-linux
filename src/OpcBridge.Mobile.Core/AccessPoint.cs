using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpcBridge.Mobile.Core;

/// <summary>How the bound plant network authenticates.</summary>
public enum AccessPointSecurity
{
    Open,
    Wpa2,
    Wpa3,
    Wpa2Enterprise,
    Wep
}

/// <summary>Why the online gate is open or closed.</summary>
public enum AccessPointGateState
{
    NotBound,
    PermissionRequired,
    Unverifiable,
    Disconnected,
    ConnectedToBound,
    ConnectedToOther
}

/// <summary>The access point the phone is allowed to use for live data.</summary>
public sealed record SavedAccessPoint(string Ssid, string Bssid, AccessPointSecurity Security)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Ssid) ? AccessPointCodec.NormalizeBssid(Bssid) : Ssid;

    public string BssidText => AccessPointCodec.NormalizeBssid(Bssid);

    public string SecurityText => AccessPointCodec.SecurityText(Security);
}

/// <summary>One access point the platform's scan found; each BSSID is its own entry.</summary>
public sealed record NearbyAccessPoint(string Ssid, string Bssid, int SignalLevel, AccessPointSecurity Security)
{
    public string BssidText => AccessPointCodec.NormalizeBssid(Bssid);

    public string SignalText => SignalLevel < -128 ? "—" : SignalLevel + " dBm";

    public string SecurityText => AccessPointCodec.SecurityText(Security);
}

/// <summary>
/// What the phone's Wi-Fi is doing now. <see cref="IdentityVisible"/> is false when Android
/// hides the SSID/BSSID (location access refused or turned off), which also keeps the gate
/// closed: the app cannot prove it is on the bound access point.
/// </summary>
public sealed record AccessPointConnection(
    bool WifiEnabled,
    bool PermissionMissing,
    bool Connected,
    bool IdentityVisible,
    string Ssid,
    string Bssid)
{
    public static AccessPointConnection Unknown { get; } = new(false, false, false, false, string.Empty, string.Empty);
}

/// <summary>The gate's verdict, with the words the screens show.</summary>
public sealed record AccessPointGateStatus(
    AccessPointGateState State,
    SavedAccessPoint? Bound,
    AccessPointConnection Current)
{
    public bool OnlineAllowed => State == AccessPointGateState.ConnectedToBound;

    public string StateText => State switch
    {
        AccessPointGateState.ConnectedToBound => "on bound AP",
        AccessPointGateState.ConnectedToOther => "other network",
        AccessPointGateState.PermissionRequired => "permission needed",
        AccessPointGateState.Unverifiable => "identity hidden",
        AccessPointGateState.NotBound => "not provisioned",
        _ => "offline"
    };

    public string DetailText => State switch
    {
        AccessPointGateState.NotBound =>
            "No plant access point is provisioned on this phone — an Engineer or Admin bridge account must assign it.",
        AccessPointGateState.PermissionRequired =>
            "Allow nearby Wi-Fi and location access so the app can verify the plant access point.",
        AccessPointGateState.Unverifiable =>
            "The phone's Wi-Fi identity is hidden — allow location access to verify the bound BSSID.",
        AccessPointGateState.Disconnected when !Current.WifiEnabled =>
            "Wi-Fi is off — turn it on and connect to " + (Bound?.DisplayName ?? "the plant AP") + ".",
        AccessPointGateState.Disconnected =>
            "Not connected to " + (Bound?.DisplayName ?? "the plant access point") + ".",
        AccessPointGateState.ConnectedToOther =>
            (string.IsNullOrWhiteSpace(Current.Ssid) ? "Another Wi-Fi network" : "Phone is on " + Current.Ssid)
            + " — not the bound access point.",
        _ => "Connected to " + (Bound?.DisplayName ?? "the plant access point") + " · " + (Bound?.BssidText ?? string.Empty)
    };
}

/// <summary>
/// The platform's Wi-Fi surface: the current association, a scan, and an app-scoped
/// connection to one BSSID. Android implements this with WifiNetworkSpecifier; tests use a fake.
/// </summary>
public interface IAccessPointNetwork
{
    event Action? Changed;

    AccessPointConnection Current { get; }

    void Refresh();

    Task<MobileResult<IReadOnlyList<NearbyAccessPoint>>> ScanAsync(CancellationToken cancellationToken);

    Task<MobileResult<AccessPointConnection>> ConnectAsync(
        SavedAccessPoint accessPoint,
        string? password,
        CancellationToken cancellationToken);

    Task DisconnectAsync();
}

/// <summary>Stores the Wi-Fi passphrase outside the ordinary preferences (Android SecureStorage).</summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);
}

/// <summary>The secret store used by tests and by a platform without secure storage.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> values_ = new(StringComparer.Ordinal);

    public string? Get(string key) => values_.TryGetValue(key, out string? value) ? value : null;

    public void Set(string key, string value) => values_[key] = value;

    public void Remove(string key) => values_.Remove(key);
}

/// <summary>Reading and writing the one bound access point, and its BSSID rules.</summary>
public static class AccessPointCodec
{
    public const string BindingKey = "opcbridge.logic.accessPoint";

    private const string SecretPrefix = "opcbridge.logic.accessPoint.password.";

    private static readonly Regex BssidPattern = new(
        "^([0-9A-F]{2}:){5}[0-9A-F]{2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(SavedAccessPoint accessPoint) =>
        JsonSerializer.Serialize(accessPoint with
        {
            Ssid = (accessPoint.Ssid ?? string.Empty).Trim(),
            Bssid = NormalizeBssid(accessPoint.Bssid)
        }, JsonOptions);

    public static SavedAccessPoint? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            SavedAccessPoint? accessPoint = JsonSerializer.Deserialize<SavedAccessPoint>(json, JsonOptions);
            if (accessPoint is null ||
                string.IsNullOrWhiteSpace(accessPoint.Ssid) ||
                !IsValidBssid(accessPoint.Bssid))
            {
                return null;
            }

            return accessPoint with
            {
                Ssid = accessPoint.Ssid.Trim(),
                Bssid = NormalizeBssid(accessPoint.Bssid)
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SecretKey(string bssid) =>
        SecretPrefix + NormalizeBssid(bssid).Replace(":", string.Empty);

    public static string NormalizeBssid(string? bssid) =>
        (bssid ?? string.Empty).Trim().Replace('-', ':').ToUpperInvariant();

    public static bool IsValidBssid(string? bssid) => BssidPattern.IsMatch(NormalizeBssid(bssid));

    public static bool SameBssid(string? left, string? right) =>
        IsValidBssid(left)
        && IsValidBssid(right)
        && string.Equals(NormalizeBssid(left), NormalizeBssid(right), StringComparison.Ordinal);

    public static string SecurityText(AccessPointSecurity security) => security switch
    {
        AccessPointSecurity.Open => "OPEN",
        AccessPointSecurity.Wpa3 => "WPA3",
        AccessPointSecurity.Wpa2Enterprise => "WPA2 · ENTERPRISE",
        AccessPointSecurity.Wep => "WEP",
        _ => "WPA2"
    };
}

/// <summary>
/// The one rule that decides whether the app may touch the network: a plant access point is
/// bound and the phone is currently on that exact BSSID. Every live call asks this gate, so a
/// phone that joined another network cannot read or write a bridge even if a stale session or
/// saved address is still on it.
/// </summary>
public sealed class AccessPointGate : IDisposable
{
    public const string BlockedText = "not on the plant access point";

    private readonly object sync_ = new();
    private readonly IBridgeSettings settings_;
    private readonly ISecretStore secrets_;
    private readonly IAccessPointNetwork network_;
    private SavedAccessPoint? binding_;
    private bool loaded_;
    private bool rebinding_;

    public AccessPointGate(IBridgeSettings settings, ISecretStore secrets, IAccessPointNetwork network)
    {
        settings_ = settings;
        secrets_ = secrets;
        network_ = network;
    }

    /// <summary>Raised when the binding or the phone's association changes.</summary>
    public event Action? Changed;

    public SavedAccessPoint? Binding
    {
        get
        {
            lock (sync_)
            {
                return binding_;
            }
        }
    }

    public AccessPointGateStatus Status
    {
        get
        {
            SavedAccessPoint? bound = Binding;
            AccessPointConnection current = network_.Current;
            if (bound is null)
            {
                return new AccessPointGateStatus(AccessPointGateState.NotBound, null, current);
            }

            if (current.PermissionMissing)
            {
                return new AccessPointGateStatus(AccessPointGateState.PermissionRequired, bound, current);
            }

            if (!current.Connected)
            {
                return new AccessPointGateStatus(AccessPointGateState.Disconnected, bound, current);
            }

            if (!current.IdentityVisible)
            {
                return new AccessPointGateStatus(AccessPointGateState.Unverifiable, bound, current);
            }

            return AccessPointCodec.SameBssid(current.Bssid, bound.Bssid)
                ? new AccessPointGateStatus(AccessPointGateState.ConnectedToBound, bound, current)
                : new AccessPointGateStatus(AccessPointGateState.ConnectedToOther, bound, current);
        }
    }

    public bool IsOnlineAllowed => Status.OnlineAllowed;

    /// <summary>
    /// True when the bound AP has what it needs to join: an open network, or a passphrase
    /// already supplied by the administrator.
    /// </summary>
    public bool HasStoredPassword(SavedAccessPoint? accessPoint = null)
    {
        SavedAccessPoint? target = accessPoint ?? Binding;
        return target is not null
            && (target.Security == AccessPointSecurity.Open
                || !string.IsNullOrEmpty(secrets_.Get(AccessPointCodec.SecretKey(target.Bssid))));
    }

    public void Load()
    {
        if (loaded_)
        {
            return;
        }

        loaded_ = true;
        SavedAccessPoint? saved = AccessPointCodec.Deserialize(settings_.Get(AccessPointCodec.BindingKey, string.Empty));
        lock (sync_)
        {
            binding_ = saved;
        }

        network_.Changed += OnNetworkChanged;
        network_.Refresh();
        RaiseChanged();
        if (saved is not null && Status.OnlineAllowed)
        {
            _ = BindCurrentNetworkAsync(saved);
        }
    }

    /// <summary>
    /// Saves the binding before trying to join it, so a failed password or an out-of-range AP
    /// leaves an address the operator can retry or replace. The passphrase goes to the secret
    /// store, never to the ordinary settings JSON.
    /// </summary>
    public async Task<MobileResult<AccessPointGateStatus>> BindAsync(
        SavedAccessPoint accessPoint,
        string? password,
        CancellationToken cancellationToken)
    {
        string ssid = (accessPoint.Ssid ?? string.Empty).Trim();
        string bssid = AccessPointCodec.NormalizeBssid(accessPoint.Bssid);
        if (ssid.Length == 0)
        {
            return MobileResult<AccessPointGateStatus>.Fail("choose an access point first");
        }

        if (!AccessPointCodec.IsValidBssid(bssid))
        {
            return MobileResult<AccessPointGateStatus>.Fail("the access point has no valid BSSID");
        }

        SavedAccessPoint normalized = new(ssid, bssid, accessPoint.Security);
        SavedAccessPoint? previous;
        lock (sync_)
        {
            previous = binding_;
            binding_ = normalized;
        }

        if (previous is not null && !AccessPointCodec.SameBssid(previous.Bssid, normalized.Bssid))
        {
            secrets_.Remove(AccessPointCodec.SecretKey(previous.Bssid));
        }

        settings_.Set(AccessPointCodec.BindingKey, AccessPointCodec.Serialize(normalized));
        if (normalized.Security == AccessPointSecurity.Open)
        {
            secrets_.Remove(AccessPointCodec.SecretKey(normalized.Bssid));
        }
        else if (!string.IsNullOrEmpty(password))
        {
            secrets_.Set(AccessPointCodec.SecretKey(normalized.Bssid), password);
        }

        RaiseChanged();
        network_.Refresh();
        MobileResult<AccessPointConnection> connection = await network_.ConnectAsync(
            normalized,
            string.IsNullOrEmpty(password)
                ? secrets_.Get(AccessPointCodec.SecretKey(normalized.Bssid))
                : password,
            cancellationToken);
        RaiseChanged();
        if (!connection.Ok)
        {
            return MobileResult<AccessPointGateStatus>.Fail(
                connection.Error ?? "could not join the access point",
                connection.StatusCode);
        }

        return Status.OnlineAllowed
            ? MobileResult<AccessPointGateStatus>.Success(Status)
            : MobileResult<AccessPointGateStatus>.Fail(
                "the phone joined " + (string.IsNullOrWhiteSpace(Status.Current.Ssid) ? "a network" : Status.Current.Ssid)
                + ", not the bound BSSID");
    }

    /// <summary>Joins the access point already bound on this phone.</summary>
    public async Task<MobileResult<AccessPointGateStatus>> ConnectAsync(CancellationToken cancellationToken)
    {
        SavedAccessPoint? bound = Binding;
        if (bound is null)
        {
            return MobileResult<AccessPointGateStatus>.Fail("bind a plant access point first");
        }

        network_.Refresh();
        MobileResult<AccessPointConnection> connection = await network_.ConnectAsync(
            bound,
            secrets_.Get(AccessPointCodec.SecretKey(bound.Bssid)),
            cancellationToken);
        RaiseChanged();
        if (!connection.Ok)
        {
            return MobileResult<AccessPointGateStatus>.Fail(
                connection.Error ?? "could not join the access point",
                connection.StatusCode);
        }

        return Status.OnlineAllowed
            ? MobileResult<AccessPointGateStatus>.Success(Status)
            : MobileResult<AccessPointGateStatus>.Fail(
                "the phone joined "
                + (string.IsNullOrWhiteSpace(Status.Current.Ssid) ? "a network" : Status.Current.Ssid)
                + ", not the bound BSSID");
    }

    /// <summary>Forgets the binding and its passphrase; live access stays off until a new one is bound.</summary>
    public async Task ForgetAsync()
    {
        SavedAccessPoint? previous;
        lock (sync_)
        {
            previous = binding_;
            binding_ = null;
        }

        settings_.Set(AccessPointCodec.BindingKey, string.Empty);
        if (previous is not null)
        {
            secrets_.Remove(AccessPointCodec.SecretKey(previous.Bssid));
        }

        await network_.DisconnectAsync();
        RaiseChanged();
    }

    public void Refresh() => network_.Refresh();

    public Task<MobileResult<IReadOnlyList<NearbyAccessPoint>>> ScanAsync(CancellationToken cancellationToken) =>
        network_.ScanAsync(cancellationToken);

    public void Dispose()
    {
        network_.Changed -= OnNetworkChanged;
    }

    private void OnNetworkChanged()
    {
        if (Status.OnlineAllowed && Binding is { } bound)
        {
            _ = BindCurrentNetworkAsync(bound);
        }

        RaiseChanged();
    }

    /// <summary>
    /// Pins the app's sockets to the Wi-Fi network it is already associated with. Without this
    /// a local-only plant AP can lose to cellular as Android's default route even though the
    /// BSSID matches.
    /// </summary>
    private async Task BindCurrentNetworkAsync(SavedAccessPoint bound)
    {
        if (rebinding_)
        {
            return;
        }

        rebinding_ = true;
        try
        {
            await network_.ConnectAsync(
                bound,
                secrets_.Get(AccessPointCodec.SecretKey(bound.Bssid)),
                CancellationToken.None);
        }
        catch (Exception)
        {
            // The next association change retries; the gate stays closed until the BSSID matches.
        }
        finally
        {
            rebinding_ = false;
        }
    }

    private void RaiseChanged() => Changed?.Invoke();
}
