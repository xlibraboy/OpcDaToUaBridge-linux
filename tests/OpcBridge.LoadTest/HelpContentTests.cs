using System.Text.RegularExpressions;
using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class HelpContentTests
{
    private static string[] Groups() => Regex.Split(HelpContent.Markdown, @"\r?\n===\r?\n");

    private static string[] Sections(string group) =>
        Regex.Split(group, @"\r?\n---\r?\n").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

    private static string SectionTitle(string section)
    {
        var match = Regex.Match(section, @"^#\s+(.+)", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }
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
        Assert.Contains("# OPC UA Source vs OPC UA Server Endpoint", HelpContent.Markdown);
        Assert.Contains("NodeId string", HelpContent.Markdown);
        Assert.Contains("SignAndEncrypt", HelpContent.Markdown);
        Assert.Contains("Basic256Sha256", HelpContent.Markdown);
        Assert.Contains("Only **mapped** tags are subscribed", HelpContent.Markdown);
    }

    [Fact]
    public void HelpText_DescribesGroupedNavigation()
    {
        Assert.Contains("# Dashboard Navigation", HelpContent.Markdown);
        Assert.Contains("Sources", HelpContent.Markdown);
        Assert.Contains("Historian", HelpContent.Markdown);
        Assert.Contains("IoT", HelpContent.Markdown);
        Assert.Contains("Setup Wizard", HelpContent.Markdown);
    }

    [Fact]
    public void Guide_Groups_MapOntoTheThreeSubTabs()
    {
        var groups = Groups();
        Assert.True(groups.Length >= 3, $"expected 3 groups, got {groups.Length}");

        var gettingStarted = Sections(groups[0]).Select(SectionTitle).ToArray();
        Assert.Contains("Getting Started", gettingStarted);
        Assert.Contains("Dashboard Navigation", gettingStarted);
        Assert.Contains("Topology & Data Flow", gettingStarted);
        Assert.DoesNotContain("Troubleshooting", gettingStarted);

        var features = Sections(groups[1]).Select(SectionTitle).ToArray();
        Assert.Contains("Access Rights & Simulation", features);
        Assert.Contains("Update Rate & Tag Limits", features);
        Assert.Contains("Interlinks", features);
        Assert.Contains("OPC UA Server", features);
        Assert.Contains("MQTT (OPC UA ↔ External Broker)", features);
        Assert.Contains("InfluxDB (Historical Logging)", features);
        Assert.Contains("OPC DA Server Discovery", features);
        Assert.Contains("PLC Drivers (Mitsubishi A3N)", features);
        Assert.Contains("PLC Drivers (Mitsubishi A3N — MX Component 4)", features);
        Assert.Contains("PLC Drivers (Siemens S7-200 PPI)", features);

        var reference = Sections(groups[2]).Select(SectionTitle).ToArray();
        Assert.Contains("OPC UA Endpoint — Bind vs Connect", reference);
        Assert.Contains("OPC UA Source vs OPC UA Server Endpoint", reference);
        Assert.Contains("Unified UA Address Space", reference);
        Assert.Contains("Troubleshooting", reference);
        Assert.Contains("Installation on Windows", reference);
        Assert.Contains("Updating to a New Version", reference);
        Assert.Contains("OPC UA Certificates (PKI)", reference);
        Assert.Contains("Configuration Reference", reference);
    }

    [Fact]
    public void HelpText_DescribesPlcGroups()
    {
        Assert.Contains("## PLC Groups (MX Component)", HelpContent.Markdown);
        Assert.Contains("group rate wins", HelpContent.Markdown);
        Assert.Contains("/api/plc/groups", HelpContent.Markdown);
        Assert.Contains("Sources → PLC Groups", HelpContent.Markdown);
    }

    [Fact]
    public void DashboardNavigation_MatchesActualSidebarPages()
    {
        var md = HelpContent.Markdown;
        var start = md.IndexOf("# Dashboard Navigation", StringComparison.Ordinal);
        Assert.True(start >= 0, "Dashboard Navigation section missing");
        var end = md.IndexOf("\n# ", start, StringComparison.Ordinal);
        var nav = md[start..end];

        foreach (var page in new[] { "DA Groups", "OPC UA", "UA Subs", "MX Component", "Diagnostics", "Sessions" })
            Assert.Contains(page, nav);

        // The stale sidebar ASCII block must not resurrect the old "Connectivity" label
        // or park Diagnostics under it.
        Assert.DoesNotContain("Connectivity ──►", HelpContent.Markdown);
    }
}
