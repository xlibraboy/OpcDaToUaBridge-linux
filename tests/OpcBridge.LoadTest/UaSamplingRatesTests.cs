using OpcBridge.Core;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class UaSamplingRatesTests
{
    private static TagMapping Mapping(string itemId, int pollRateMs = 0) => new()
    {
        SourceId = "ua-demo",
        ItemId = itemId,
        PollRateMs = pollRateMs
    };

    [Fact]
    public void BuildDesiredSampling_PerTagRateWins_ElseSourceDefault()
    {
        var mappings = new[]
        {
            Mapping("ns=2;s=Tag00001", pollRateMs: 250),
            Mapping("ns=2;s=Tag00002", pollRateMs: 5000),
            Mapping("ns=2;s=Tag00003") // no override -> source default
        };

        var sampling = UaSamplingRates.BuildDesiredSampling(mappings, defaultSamplingMs: 1000);

        Assert.Equal(250, sampling["ns=2;s=Tag00001"]);
        Assert.Equal(5000, sampling["ns=2;s=Tag00002"]);
        Assert.Equal(1000, sampling["ns=2;s=Tag00003"]);
    }

    [Fact]
    public void BuildDesiredSampling_ExcludesDisabledManualAndWriteOnly()
    {
        var mappings = new[]
        {
            new TagMapping { SourceId = "ua-demo", ItemId = "ns=2;s=Disabled", Enabled = false },
            new TagMapping { SourceId = "ua-demo", ItemId = "ns=2;s=Manual", Mode = TagMode.Manual },
            new TagMapping { SourceId = "ua-demo", ItemId = "ns=2;s=WriteOnly", AccessRights = TagAccessRights.Write },
            new TagMapping { SourceId = "ua-demo", ItemId = "" } // empty NodeId
        };

        var sampling = UaSamplingRates.BuildDesiredSampling(mappings, defaultSamplingMs: 1000);

        Assert.Empty(sampling);
    }

    [Fact]
    public void BuildDesiredSampling_TrimsNodeIdsAndFirstWins()
    {
        var mappings = new[]
        {
            Mapping("  ns=2;s=Tag00001  ", pollRateMs: 250),
            Mapping("ns=2;s=Tag00001", pollRateMs: 5000) // duplicate key, first wins
        };

        var sampling = UaSamplingRates.BuildDesiredSampling(mappings, defaultSamplingMs: 1000);

        Assert.Equal(250, sampling["ns=2;s=Tag00001"]);
    }

    [Fact]
    public void DesiredPublishingInterval_IsFastestSampling()
    {
        var sampling = new Dictionary<string, int>
        {
            ["ns=2;s=Tag00001"] = 250,
            ["ns=2;s=Tag00002"] = 1000,
            ["ns=2;s=Tag00003"] = 5000
        };

        // The subscription must publish at least as fast as the fastest tag so that
        // tag's per-tag rate actually drives its delivery cadence.
        Assert.Equal(250, UaSamplingRates.DesiredPublishingInterval(sampling, defaultSamplingMs: 1000));
    }

    [Fact]
    public void DesiredPublishingInterval_ClampsToMinimum100()
    {
        var sampling = new Dictionary<string, int> { ["ns=2;s=Tag00001"] = 50 };

        Assert.Equal(100, UaSamplingRates.DesiredPublishingInterval(sampling, defaultSamplingMs: 1000));
    }

    [Fact]
    public void DesiredPublishingInterval_EmptyMappingsFallsBackToSourceDefault()
    {
        Assert.Equal(1000, UaSamplingRates.DesiredPublishingInterval(
            new Dictionary<string, int>(), defaultSamplingMs: 1000));
    }

    [Fact]
    public void RateChange_OnMapsFaceplate_MovesPublishingInterval()
    {
        var mappings = new[]
        {
            Mapping("ns=2;s=Tag00001", pollRateMs: 1000),
            Mapping("ns=2;s=Tag00002")
        };

        Dictionary<string, int> sampling = UaSamplingRates.BuildDesiredSampling(mappings, defaultSamplingMs: 1000);
        Assert.Equal(1000, UaSamplingRates.DesiredPublishingInterval(sampling, 1000));

        // Tag00001's faceplate rate drops to 250 ms -> subscription publishes at 250 ms.
        mappings[0].PollRateMs = 250;
        sampling = UaSamplingRates.BuildDesiredSampling(mappings, defaultSamplingMs: 1000);
        Assert.Equal(250, UaSamplingRates.DesiredPublishingInterval(sampling, 1000));
        Assert.Equal(250, sampling["ns=2;s=Tag00001"]);
        Assert.Equal(1000, sampling["ns=2;s=Tag00002"]);
    }
}
