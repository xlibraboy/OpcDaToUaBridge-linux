using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpcBridge.App;
using OpcBridge.Client.Workers;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The parent→child stdin handshake of a DA worker, vendor-neutral. Regression tests for the
/// run-as worker start failure: the bootstrap was written with <c>Encoding.UTF8</c>, whose
/// byte-order mark <c>StreamWriter</c> writes and <c>System.Text.Json</c> rejects — every
/// worker under a different run-as account exited before it reached its pipe.
/// </summary>
public sealed class DaWorkerBootstrapTests
{
    [Fact]
    public void BootstrapEncoding_DoesNotEmitABom()
    {
        Assert.Empty(DaWorkerIdentity.BootstrapStdinEncoding.GetPreamble());
    }

    [Fact]
    public void Bootstrap_RoundTripsThroughTheChildReader()
    {
        WorkerBootstrap bootstrap = new(
            WorkerProtocol.Version,
            "own:vendor",
            "opcbridge-da-worker-test",
            1234,
            new[]
            {
                new WorkerSourceConfig(
                    "vendor", "Vendor.Server.1", "localhost", null, null, null,
                    "Sync", 1000, false, Array.Empty<WorkerGroupIoMode>(), 60000)
            });

        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new StreamWriter(stream, DaWorkerIdentity.BootstrapStdinEncoding) { AutoFlush = true })
            {
                writer.WriteLine(JsonSerializer.Serialize(bootstrap, WorkerProtocol.JsonOptions));
            }

            bytes = stream.ToArray();
        }

        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the bootstrap must not start with a UTF-8 BOM");

        // Exactly how the child reads it back.
        using var input = new MemoryStream(bytes);
        WorkerBootstrap? parsed = JsonSerializer.Deserialize<WorkerBootstrap>(
            DaWorkerHost.ReadBootstrapText(input),
            WorkerProtocol.JsonOptions);

        Assert.NotNull(parsed);
        Assert.Equal("own:vendor", parsed!.WorkerId);
        Assert.Equal("Vendor.Server.1", Assert.Single(parsed.Sources).ProgId);
    }

    [Fact]
    public void Bootstrap_WithALegacyBom_IsStillReadable()
    {
        // A parent built before the fix wrote a BOM; the reader tolerates it so a mixed
        // upgrade cannot strand a worker the same way again.
        WorkerBootstrap bootstrap = new(
            WorkerProtocol.Version,
            "own:vendor",
            "pipe",
            1,
            Array.Empty<WorkerSourceConfig>());
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bootstrap, WorkerProtocol.JsonOptions));
        byte[] bytes = new byte[payload.Length + 3];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        payload.CopyTo(bytes, 3);

        using var input = new MemoryStream(bytes);
        WorkerBootstrap? parsed = JsonSerializer.Deserialize<WorkerBootstrap>(
            DaWorkerHost.ReadBootstrapText(input),
            WorkerProtocol.JsonOptions);

        Assert.NotNull(parsed);
        Assert.Equal("own:vendor", parsed!.WorkerId);
    }

    [Fact]
    public async Task ChildDiagnostics_AreReadFromStderr()
    {
        using Process process = StartShell("printf 'bad bootstrap: boom' >&2; exit 71");
        DaWorkerChild child = Child(process);

        string diagnostics = await DaWorkerProcess.TryReadChildDiagnosticsAsync(child, TimeSpan.FromSeconds(5));

        Assert.Equal("bad bootstrap: boom", diagnostics);
    }

    [Fact]
    public async Task ChildDiagnostics_AreBoundedWhenTheChildSaysNothing()
    {
        using Process process = StartShell("sleep 30");
        try
        {
            DaWorkerChild child = Child(process);

            var elapsed = Stopwatch.StartNew();
            string diagnostics = await DaWorkerProcess.TryReadChildDiagnosticsAsync(
                child,
                TimeSpan.FromMilliseconds(200));
            elapsed.Stop();

            Assert.Equal(string.Empty, diagnostics);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"diagnostics read took {elapsed.Elapsed}");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void StartFailureMessage_IncludesTheChildOutput()
    {
        var timeout = new TimeoutException("did not connect to its pipe within 10 s.");

        string bare = DaWorkerProcess.DescribeStartFailure("own:vendor", timeout, string.Empty);
        Assert.Contains("failed to start", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("Worker output", bare, StringComparison.Ordinal);

        string detailed = DaWorkerProcess.DescribeStartFailure(
            "own:vendor",
            timeout,
            "OpcBridge worker: bad bootstrap: '0xEF' is an invalid start of a value.");
        Assert.Contains("bad bootstrap", detailed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_WorkerStartFailure_SurfacesAsTransient()
    {
        // The supervisor reports a failed start as SourceConnectionLostException; the proxy
        // must pass that through so BridgeWorker retries instead of faulting the source.
        var host = new TestWorkerHost
        {
            EnsureError = new SourceConnectionLostException("Worker 'own:vendor' failed to start: pipe timeout.")
        };
        var client = new WorkerSourceClient(host, "own:vendor", "vendor");

        await Assert.ThrowsAsync<SourceConnectionLostException>(() => client.ConnectAsync(CancellationToken.None));
    }

    private static Process StartShell(string script)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return Process.Start(startInfo)!;
    }

    private static DaWorkerChild Child(Process process) => new()
    {
        Process = process,
        StandardInput = process.StandardInput,
        StandardOutput = process.StandardOutput.BaseStream,
        StandardError = process.StandardError
    };
}
