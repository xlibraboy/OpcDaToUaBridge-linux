using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.MxComponent;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MxComponentFactoryTests
{
    private static DaSourceRuntimeSettings MxSource(string sourceId = "mx1", int station = 2) =>
        SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = sourceId,
            SourceType = SourceTypes.MxComponent,
            LogicalStationNumber = station,
            TimeoutMs = 4200,
            RetryCount = 1,
            UpdateRateMs = 1000
        }, 1000);

    private static DaRuntimeSettingsSnapshot Snapshot(params DaSourceRuntimeSettings[] sources) =>
        new(1000, true, sources, 1);

    [Fact]
    public void Create_MxComponent_ReturnsMxComponentClient()
    {
        var factory = new SourceClientFactory();
        DaSourceRuntimeSettings source = MxSource();
        ISourceClient client = factory.Create(Snapshot(source), source);
        Assert.IsType<MxComponentClient>(client);
    }

    [Fact]
    public void Create_MxComponent_SourceTypeIsCaseInsensitive()
    {
        var factory = new SourceClientFactory();
        DaSourceRuntimeSettings source = MxSource() with { SourceType = "mxcomponent" };
        ISourceClient client = factory.Create(Snapshot(source), source);
        Assert.IsType<MxComponentClient>(client);
    }

    [Fact]
    public void Create_MxComponent_PropagatesLogicalStationAndSourceId()
    {
        var factory = new SourceClientFactory();
        DaSourceRuntimeSettings source = MxSource(sourceId: "plc-mx", station: 7);
        var mx = Assert.IsType<MxComponentClient>(factory.Create(Snapshot(source), source));

        var optionsField = typeof(MxComponentClient).GetField(
            "_options",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(optionsField);
        var options = Assert.IsType<MxComponentClientOptions>(optionsField!.GetValue(mx));
        Assert.Equal("plc-mx", options.SourceId);
        Assert.Equal(7, options.LogicalStationNumber);
        Assert.Equal(4200, options.TimeoutMs);
        Assert.Equal(1, options.RetryCount);
    }
}
