using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpcBridge.Client.Workers;

namespace OpcBridge.App;

/// <summary>
/// DA worker child mode (<c>OpcBridge.App --da-worker</c>): hosts one or more OPC DA source
/// clients in a separate process, so a fault in a vendor in-proc server kills only this
/// process instead of the bridge. The parent spawns it, writes one bootstrap JSON line on
/// stdin (credentials never in argv) and then talks over a named pipe. It runs before the
/// crash handlers, the instance lock and the web host (Program.cs).
/// </summary>
internal static class DaWorkerHost
{
    public const string ModeArgument = "--da-worker";

    /// <summary>Exit contract: 0 clean shutdown, 70 pipe closed / parent gone,
    /// 71 bootstrap or platform error, 72 protocol violation or fatal error.</summary>
    public const int ExitClean = 0;
    public const int ExitPipeClosed = 70;
    public const int ExitBootstrapError = 71;
    public const int ExitProtocolError = 72;

    private const int PipeConnectTimeoutMs = 10_000;

    public static bool IsWorkerInvocation(string[] args) =>
        args.Length == 1 && string.Equals(args[0], ModeArgument, StringComparison.Ordinal);

    /// <summary>Child mode: read the bootstrap, serve the pipe until EOF or shutdown.</summary>
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("OPC DA workers require Windows.");
            return ExitBootstrapError;
        }

        WorkerBootstrap? bootstrap;
        try
        {
            bootstrap = JsonSerializer.Deserialize<WorkerBootstrap>(ReadBootstrapText(Console.OpenStandardInput()), WorkerProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"OpcBridge worker: bad bootstrap: {ex.Message}");
            return ExitBootstrapError;
        }

        if (bootstrap is null
            || !string.Equals(bootstrap.ProtocolVersion, WorkerProtocol.Version, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(bootstrap.PipeName))
        {
            Console.Error.WriteLine("OpcBridge worker: bootstrap is missing or uses an unsupported protocol version.");
            return ExitBootstrapError;
        }

        // The worker is a long-lived process of its own: a managed fault here deserves the
        // same crash report the bridge writes.
        CrashLog.Install();

        using var pipe = new NamedPipeClientStream(".", bootstrap.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(PipeConnectTimeoutMs);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            Console.Error.WriteLine($"OpcBridge worker: could not reach the parent pipe '{bootstrap.PipeName}': {ex.Message}");
            return ExitPipeClosed;
        }

        try
        {
            return RunSessionAsync(bootstrap, pipe).GetAwaiter().GetResult();
        }
        catch (WorkerProtocolException ex)
        {
            Console.Error.WriteLine($"OpcBridge worker: protocol violation: {ex.Message}");
            return ExitProtocolError;
        }
        catch (IOException)
        {
            return ExitPipeClosed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OpcBridge worker: fatal: {ex}");
            return ExitProtocolError;
        }
    }

    /// <summary>
    /// Reads the parent's bootstrap line as UTF-8 — deliberately not <c>Console.In</c>, which
    /// decodes with the console code page (not necessarily UTF-8 on a service host), and with
    /// byte-order-mark detection on so a stray mark can never reach the JSON parse. A BOM is
    /// not valid JSON: a parent that wrote one made every run-as worker exit here.
    /// </summary>
    internal static string ReadBootstrapText(Stream stdin)
    {
        using var reader = new StreamReader(
            stdin,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static async Task<int> RunSessionAsync(WorkerBootstrap bootstrap, Stream pipe)
    {
        await using var session = new DaWorkerSession(bootstrap, pipe);
        session.Start();
        while (true)
        {
            WorkerFrame? frame = await FrameCodec.ReadAsync(pipe).ConfigureAwait(false);
            if (frame is null)
            {
                return ExitPipeClosed;
            }

            if (!session.HandleFrame(frame))
            {
                await session.StopAsync().ConfigureAwait(false);
                return ExitClean;
            }
        }
    }
}
