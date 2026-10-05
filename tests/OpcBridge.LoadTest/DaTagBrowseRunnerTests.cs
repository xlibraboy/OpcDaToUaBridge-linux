using OpcBridge.App;
using OpcBridge.Client.Workers;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Routing of a DA tag browse: a worker-placed source must browse through its worker (the COM
/// call then carries the worker identity), an in-process source keeps the bridge-process path.
/// </summary>
public sealed class DaTagBrowseRunnerTests
{
    private sealed class StubChannel : IWorkerChannel
    {
        public string? LastType { get; private set; }

        public object? LastPayload { get; private set; }

        public WorkerFrame? Response { get; set; }

        public event Action<WorkerFrame>? PushReceived
        {
            add { }
            remove { }
        }

        public event Action<Exception?>? Closed
        {
            add { }
            remove { }
        }

        public Exception? ClosedReason => null;

        public Task<WorkerFrame> RequestAsync(string type, object payload, TimeSpan timeout, CancellationToken cancellationToken)
        {
            LastType = type;
            LastPayload = payload;
            return Task.FromResult(Response ?? throw new InvalidOperationException("No response staged."));
        }
    }

    [Fact]
    public async Task WorkerPlacedSource_BrowsesThroughTheWorker()
    {
        var channel = new StubChannel
        {
            Response = WorkerFrame.Create(
                WorkerFrameTypes.BrowseResult,
                1,
                new WorkerBrowseResult(
                    new[] { "Line1" },
                    new[] { new WorkerBrowseTag("T1", "Line1.T1", 5, 3) },
                    new[] { "warning" }))
        };
        var host = new TestWorkerHost(channel);

        OpcTagBrowseResult result = await DaTagBrowseRunner.RunAsync(
            new DaTagBrowseRequest("pmd", "PMD.DDT_OPCDataServer.1", "localhost", "Line1", Recursive: false),
            Snapshot(Da("pmd", new DaWorkerOptions(DaWorkerModes.Own, ".\\mesadm1"))),
            host,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal("own:pmd", host.LastWorkerKey);
        Assert.Equal(WorkerFrameTypes.Browse, channel.LastType);
        WorkerBrowseRequest request = Assert.IsType<WorkerBrowseRequest>(channel.LastPayload);
        Assert.Equal("pmd", request.SourceId);
        Assert.Equal("Line1", request.Path);
        Assert.False(request.Recursive);

        Assert.Equal("Line1", Assert.Single(result.Branches));
        OpcTagNode tag = Assert.Single(result.Tags);
        Assert.Equal("T1", tag.Name);
        Assert.Equal("Line1.T1", tag.ItemId);
        Assert.Equal((short)5, tag.CanonicalDataType);
        Assert.Equal(3, tag.AccessRights);
        Assert.Equal("warning", Assert.Single(result.Warnings!));
    }

    [Fact]
    public async Task GroupPlacedSource_BrowsesThroughItsSharedWorker()
    {
        var channel = new StubChannel
        {
            Response = WorkerFrame.Create(
                WorkerFrameTypes.BrowseResult,
                1,
                new WorkerBrowseResult(Array.Empty<string>(), Array.Empty<WorkerBrowseTag>(), Array.Empty<string>()))
        };
        var host = new TestWorkerHost(channel);

        OpcTagBrowseResult result = await DaTagBrowseRunner.RunAsync(
            new DaTagBrowseRequest("b", "Vendor.Server.1", "localhost"),
            Snapshot(
                Da("a", new DaWorkerOptions(DaWorkerModes.Group, ".\\mesadm1")),
                Da("b", new DaWorkerOptions(DaWorkerModes.Group, " .\\MESADM1 "))),
            host,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        // Both sources share one worker key, canonicalized by account.
        Assert.Equal("acct:.\\MESADM1", host.LastWorkerKey);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task WorkerPlacedSource_WorkerErrorBecomesAnException()
    {
        var channel = new StubChannel
        {
            Response = WorkerFrame.Create(
                WorkerFrameTypes.BrowseResult,
                1,
                WorkerBrowseResult.Failed("Logon failed for 'DOM\\user' (Win32 error 1326)."))
        };
        var host = new TestWorkerHost(channel);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DaTagBrowseRunner.RunAsync(
                new DaTagBrowseRequest("pmd", "PMD.DDT_OPCDataServer.1", "localhost"),
                Snapshot(Da("pmd", new DaWorkerOptions(DaWorkerModes.Own, ".\\mesadm1"))),
                host,
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.Contains("1326", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InProcessSource_DoesNotTouchTheWorker()
    {
        var channel = new StubChannel();
        var host = new TestWorkerHost(channel);

        // The in-process browse goes to real COM: on this host that is the platform guard
        // (non-Windows) or the local registration check (Windows) — either way it throws and
        // the worker channel is never requested.
        await Assert.ThrowsAnyAsync<Exception>(() => DaTagBrowseRunner.RunAsync(
            new DaTagBrowseRequest("pmd", "OpcBridge.NoSuchServer.1", "localhost"),
            Snapshot(Da("pmd")),
            host,
            TimeSpan.FromSeconds(5),
            CancellationToken.None));

        Assert.Null(channel.LastType);
    }

    [Fact]
    public void BrowseResult_IsAResponseTypeOnTheParentConnection()
    {
        // WorkerConnection answers a pending request only for frames in ResponseTypes; a
        // browse result missing there would arrive as a push and every browse would time out.
        Assert.Contains(WorkerFrameTypes.BrowseResult, WorkerConnection.ResponseTypes);
    }

    private static DaSourceRuntimeSettings Da(string id, DaWorkerOptions? worker = null) => new(
        SourceId: id,
        DisplayName: id,
        SourceType: SourceTypes.OpcDa,
        UpdateRateMs: 1000,
        UseSubscriptions: true,
        MaxMappedTags: 50000,
        OpcDa: new OpcDaSourceOptions("Vendor.Server.1", "localhost", null, null, null, Worker: worker),
        OpcUa: null,
        Melsec: null,
        S7200: null);

    private static DaRuntimeSettingsSnapshot Snapshot(params DaSourceRuntimeSettings[] sources) =>
        new(1000, true, sources, Version: 1);
}
