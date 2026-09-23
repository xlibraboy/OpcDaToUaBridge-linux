using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpcBridge.App.Auth;

/// <summary>
/// Dashboard user registry persisted to <c>users.json</c> in <see cref="DataDirectory"/>
/// (beside mappings.json / sources.json), mirroring <c>MappingStore</c>'s shape: a
/// lock-guarded in-memory list, best-effort persistence, no partial state on load.
/// Passwords are stored as PBKDF2-SHA256 hashes with a per-user random salt.
/// </summary>
public sealed class UserStore
{
    /// <summary>Iteration count for new hashes. Existing accounts keep their stored count.</summary>
    internal const int DefaultIterations = 100_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Shortest accepted password. Kept low enough for plant-floor PINs, logged in the user list.</summary>
    public const int MinPasswordLength = 4;
    public const int MaxPasswordLength = 256;
    public const int MaxUsernameLength = 64;

    /// <summary>Username/password of the account seeded on a fresh install.</summary>
    public const string DefaultAdminUsername = "admin";
    internal const string DefaultAdminPassword = "admin";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private readonly ILogger<UserStore> logger_;
    private List<UserAccount> users_;
    private bool seeded_;

    public UserStore(ILogger<UserStore> logger)
        : this(logger, DataDirectory.Combine("users.json"))
    {
    }

    /// <summary>Test seam: run against an explicit users.json path instead of <see cref="DataDirectory"/>.</summary>
    internal UserStore(ILogger<UserStore> logger, string persistPath)
    {
        logger_ = logger;
        persist_path_ = persistPath;
        users_ = LoadFromDisk() ?? new List<UserAccount>();

        if (users_.Count == 0)
        {
            // Bootstrap: an empty registry would lock everyone out of the dashboard.
            UserAccount admin = BuildAccount(
                DefaultAdminUsername,
                DefaultAdminPassword,
                UserRole.Admin,
                "Administrator");
            users_.Add(admin);
            seeded_ = true;
            Persist();
        }
    }

    /// <summary>True when this instance created the default admin (fresh install, empty users.json).</summary>
    public bool SeededDefaultAdmin => seeded_;

    /// <summary>Accounts, most privileged first, then by name. Never exposes hashes beyond the record itself.</summary>
    public IReadOnlyList<UserAccount> List()
    {
        lock (sync_)
        {
            return users_
                .OrderByDescending(user => user.Role)
                .ThenBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public UserAccount? TryGet(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        lock (sync_)
        {
            return users_.FirstOrDefault(user =>
                string.Equals(user.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Verifies credentials. Returns null for unknown, disabled, or wrong-password
    /// attempts (the caller must not distinguish them). On success stamps LastLoginUtc.
    /// </summary>
    public UserAccount? Authenticate(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        UserAccount? account;
        lock (sync_)
        {
            account = users_.FirstOrDefault(user =>
                string.Equals(user.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (account is null || !account.Enabled)
        {
            // Hash anyway on unknown users to keep the timing profile flat.
            _ = VerifyPassword(password, account?.PasswordHash, account?.PasswordSalt, account?.PasswordIterations ?? DefaultIterations);
            return null;
        }

        if (!VerifyPassword(password, account.PasswordHash, account.PasswordSalt, account.PasswordIterations))
        {
            return null;
        }

        UserAccount updated = account with { LastLoginUtc = DateTime.UtcNow };
        lock (sync_)
        {
            int index = users_.FindIndex(user =>
                string.Equals(user.Username, updated.Username, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                users_[index] = updated;
                Persist();
            }
        }

        return updated;
    }

    public (bool Ok, string? Error) Create(string? username, string? password, string? role, string? displayName)
    {
        username = (username ?? string.Empty).Trim();
        if (ValidateUsername(username) is string usernameError)
        {
            return (false, usernameError);
        }

        if (validatePassword(password) is string passwordError)
        {
            return (false, passwordError);
        }

        if (!UserRoles.TryParse(role, out UserRole parsedRole))
        {
            return (false, "Role must be Admin, Engineer, Operator or Viewer.");
        }

        string name = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();
        UserAccount account = BuildAccount(username, password!, parsedRole, name);

        lock (sync_)
        {
            if (users_.Any(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "A user with that name already exists.");
            }

            users_.Add(account);
            Persist();
        }

        return (true, null);

        static string? validatePassword(string? password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "Password is required.";
            }

            if (password.Length < MinPasswordLength)
            {
                return $"Password must be at least {MinPasswordLength} characters.";
            }

            return password.Length > MaxPasswordLength
                ? $"Password must be at most {MaxPasswordLength} characters."
                : null;
        }
    }

    public (bool Ok, string? Error) SetPassword(string username, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return (false, "Password is required.");
        }

        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            return (false, $"Password must be {MinPasswordLength}-{MaxPasswordLength} characters.");
        }

        (string hash, string salt, int iterations) = HashPassword(password);

        lock (sync_)
        {
            int index = IndexOf(username);
            if (index < 0)
            {
                return (false, "User not found.");
            }

            users_[index] = users_[index] with
            {
                PasswordHash = hash,
                PasswordSalt = salt,
                PasswordIterations = iterations
            };
            Persist();
        }

        return (true, null);
    }

    public (bool Ok, string? Error) SetRole(string username, string? role)
    {
        if (!UserRoles.TryParse(role, out UserRole parsedRole))
        {
            return (false, "Role must be Admin, Engineer, Operator or Viewer.");
        }

        lock (sync_)
        {
            int index = IndexOf(username);
            if (index < 0)
            {
                return (false, "User not found.");
            }

            UserAccount existing = users_[index];
            if (existing.Role == UserRole.Admin && parsedRole != UserRole.Admin && CountEnabledAdmins() <= 1)
            {
                return (false, "Cannot demote the last enabled administrator.");
            }

            users_[index] = existing with { Role = parsedRole };
            Persist();
        }

        return (true, null);
    }

    public (bool Ok, string? Error) SetEnabled(string username, bool enabled)
    {
        lock (sync_)
        {
            int index = IndexOf(username);
            if (index < 0)
            {
                return (false, "User not found.");
            }

            UserAccount existing = users_[index];
            if (!enabled && existing is { Role: UserRole.Admin, Enabled: true } && CountEnabledAdmins() <= 1)
            {
                return (false, "Cannot disable the last enabled administrator.");
            }

            users_[index] = existing with { Enabled = enabled };
            Persist();
        }

        return (true, null);
    }

    public (bool Ok, string? Error) SetDisplayName(string username, string? displayName)
    {
        string name = (displayName ?? string.Empty).Trim();
        if (name.Length > 128)
        {
            return (false, "Display name must be at most 128 characters.");
        }

        lock (sync_)
        {
            int index = IndexOf(username);
            if (index < 0)
            {
                return (false, "User not found.");
            }

            users_[index] = users_[index] with { DisplayName = name };
            Persist();
        }

        return (true, null);
    }

    public (bool Ok, string? Error) Delete(string username)
    {
        lock (sync_)
        {
            int index = IndexOf(username);
            if (index < 0)
            {
                return (false, "User not found.");
            }

            UserAccount existing = users_[index];
            if (existing is { Role: UserRole.Admin, Enabled: true } && CountEnabledAdmins() <= 1)
            {
                return (false, "Cannot delete the last enabled administrator.");
            }

            users_.RemoveAt(index);
            Persist();
        }

        return (true, null);
    }

    /// <summary>Username shape check: non-empty, no whitespace, bounded length. Returns null when valid.</summary>
    public static string? ValidateUsername(string? username)
    {
        string name = (username ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return "Username is required.";
        }

        if (name.Length > MaxUsernameLength)
        {
            return $"Username must be at most {MaxUsernameLength} characters.";
        }

        if (name.Any(char.IsWhiteSpace))
        {
            return "Username must not contain spaces.";
        }

        return null;
    }

    internal static (string Hash, string Salt, int Iterations) HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            DefaultIterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), DefaultIterations);
    }

    internal static bool VerifyPassword(string password, string? hashBase64, string? saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64) || iterations <= 0)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(saltBase64);
            byte[] expected = Convert.FromBase64String(hashBase64);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static UserAccount BuildAccount(string username, string password, UserRole role, string displayName)
    {
        (string hash, string salt, int iterations) = HashPassword(password);
        return new UserAccount(
            username,
            displayName,
            role,
            Enabled: true,
            hash,
            salt,
            iterations,
            DateTime.UtcNow,
            LastLoginUtc: null);
    }

    private int IndexOf(string username) => users_.FindIndex(user =>
        string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));

    private int CountEnabledAdmins() => users_.Count(user => user is { Role: UserRole.Admin, Enabled: true });

    private void Persist()
    {
        try
        {
            string json = JsonSerializer.Serialize(users_, JsonOptions);
            File.WriteAllText(persist_path_, json);
        }
        catch (Exception ex)
        {
            logger_.LogError(ex, "Could not persist users.json at {Path}; user changes are in memory only", persist_path_);
        }
    }

    private List<UserAccount>? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_))
            {
                return null;
            }

            List<UserAccount>? loaded = JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(persist_path_));
            return loaded?.Where(user => user is { Username.Length: > 0 }).ToList();
        }
        catch (Exception ex)
        {
            logger_.LogError(ex, "Could not read users.json at {Path}; starting with an empty user list", persist_path_);
            return null;
        }
    }
}
