using System.Text.Json;
using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The issues record is an embedded markdown file (ISSUES.md) served at /api/issues and
/// shown under Ops ▸ Issues to Admin users. These tests fail if the repository file, the
/// shipped copy or the dashboard wiring drift apart.
/// </summary>
public sealed class IssuesTests
{
    private const string IssuesFile = "ISSUES.md";

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpcBridge.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string OnDisk() =>
        File.ReadAllText(Path.Combine(RepoRoot(), IssuesFile)).Replace("\r\n", "\n");

    [Fact]
    public void IssuesFile_StartsWithHeaderAndHasEntries()
    {
        string markdown = OnDisk();

        Assert.StartsWith("# Issues", markdown, StringComparison.Ordinal);
        Assert.Contains("**Status:**", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedIssues_MatchTheRepositoryFile()
    {
        Assert.Equal(OnDisk(), IssuesContent.Markdown);
    }

    [Fact]
    public void Dashboard_ShipsTheIssuesView()
    {
        string html = DashboardPage.FullHtml;

        Assert.Contains("data-tab=\"issues\"", html, StringComparison.Ordinal);
        Assert.Contains("data-route=\"ops/issues\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"view-issues\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"navIssues\"", html, StringComparison.Ordinal);
        Assert.Contains("loadIssues", html, StringComparison.Ordinal);
        Assert.Contains("/api/issues", html, StringComparison.Ordinal);
    }
}

/// <summary>Route wiring for the issues endpoint (the content itself is covered above).</summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class IssuesApiTests
{
    [Fact]
    public async Task IssuesEndpoint_ServesTheEmbeddedRecord()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });

        using JsonDocument document = await handle.GetJsonAsync("/api/issues");
        string markdown = document.RootElement.GetProperty("markdown").GetString()!;

        Assert.StartsWith("# Issues", markdown, StringComparison.Ordinal);
        Assert.Equal(IssuesContent.Markdown, markdown);
    }
}
