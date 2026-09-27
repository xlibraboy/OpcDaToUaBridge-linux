using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace OpcBridge.App;

/// <summary>
/// Reads and updates the Windows Firewall rules that open the bridge's ports.
/// <para>
/// Rule names mirror the installer's (<c>packaging/msi/OpcBridge.wxs</c>, components
/// <c>FwDashboard</c> / <c>FwOpcUa</c>), so a rule the installer created is updated in place
/// instead of duplicated — which also keeps MSI uninstall cleanup matching. The bridge
/// auto-assigns a free port when a default is taken, and the installer's rules stay on the
/// build-time ports, so without this sync a rolled port leaves the bridge unreachable from
/// the LAN.
/// </para>
/// <para>
/// PowerShell's NetFirewall cmdlets are used rather than <c>netsh</c>: netsh prints
/// localized labels, while the CIM cmdlet and property names are stable across OS
/// languages. Changing the firewall needs the rights the MSI's service account
/// (LocalSystem) has; an interactive/debug run of the bridge may not.
/// </para>
/// </summary>
public static class WindowsFirewall
{
    public const string DashboardRuleName = "OpcBridge Dashboard";
    public const string UaRuleName = "OpcBridge OPC UA";

    public const string DashboardRuleDescription = "OpcBridge web dashboard and API";
    public const string UaRuleDescription = "OpcBridge OPC UA server endpoint";

    /// <summary>Long enough for a busy host, short enough not to hang the card.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// One rule's state. <see cref="Port"/> is the single TCP port the rule covers, null when
    /// it covers any port or a set of ports (which reads as "does not match" to callers).
    /// <see cref="Error"/> carries why the state could not be read, when it could not.
    /// </summary>
    public sealed record RuleStatus(string Name, bool Exists, bool Enabled, int? Port, bool AnyPort, string? Error = null);

    public sealed record ApplyResult(string Name, int Port, string Action, bool Ok, string? Error);

    [SupportedOSPlatform("windows")]
    public static async Task<RuleStatus> GetRuleAsync(string ruleName, CancellationToken cancellationToken)
    {
        string script =
            "$ErrorActionPreference = 'Stop'\n" +
            $"$r = Get-NetFirewallRule -DisplayName {Quote(ruleName)} -ErrorAction SilentlyContinue\n" +
            "if ($null -eq $r) { 'missing'; exit 0 }\n" +
            "$p = @($r | Get-NetFirewallPortFilter | Select-Object -First 1 -ExpandProperty LocalPort)\n" +
            "\"$($r.Enabled)|$($p -join ',')\"\n";

        (int exitCode, string stdOut, string stdErr) = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return new RuleStatus(ruleName, false, false, null, false, DescribeFailure(stdOut, stdErr));
        }

        string line = LastLine(stdOut);
        if (line.Length == 0)
        {
            return new RuleStatus(ruleName, false, false, null, false, "PowerShell returned no rule state.");
        }

        if (string.Equals(line, "missing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(line, "missing\r", StringComparison.OrdinalIgnoreCase))
        {
            return new RuleStatus(ruleName, false, false, null, false);
        }

        string[] parts = line.Split('|', 2);
        bool enabled = parts[0].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        string portToken = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        bool anyPort = portToken.Equals("Any", StringComparison.OrdinalIgnoreCase);
        int? port = !anyPort
            && int.TryParse(portToken, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;

        return new RuleStatus(ruleName, true, enabled, port, anyPort);
    }

    /// <summary>
    /// Points the named rule at <paramref name="port"/>, creating the rule when it does not
    /// exist. An existing rule is changed in place (<c>Set-</c>), not recreated, so its
    /// identity — and the installer's bookkeeping of it — survives.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<ApplyResult> ApplyRuleAsync(string ruleName, string description, int port, CancellationToken cancellationToken)
    {
        string script =
            "$ErrorActionPreference = 'Stop'\n" +
            $"$r = Get-NetFirewallRule -DisplayName {Quote(ruleName)} -ErrorAction SilentlyContinue\n" +
            "if ($null -eq $r) {\n" +
            $"  New-NetFirewallRule -DisplayName {Quote(ruleName)} -Description {Quote(description)} -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} -Profile Any | Out-Null\n" +
            "  'created'\n" +
            "} else {\n" +
            $"  Set-NetFirewallRule -DisplayName {Quote(ruleName)} -LocalPort {port} | Out-Null\n" +
            "  'updated'\n" +
            "}\n";

        (int exitCode, string stdOut, string stdErr) = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return new ApplyResult(ruleName, port, string.Empty, false, DescribeFailure(stdOut, stdErr));
        }

        string action = LastLine(stdOut).Trim();
        return new ApplyResult(ruleName, port, action.Length > 0 ? action : "applied", true, null);
    }

    /// <summary>Single-quote a value for a PowerShell command line (rule names are constants, this is belt-and-braces).</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string LastLine(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? string.Empty : lines[^1];
    }

    private static string DescribeFailure(string stdOut, string stdErr)
    {
        string message = stdErr.Trim();
        if (message.Length == 0)
        {
            message = stdOut.Trim();
        }

        if (message.Length == 0)
        {
            message = "PowerShell failed with no output.";
        }

        return message.Length > 400 ? message[..400] : message;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using Process process = new() { StartInfo = startInfo };
        process.Start();

        Task<string> stdOut = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stdErr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return (process.ExitCode, await stdOut.ConfigureAwait(false), await stdErr.ConfigureAwait(false));
    }
}
