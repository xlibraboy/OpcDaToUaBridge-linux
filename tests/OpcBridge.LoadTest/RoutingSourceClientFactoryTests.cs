using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class RoutingSourceClientFactoryTests
{
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

    [Fact]
    public void InProcessSource_UsesTheBaseFactory()
    {
        var factory = new RoutingSourceClientFactory();
        DaSourceRuntimeSettings source = Da("sim");
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);

        ISourceClient client = factory.Create(snapshot, source);

        Assert.IsType<OpcDaClient>(client);
    }

    [Fact]
    public void WorkerSource_WithoutAHost_Throws()
    {
        var factory = new RoutingSourceClientFactory();
        DaSourceRuntimeSettings source = Da("pmd", new DaWorkerOptions(DaWorkerModes.Own));
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);

        Assert.Throws<InvalidOperationException>(() => factory.Create(snapshot, source));
    }

    [Fact]
    public void WorkerSource_WithAHost_ReturnsTheProxy()
    {
        var factory = new RoutingSourceClientFactory(new TestWorkerHost(new NullWorkerChannel()));
        DaSourceRuntimeSettings source = Da("pmd", new DaWorkerOptions(DaWorkerModes.Own));
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);

        ISourceClient client = factory.Create(snapshot, source);

        Assert.IsType<WorkerSourceClient>(client);
    }
}
