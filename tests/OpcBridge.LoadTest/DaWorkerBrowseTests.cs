using System.IO.Pipelines;
using OpcBridge.App;
using OpcBridge.Client.Workers;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The worker side of the browse request. Unknown or malformed requests must answer with an
/// error frame instead of faulting the worker; the COM path itself needs a Windows rig with a
/// real DA server.
/// </summary>
public sealed class DaWorkerBrowseTests
{
    [Fact]
    public async Task Browse_UnknownSource_AnswersWithAnError()
    {
        var pipe = new Pipe();
        var bootstrap = new WorkerBootstrap("1", "worker-test", "pipe-test", 1, Array.Empty<WorkerSourceConfig>());
        await using var session = new DaWorkerSession(bootstrap, pipe.Writer.AsStream());
        session.Start();

        Assert.True(session.HandleFrame(WorkerFrame.Create(
            WorkerFrameTypes.Browse,
            7,
            new WorkerBrowseRequest("missing", null, false))));

        WorkerFrame response = await ReadUntilAsync(pipe.Reader.AsStream(), WorkerFrameTypes.BrowseResult);
        WorkerBrowseResult? payload = response.PayloadAs<WorkerBrowseResult>();

        Assert.Equal(7, response.Seq);
        Assert.NotNull(payload);
        Assert.Contains("missing", payload!.Error);
    }

    [Fact]
    public async Task Browse_KnownSourceOffWindows_AnswersWithThePlatformError()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The COM path is exercised on the Windows rig.
        }

        var pipe = new Pipe();
        var bootstrap = new WorkerBootstrap("1", "worker-test", "pipe-test", 1, new[]
        {
            new WorkerSourceConfig(
                "pmd",
                "PMD.DDT_OPCDataServer.1",
                "localhost",
                null,
                null,
                null,
                "AutoDetect",
                1000,
                true,
                Array.Empty<WorkerGroupIoMode>(),
                0)
        });
        await using var session = new DaWorkerSession(bootstrap, pipe.Writer.AsStream());
        session.Start();

        session.HandleFrame(WorkerFrame.Create(
            WorkerFrameTypes.Browse,
            8,
            new WorkerBrowseRequest("pmd", "Line1", true)));

        WorkerFrame response = await ReadUntilAsync(pipe.Reader.AsStream(), WorkerFrameTypes.BrowseResult);
        WorkerBrowseResult? payload = response.PayloadAs<WorkerBrowseResult>();

        Assert.Equal(8, response.Seq);
        Assert.NotNull(payload);
        Assert.Contains("Windows", payload!.Error);
    }

    private static async Task<WorkerFrame> ReadUntilAsync(Stream stream, string frameType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            WorkerFrame? frame = await FrameCodec.ReadAsync(stream, timeout.Token);
            if (frame is null)
            {
                throw new IOException("The worker pipe closed before the expected frame arrived.");
            }

            if (frame.Type == frameType)
            {
                return frame;
            }
        }
    }
}
