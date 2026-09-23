namespace OpcBridge.App.Auth;

/// <summary>
/// A dashboard user as persisted in <c>users.json</c> (beside mappings.json).
/// The password is stored as a PBKDF2-SHA256 hash with a per-user random salt;
/// the plaintext never touches disk and never leaves the login/logout pair of
/// endpoints.
/// </summary>
public sealed record UserAccount(
    string Username,
    string DisplayName,
    UserRole Role,
    bool Enabled,
    string PasswordHash,
    string PasswordSalt,
    int PasswordIterations,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc);

/// <summary>Wire shape of a user: never carries the hash, salt or iteration count.</summary>
public sealed record UserDto(
    string Username,
    string DisplayName,
    string Role,
    bool Enabled,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc)
{
    public static UserDto FromAccount(UserAccount account) => new(
        account.Username,
        account.DisplayName,
        UserRoles.Format(account.Role),
        account.Enabled,
        account.CreatedUtc,
        account.LastLoginUtc);
}
