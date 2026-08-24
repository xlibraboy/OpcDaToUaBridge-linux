using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class HelpContentTests
{
    [Fact]
    public void HelpText_DescribesInterlinksAsAnySourceTagLinking()
    {
        Assert.Contains("# Interlinks", HelpContent.Markdown);
        Assert.Contains("separate subsystem", HelpContent.Markdown);
        Assert.DoesNotContain("# DA Links", HelpContent.Markdown);
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

    [Fact]
    public void HelpText_DescribesOpcUaSources()
    {
        Assert.Contains("OPC UA (client sources)", HelpContent.Markdown);
        Assert.Contains("## OPC UA Source vs OPC UA Server Endpoint", HelpContent.Markdown);
        Assert.Contains("NodeId string", HelpContent.Markdown);
        Assert.Contains("SignAndEncrypt", HelpContent.Markdown);
        Assert.Contains("Basic256Sha256", HelpContent.Markdown);
        Assert.Contains("Only **mapped** tags are subscribed", HelpContent.Markdown);
    }

    [Fact]
    public void HelpText_DescribesGroupedNavigation()
    {
        Assert.Contains("## Dashboard Navigation", HelpContent.Markdown);
        Assert.Contains("Sources", HelpContent.Markdown);
        Assert.Contains("Historian", HelpContent.Markdown);
        Assert.Contains("IoT", HelpContent.Markdown);
        Assert.Contains("Setup Wizard", HelpContent.Markdown);
    }
}
