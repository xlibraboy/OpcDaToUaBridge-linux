using Xunit;

namespace OpcBridge.LoadTest;

// These tests guard the MkDocs documentation site (docs/*.md, served at /docs/).
// The docs replace the old in-app Help tab (HelpContent.Markdown + /api/help, removed).
public sealed class HelpContentTests
{
    private static readonly string DocsDir = FindDocsDir();

    private static string FindDocsDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "docs");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.md")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repository docs/ directory from the test output directory.");
    }

    private static string ReadPage(string name)
    {
        string path = Path.Combine(DocsDir, name);
        Assert.True(File.Exists(path), $"Missing docs page: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Docs_DescribeDaLinksAsIndependentSubsystem()
    {
        string md = ReadPage("da-links.md");
        Assert.Contains("DA Links", md);
        Assert.Contains("separate subsystem", md);
        Assert.DoesNotContain("faceplate → Setup → Provider", md);
    }

    [Fact]
    public void Docs_DescribeInfluxHistoricalLogging()
    {
        string md = ReadPage("influxdb.md");
        Assert.Contains("# InfluxDB (Historical Logging)", md);
        Assert.Contains("External InfluxDB 2.x/3.x server required", md);
        Assert.Contains("Enable per tag via faceplate Influx checkbox", md);
        Assert.Contains("Outage does not stop the bridge", md);
    }

    [Fact]
    public void Docs_DescribeOpcUaSources()
    {
        string md = ReadPage("opcua-endpoint.md");
        Assert.Contains("# OPC UA Source vs OPC UA Server Endpoint", md);
        Assert.Contains("NodeId string", md);
        Assert.Contains("SignAndEncrypt", md);
        Assert.Contains("Basic256Sha256", md);
        Assert.Contains("Only **mapped** tags are subscribed", md);
    }

    [Fact]
    public void Docs_DescribeGroupedNavigation()
    {
        string md = ReadPage("topology.md");
        Assert.Contains("## Dashboard Navigation", md);
        Assert.Contains("Sources", md);
        Assert.Contains("Historian", md);
        Assert.Contains("IoT", md);
        Assert.Contains("Setup Wizard", md);
    }

    [Fact]
    public void Docs_DocumentationMenu_EmbeddedInDashboard()
    {
        string md = ReadPage("topology.md");
        Assert.Contains("Docs ──► Documentation (embedded in dashboard), About", md);
        Assert.DoesNotContain("opens in new tab", md);
    }
}
