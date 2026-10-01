using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using OpcBridge.Client.Workers;

namespace OpcBridge.App;

/// <summary>
/// One worker child process: spawn (under the bridge identity — a differing run-as account is
/// rejected until the privileged spawn path lands), bootstrap on stdin, named-pipe channel,
/// stderr forwarding into the bridge log, resource sampling and the shutdown/kill sequence.
/// </summary>
internal sealed class DaWorkerProcess : IAsyncDisposable
{
    private const int PipeConnectTimeoutMs = 10_000;
    private const int ShutdownGraceMs = 5_000;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    private readonly Process process_;
    private readonly Timer sampleTimer_;
    private readonly Task stderrPump_;
    private int stopping_;

    private DaWorkerProcess(string workerId, string account, Process process, WorkerConnection connection)
    {
        WorkerId = workerId;
        Account = account;
        process_ = process;
        Connection = connection;
        StartedUtc = DateTime.UtcNow;
        sampleTimer_ = new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
        stderrPump_ = PumpStderrAsync();
    }

    public string WorkerId { get; }

    public string Account { get; }

    public WorkerConnection Connection { get; }

    public DateTime StartedUtc { get; }

    public long WorkingSetBytes { get; private set; }

    public long PrivateBytes { get; private set; }

    public int HandleCount { get; private set; }

    public int Pid => process_.Id;

    public bool HasExited => process_.HasExited;

    public int ExitCode => process_.HasExited ? process_.ExitCode : 0;

    public bool Stopping => Volatile.Read(ref stopping_) == 1;

    public bool ExitRecorded { get; set; }

    public event Action<string>? LogLine;

    public static async Task<DaWorkerProcess> StartAsync(
        string workerId,
        DaWorkerOptions workerOptions,
        IReadOnlyList<WorkerSourceConfig> sources,
        CancellationToken cancellationToken)
    {
        EnsureSameIdentityOrThrow(workerOptions);

        string pipeName = "opcbridge-da-worker-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        (string fileName, List<string> arguments) = DaChildProcess.Resolve(DaWorkerHost.ModeArgument);
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
        try
        {
            process.Start();
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            WorkerBootstrap bootstrap = new(WorkerProtocol.Version, workerId, pipeName, Environment.ProcessId, sources);
            await process.StandardInput
                .WriteLineAsync(JsonSerializer.Serialize(bootstrap, WorkerProtocol.JsonOptions))
                .ConfigureAwait(false);
            process.StandardInput.Close();

            // The worker writes nothing to stdout (diagnostics go to stderr), but a redirected
            // stream nobody drains would eventually block it.
            _ = process.StandardOutput.ReadToEndAsync();

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(PipeConnectTimeoutMs);
            try
            {
                await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Worker '{workerId}' did not connect to its pipe within {PipeConnectTimeoutMs / 1000} s.");
            }

            var connection = new WorkerConnection(pipe);
            return new DaWorkerProcess(workerId, CurrentAccountName(), process, connection);
        }
        catch
        {
            TryKill(process);
            await pipe.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            throw;
        }
    }

    public void MarkStopping() => Volatile.Write(ref stopping_, 1);

    public Task WaitForExitAsync(CancellationToken cancellationToken) => process_.WaitForExitAsync(cancellationToken);

    public async Task ShutdownAsync()
    {
        MarkStopping();

        try
        {
            using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Connection.RequestAsync(
                    WorkerFrameTypes.Shutdown,
                    new WorkerShutdownRequest(ShutdownGraceMs),
                    TimeSpan.FromSeconds(2),
                    requestTimeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // A worker that already exited never answers; the exit wait below is what matters.
        }

        try
        {
            using var wait = new CancellationTokenSource(ShutdownGraceMs);
            await process_.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process_);
        }
    }

    public async ValueTask DisposeAsync()
    {
        sampleTimer_.Dispose();

        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        await Connection.DisposeAsync().ConfigureAwait(false);

        try
        {
            await stderrPump_.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        {
        }

        process_.Dispose();
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await process_.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    LogLine?.Invoke(line);
                }
            }
        }
        catch
        {
            // The stream ends when the process does.
        }
    }

    private void Sample()
    {
        try
        {
            process_.Refresh();
            WorkingSetBytes = process_.WorkingSet64;
            PrivateBytes = process_.PrivateMemorySize64;
            HandleCount = process_.HandleCount;
        }
        catch
        {
            // The process may have exited between the timer tick and the read.
        }
    }

    private static void EnsureSameIdentityOrThrow(DaWorkerOptions workerOptions)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OPC DA workers require Windows.");
        }

        if (string.IsNullOrWhiteSpace(workerOptions.RunAsUser))
        {
            return;
        }

        if (!IsCurrentIdentity(workerOptions.RunAsUser!, workerOptions.RunAsDomain))
        {
            throw new NotSupportedException(
                $"Worker run-as account '{workerOptions.RunAsUser}' differs from the bridge identity " +
                $"('{CurrentAccountName()}'); spawning under another account is not implemented yet. " +
                "Run the bridge as that account, or leave the run-as account empty.");
        }
    }

    private static bool IsCurrentIdentity(string user, string? domain)
    {
        string candidate = user.Trim();
        if (candidate.StartsWith(".\\", StringComparison.Ordinal))
        {
            candidate = Environment.MachineName + candidate[1..];
        }
        else if (!candidate.Contains('\\'))
        {
            string prefix = string.IsNullOrWhiteSpace(domain) ? Environment.MachineName : domain.Trim();
            candidate = prefix + "\\" + candidate;
        }

        return string.Equals(CurrentAccountName(), candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string CurrentAccountName()
        => OperatingSystem.IsWindows() ? CurrentWindowsAccountName() : Environment.UserName;

    [SupportedOSPlatform("windows")]
    private static string CurrentWindowsAccountName() => WindowsIdentity.GetCurrent().Name;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
