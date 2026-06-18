using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace OpcBridge.Da;

/// <summary>
/// Helper for running an action under impersonated Windows credentials,
/// used for remote DCOM access in workgroup / cross-domain scenarios.
/// </summary>
public static class WindowsImpersonation
{
    // LOGON32_LOGON_NEW_CREDENTIALS — provides outbound network credentials
    // without a full interactive logon; ideal for DCOM remote activation.
    private const int LogonNewCredentials = 9;
    private const int ProviderWinNt50 = 3;

    /// <summary>
    /// Runs <paramref name="action"/>. If username is provided, runs it under
    /// the impersonated identity; otherwise runs it directly.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Run(string? username, string? password, string? domain, string fallbackDomain, Action action)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            action();
            return;
        }

        string user = username.Trim();
        string pass = password ?? string.Empty;
        string dom  = string.IsNullOrWhiteSpace(domain) ? fallbackDomain : domain.Trim();

        if (!LogonUser(user, dom, pass, LogonNewCredentials, ProviderWinNt50, out nint token))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Logon failed for '{dom}\\{user}' (Win32 error {error}). Check username, password and domain.");
        }

        try
        {
            using var identity = new WindowsIdentity(token);
            WindowsIdentity.RunImpersonated(identity.AccessToken, action);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string username, string domain, string password,
        int logonType, int logonProvider, out nint token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
