using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace OpcBridge.App;

/// <summary>
/// Protects secrets at rest for config files the app owns (the worker run-as password today).
/// On Windows values are encrypted with DPAPI (LocalMachine scope) and stored with a
/// <c>dpapi:</c> prefix; reading accepts plaintext for migration. On non-Windows platforms the
/// value passes through unchanged — the worker feature is Windows-only, and tests and Linux
/// dev runs must keep working.
/// </summary>
public static class SecretProtector
{
    public const string Prefix = "dpapi:";

    public static bool IsProtected(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value) || IsProtected(value))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            return value;
        }

        return Prefix + Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(value)));
    }

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsProtected(value))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Cannot decrypt DPAPI anywhere but Windows; a config moved between machines
            // cannot be read back (LocalMachine scope) and must be re-entered.
            return null;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(value[Prefix.Length..]);
            return Encoding.UTF8.GetString(UnprotectBytes(bytes));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectBytes(byte[] value)
        => ProtectedData.Protect(value, null, DataProtectionScope.LocalMachine);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectBytes(byte[] value)
        => ProtectedData.Unprotect(value, null, DataProtectionScope.LocalMachine);
}
