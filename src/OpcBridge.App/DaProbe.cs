using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using OpcBridge.Da;

namespace OpcBridge.App;

/// <summary>Probe request crossing the process boundary as JSON on stdin (credentials never in argv).</summary>
public sealed record DaProbeRequest(
    string ProgId,
    string? Host = null,
    string? Username = null,
    string? Password = null,
    string? Domain = null);

/// <summary>The single JSON line the child prints on stdout.</summary>
public sealed record DaProbeResult(
    bool Ok,
    bool Activated,
    string? ServerInfo = null,
    string? Error = null,
    int? HResult = null,
    string? Classification = null,
    bool Died = false);

/// <summary>
/// The OPC DA activation probe. Activation loads an in-proc vendor DLL into the calling
/// process — the PMD server took the whole bridge down that way — so the parent runs it in a
/// short-lived copy of this executable and reports a dead child as a failed probe instead of
/// a failed bridge. The child runs before the instance lock and the web host (see Program.cs),
/// so it works while the service is running and never touches appsettings or ports.
/// </summary>
public static class DaProbe
{
    public const string ModeArgument = "--da-probe";
    public const int ProbeTimeoutSeconds = 25;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsProbeInvocation(string[] args) =>
        args.Length == 1 && string.Equals(args[0], ModeArgument, StringComparison.Ordinal);

    /// <summary>Child mode: activate the configured server, print one result line, set the exit code.</summary>
    public static int RunChild()
    {
        DaProbeResult result;
        try
        {
            DaProbeRequest? request = JsonSerializer.Deserialize<DaProbeRequest>(Console.In.ReadToEnd(), JsonOptions);
            result = request is null || string.IsNullOrWhiteSpace(request.ProgId)
                ? new DaProbeResult(false, false, Error: "ProgId is required.")
                : !OperatingSystem.IsWindows()
                    ? new DaProbeResult(false, false, Error: "OPC DA activation requires Windows.")
                    : RunActivation(request);
        }
        catch (Exception exception)
        {
            result = new DaProbeResult(false, false, Error: exception.Message);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        Console.Out.Flush();
        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// Runs the activation probe under the bridge's own identity (or the supplied credentials)
    /// and returns its result. Never throws for probe outcomes — a dead or timed-out child is
    /// itself a diagnosis.
    /// </summary>
    public static async Task<DaProbeResult> RunAsync(DaProbeRequest request, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (string fileName, List<string> arguments) = ResolveChildCommand();
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

            using Process process = new() { StartInfo = startInfo };
            process.Start();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
            process.StandardInput.Close();

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new DaProbeResult(
                    false,
                    false,
                    Error: $"Activation probe timed out after {ProbeTimeoutSeconds} s and was killed; the server may be hanging.",
                    Died: true);
            }

            string output = await standardOutput.ConfigureAwait(false);
            string errorOutput = await standardError.ConfigureAwait(false);
            DaProbeResult? parsed = ParseLastJsonLine(output);
            if (parsed is not null)
            {
                return parsed;
            }

            return new DaProbeResult(
                false,
                false,
                Error: string.IsNullOrWhiteSpace(errorOutput)
                    ? $"Activation probe exited with code {process.ExitCode} and produced no result."
                    : errorOutput.Trim(),
                Died: true);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Last non-empty stdout line that parses; other output may precede the JSON line.</summary>
    internal static DaProbeResult? ParseLastJsonLine(string output)
    {
        string[] lines = output.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                DaProbeResult? result = JsonSerializer.Deserialize<DaProbeResult>(line, JsonOptions);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static DaProbeResult RunActivation(DaProbeRequest request)
    {
        DaClientOptions options = new()
        {
            SourceId = "da-probe",
            DisplayName = "OPC DA probe",
            ProgId = request.ProgId.Trim(),
            Host = string.IsNullOrWhiteSpace(request.Host) ? "localhost" : request.Host!,
            UpdateRateMs = 1000,
            UseSubscriptions = false,
            IoMode = "Sync",
            RemoteUsername = request.Username,
            RemotePassword = request.Password,
            RemoteDomain = request.Domain
        };

        OpcDaClient client = new(options);
        try
        {
            // Connect only: activation and GetStatus. No groups, no Advise — the PMD crash
            // lived in the subscription lifecycle and the probe stays clear of it.
            try
            {
                client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                return Failure(exception);
            }

            return new DaProbeResult(
                true,
                true,
                ServerInfo: client.ServerInfo?.Describe() ?? "connected");
        }
        finally
        {
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Teardown of a half-open connection must not mask the result.
            }
        }
    }

    private static DaProbeResult Failure(Exception exception) =>
        exception is COMException com
            ? new DaProbeResult(false, false, Error: com.Message, HResult: com.HResult, Classification: Classify(com.HResult))
            : new DaProbeResult(false, false, Error: exception.Message);

    private static string? Classify(int hresult) => hresult switch
    {
        unchecked((int)0x80040154) => "class-not-registered",
        unchecked((int)0x80070003) => "path-not-found",
        unchecked((int)0x8007007E) => "module-not-found",
        unchecked((int)0x800706BA) => "rpc-server-unavailable",
        unchecked((int)0x80080005) => "server-execution-failed",
        _ => null
    };

    private static (string FileName, List<string> Arguments) ResolveChildCommand()
        => DaChildProcess.Resolve(ModeArgument);
}
