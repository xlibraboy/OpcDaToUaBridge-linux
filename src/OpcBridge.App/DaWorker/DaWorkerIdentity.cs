using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpcBridge.App;

/// <summary>Process plus redirected streams of a spawned worker, whatever identity it runs as.</summary>
internal sealed class DaWorkerChild
{
    public required Process Process { get; init; }

    public required StreamWriter StandardInput { get; init; }

    public required Stream StandardOutput { get; init; }

    public required StreamReader StandardError { get; init; }
}

/// <summary>
/// Spawns the worker process. Same identity: an ordinary <see cref="Process.Start"/> with
/// redirected streams. A different run-as account needs a real Windows logon —
/// <c>LogonUser</c> (batch) + <c>DuplicateTokenEx</c> + <c>CreateProcessAsUser</c> with
/// inherited anonymous pipes — because the account must be the worker's process token, not
/// an impersonation: the vendor DCOM stack checks the token's SID. Credentials travel on
/// stdin after start, never in argv.
/// </summary>
internal static class DaWorkerIdentity
{
    private const int LogonBatch = 4;
    private const int ProviderDefault = 0;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenAllAccess = 0xF01FF;
    private const int HandleFlagInherit = 0x00000001;
    private const int StartfUseStdHandles = 0x00000100;
    private const int CreateNoWindow = 0x08000000;

    internal static DaWorkerChild Start(string fileName, IReadOnlyList<string> arguments, DaWorkerOptions options)
    {
        bool differentAccount = !string.IsNullOrWhiteSpace(options.RunAsUser)
            && !IsCurrentIdentity(options.RunAsUser!, options.RunAsDomain);

        if (differentAccount && string.IsNullOrEmpty(options.RunAsPassword))
        {
            throw new InvalidOperationException(
                $"Worker run-as account '{options.RunAsUser}' needs a password; no worker can log on without one.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA workers require Windows.");
        }

        return differentAccount
            ? StartRunAs(fileName, arguments, options)
            : StartCurrentIdentity(fileName, arguments);
    }

    /// <summary>True when the configured run-as account is the account the bridge runs as.</summary>
    internal static bool IsCurrentIdentity(string user, string? domain)
        => string.Equals(CurrentAccountName(), NormalizeAccountName(user, domain), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Qualifies a configured account the way Windows reports it: <c>.\user</c> and a bare
    /// <c>user</c> become <c>MACHINE\user</c>; an explicit <c>DOMAIN\user</c> stays as given.
    /// </summary>
    internal static string NormalizeAccountName(string user, string? domain)
    {
        string candidate = user.Trim();
        if (candidate.StartsWith(".\\", StringComparison.Ordinal))
        {
            return Environment.MachineName + candidate[1..];
        }

        if (!candidate.Contains('\\'))
        {
            string prefix = string.IsNullOrWhiteSpace(domain) ? Environment.MachineName : domain.Trim();
            return prefix + "\\" + candidate;
        }

        return candidate;
    }

    internal static string CurrentAccountName()
        => OperatingSystem.IsWindows() ? CurrentWindowsAccountName() : Environment.UserName;

    [SupportedOSPlatform("windows")]
    private static string CurrentWindowsAccountName()
        => System.Security.Principal.WindowsIdentity.GetCurrent().Name;

    /// <summary>CommandLineToArgvW-compatible quoting for <c>CreateProcess</c> command lines.</summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static DaWorkerChild StartCurrentIdentity(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = new() { StartInfo = startInfo };
        process.Start();
        return new DaWorkerChild
        {
            Process = process,
            StandardInput = process.StandardInput,
            StandardOutput = process.StandardOutput.BaseStream,
            StandardError = process.StandardError
        };
    }

    [SupportedOSPlatform("windows")]
    private static DaWorkerChild StartRunAs(string fileName, IReadOnlyList<string> arguments, DaWorkerOptions options)
    {
        using SafeAccessTokenHandle logonToken = LogonOrThrow(options);
        if (!DuplicateTokenEx(
                logonToken,
                TokenAllAccess,
                IntPtr.Zero,
                SecurityImpersonation,
                TokenPrimary,
                out SafeAccessTokenHandle primaryToken))
        {
            throw new InvalidOperationException(
                $"DuplicateTokenEx failed for '{options.RunAsUser}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        using (primaryToken)
        {
            SafeFileHandle? childStdinRead = null;
            SafeFileHandle? parentStdinWrite = null;
            SafeFileHandle? parentStdoutRead = null;
            SafeFileHandle? childStdoutWrite = null;
            SafeFileHandle? parentStderrRead = null;
            SafeFileHandle? childStderrWrite = null;
            try
            {
                SECURITY_ATTRIBUTES inheritable = new()
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = IntPtr.Zero,
                    bInheritHandle = true
                };

                if (!CreatePipe(out childStdinRead, out parentStdinWrite, ref inheritable, 0)
                    || !CreatePipe(out parentStdoutRead, out childStdoutWrite, ref inheritable, 0)
                    || !CreatePipe(out parentStderrRead, out childStderrWrite, ref inheritable, 0))
                {
                    throw new InvalidOperationException($"CreatePipe failed (Win32 error {Marshal.GetLastWin32Error()}).");
                }

                // Only the child ends stay inheritable; leaking our ends would keep the
                // child's stdin open after we close it and the worker would never see EOF.
                if (!SetHandleInformation(parentStdinWrite, HandleFlagInherit, 0)
                    || !SetHandleInformation(parentStdoutRead, HandleFlagInherit, 0)
                    || !SetHandleInformation(parentStderrRead, HandleFlagInherit, 0))
                {
                    throw new InvalidOperationException($"SetHandleInformation failed (Win32 error {Marshal.GetLastWin32Error()}).");
                }

                STARTUPINFO startup = new()
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = childStdinRead.DangerousGetHandle(),
                    hStdOutput = childStdoutWrite.DangerousGetHandle(),
                    hStdError = childStderrWrite.DangerousGetHandle()
                };

                string commandLine = string.Join(
                    ' ',
                    new[] { fileName }.Concat(arguments).Select(QuoteArgument));

                if (!CreateProcessAsUser(
                        primaryToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        CreateNoWindow,
                        IntPtr.Zero,
                        null,
                        ref startup,
                        out PROCESS_INFORMATION processInformation))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"CreateProcessAsUser failed for '{options.RunAsUser}' (Win32 error {error}): " +
                        (error switch
                        {
                            1314 => "the bridge account lacks SeAssignPrimaryToken/SeIncreaseQuota; start the bridge as LocalSystem.",
                            _ => new Win32Exception(error).Message
                        }));
                }

                CloseHandle(processInformation.hThread);
                CloseHandle(processInformation.hProcess);
                Process process = Process.GetProcessById(processInformation.dwProcessId);

                var child = new DaWorkerChild
                {
                    Process = process,
                    StandardInput = new StreamWriter(new FileStream(parentStdinWrite, FileAccess.Write), Encoding.UTF8)
                    {
                        AutoFlush = true
                    },
                    StandardOutput = new FileStream(parentStdoutRead, FileAccess.Read),
                    StandardError = new StreamReader(new FileStream(parentStderrRead, FileAccess.Read), Encoding.UTF8)
                };

                // Ownership moved into the streams; keep the finally block from closing them.
                parentStdinWrite = null;
                parentStdoutRead = null;
                parentStderrRead = null;
                return child;
            }
            finally
            {
                childStdinRead?.Dispose();
                childStdoutWrite?.Dispose();
                childStderrWrite?.Dispose();
                parentStdinWrite?.Dispose();
                parentStdoutRead?.Dispose();
                parentStderrRead?.Dispose();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeAccessTokenHandle LogonOrThrow(DaWorkerOptions options)
    {
        string user = options.RunAsUser!.Trim();
        string? domain = string.IsNullOrWhiteSpace(options.RunAsDomain) ? null : options.RunAsDomain!.Trim();
        int separator = user.IndexOf('\\');
        if (separator >= 0)
        {
            domain = user[..separator];
            user = user[(separator + 1)..];
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = ".";
        }

        if (!LogonUser(user, domain, options.RunAsPassword ?? string.Empty, LogonBatch, ProviderDefault, out SafeAccessTokenHandle token))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Windows logon failed for '{domain}\\{user}' (Win32 error {error}): " +
                (error switch
                {
                    1326 => "wrong user name or password.",
                    1385 => "the account lacks the 'Log on as a batch job' right (secpol.msc > Local Policies > User Rights Assignment).",
                    1909 => "the account is locked out.",
                    _ => new Win32Exception(error).Message
                }));
        }

        return token;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string username, string domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        int desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        int creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SECURITY_ATTRIBUTES pipeAttributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
