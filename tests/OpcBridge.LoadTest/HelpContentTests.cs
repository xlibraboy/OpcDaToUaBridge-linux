using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class HelpContentTests
{
    [Fact]
    public void HelpText_DescribesDaLinksAsIndependentSubsystem()
    {
        Assert.Contains("DA Links", HelpContent.Markdown);
        Assert.Contains("separate subsystem", HelpContent.Markdown);
        Assert.DoesNotContain("faceplate → Setup → Provider", HelpContent.Markdown);
    }

    [Fact]
    public void HelpText_DescribesInfluxHistoricalLogging()
    {
        Assert.Contains("# InfluxDB (Historical Logging)", HelpContent.Markdown);
        Assert.Contains("External InfluxDB 2.x/3.x server required", HelpContent.Markdown);
        Assert.Contains("Enable per tag via faceplate Influx checkbox", HelpContent.Markdown);
        Assert.Contains("Outage does not stop the bridge", HelpContent.Markdown);
    }
}
