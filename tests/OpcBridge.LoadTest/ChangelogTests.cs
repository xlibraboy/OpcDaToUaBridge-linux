using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpcBridge.App;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Release notes are per-app files sharing one version: the server's CHANGELOG.md (the
/// release authority, served at /api/changelog) and one file per desktop app, each
/// embedded in its own assembly and listing only the releases that changed it. These
/// tests fail if the files, the shipped copies or the reported version drift apart.
/// </summary>
public sealed class ChangelogTests
{
    private const string ServerFile = "CHANGELOG.md";
    private const string HmiFile = "src/OpcBridge.Hmi/CHANGELOG.md";
    private const string DesignerFile = "src/OpcBridge.Hmi.Designer/CHANGELOG.md";

    private static readonly Regex ReleasedSection = new(
        @"^##\s+\[\s*(\d+\.\d+\.\d+)\s*\]\s+-\s+(\d{4}-\d{2}-\d{2})\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static TheoryData<string> ChangelogFiles => new() { ServerFile, HmiFile, DesignerFile };

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

    private static string OnDisk(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath)).Replace("\r\n", "\n");

    private static Dictionary<string, string> ReleasedVersions(string markdown) =>
        ReleasedSection.Matches(markdown)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);

    private static Version Newest(IEnumerable<string> versions) =>
        versions.Select(version => new Version(version)).OrderByDescending(version => version).First();

    [Theory]
    [MemberData(nameof(ChangelogFiles))]
    public void Changelog_FollowsKeepAChangelog(string relativePath)
    {
        string markdown = OnDisk(relativePath);

        Assert.StartsWith("# Changelog", markdown, StringComparison.Ordinal);
        Assert.Contains("Keep a Changelog", markdown, StringComparison.Ordinal);
        Assert.Contains("Semantic Versioning", markdown, StringComparison.Ordinal);
        Assert.Contains("## [Unreleased]", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerChangelog_RunsFromTheCurrentVersionToTheBaseline()
    {
        MatchCollection released = ReleasedSection.Matches(OnDisk(ServerFile));

        Assert.True(released.Count >= 2, $"expected at least the 1.0.0 baseline and the current release, got {released.Count}");

        // Newest first: the top released section is the current version.
        Assert.Equal("1.3.0", released[0].Groups[1].Value);
        Assert.Equal("1.0.0", released[^1].Groups[1].Value);
    }

    [Theory]
    [MemberData(nameof(ChangelogFiles))]
    public void ReleasedSections_AreNewestFirst(string relativePath)
    {
        string[] versions = ReleasedSection.Matches(OnDisk(relativePath))
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(versions);
        Assert.Equal(
            versions.OrderByDescending(version => new Version(version)).ToArray(),
            versions);
    }

    [Fact]
    public void AppChangelogs_OnlyCarryVersionsReleasedByTheServer()
    {
        Dictionary<string, string> server = ReleasedVersions(OnDisk(ServerFile));

        foreach (string file in new[] { HmiFile, DesignerFile })
        {
            Dictionary<string, string> app = ReleasedVersions(OnDisk(file));
            Assert.NotEmpty(app);

            foreach ((string version, string date) in app)
            {
                Assert.True(server.ContainsKey(version), $"{file} carries version {version}, which {ServerFile} does not have");
                Assert.Equal(server[version], date);
            }

            // An app may trail the product version — it just may not lead it.
            Assert.True(Newest(app.Keys) <= Newest(server.Keys), $"{file} leads {ServerFile}");
        }
    }

    [Fact]
    public void ShippedNotes_MatchTheRepositoryFile()
    {
        Assert.Equal(OnDisk(ServerFile), ChangelogContent.Markdown);
        Assert.Equal(ChangelogContent.ParseLatestVersion(OnDisk(ServerFile)), ChangelogContent.LatestVersion);

        AssertEmbedded(HmiFile, typeof(OpcBridge.Hmi.Views.MainWindow).Assembly, "OpcBridge.Hmi.CHANGELOG.md");
        AssertEmbedded(DesignerFile, typeof(OpcBridge.Hmi.Designer.Views.DesignerWindow).Assembly, "OpcBridge.Hmi.Designer.CHANGELOG.md");
    }

    private static void AssertEmbedded(string relativePath, Assembly assembly, string resourceName)
    {
        string markdown = ReleaseNotes.Load(assembly, resourceName);

        Assert.Equal(OnDisk(relativePath), markdown);
        Assert.Equal(ReleaseNotes.ParseLatestVersion(OnDisk(relativePath)), ReleaseNotes.ParseLatestVersion(markdown));
    }

    [Fact]
    public void ChangelogVersion_MatchesTheReportedApplicationVersion()
    {
        string changelogVersion = ChangelogContent.ParseLatestVersion(OnDisk(ServerFile));
        Assert.False(string.IsNullOrEmpty(changelogVersion), "CHANGELOG.md has no released version section");

        // Exactly what /api/app-info reports to the dashboard.
        AppInfoSnapshot info = AppInfoSnapshot.CreateCurrent();
        Assert.Equal(changelogVersion, info.InformationalVersion.Split('+')[0]);
        Assert.StartsWith(changelogVersion, info.Version, StringComparison.Ordinal);

        // And the raw assembly version (Major.Minor.Build).
        Version assemblyVersion = typeof(AppInfoSnapshot).Assembly.GetName().Version!;
        Assert.Equal(changelogVersion, $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");
    }

    [Fact]
    public void EveryApp_ReportsTheSharedVersion()
    {
        string changelogVersion = ChangelogContent.ParseLatestVersion(OnDisk(ServerFile));

        // The same version names all three apps (Directory.Build.props), which is what the
        // release-notes window title and the installer carry.
        Assert.Equal(changelogVersion, ReleaseNotes.InformationalVersion(typeof(AppInfoSnapshot).Assembly));
        Assert.Equal(changelogVersion, ReleaseNotes.InformationalVersion(typeof(OpcBridge.Hmi.Views.MainWindow).Assembly));
        Assert.Equal(changelogVersion, ReleaseNotes.InformationalVersion(typeof(OpcBridge.Hmi.Designer.Views.DesignerWindow).Assembly));
    }

    [Fact]
    public void CurrentRelease_DocumentsTheSignInFeature()
    {
        string markdown = OnDisk(ServerFile);
        int baseline = markdown.IndexOf("## [1.0.0]", StringComparison.Ordinal);
        Assert.True(baseline > 0, "the 1.0.0 baseline section is missing");
        string current = markdown[..baseline];

        Assert.Contains("Viewer", current, StringComparison.Ordinal);
        Assert.Contains("Operator", current, StringComparison.Ordinal);
        Assert.Contains("Engineer", current, StringComparison.Ordinal);
        Assert.Contains("Admin", current, StringComparison.Ordinal);
        Assert.Contains("PBKDF2", current, StringComparison.Ordinal);
        Assert.Contains("### Security", current, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_ShipsTheReleaseNotesView()
    {
        string html = DashboardPage.FullHtml;

        Assert.Contains("id=\"view-changelog\"", html, StringComparison.Ordinal);
        Assert.Contains("help/release-notes", html, StringComparison.Ordinal);
        Assert.Contains("loadChangelog", html, StringComparison.Ordinal);
        Assert.Contains("/api/changelog", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVersionHeadings_ParseAsEmpty()
    {
        Assert.Equal(string.Empty, ChangelogContent.ParseLatestVersion("## [Unreleased]\n\n- nothing yet"));
        Assert.Equal("2.13.4", ChangelogContent.ParseLatestVersion("## [Unreleased]\n\n## [2.13.4] - 2030-01-01"));
        Assert.Equal(string.Empty, ReleaseNotes.ParseLatestVersion("## [Unreleased]\n\n- nothing yet"));
    }
}

/// <summary>Route wiring for the release-notes endpoint (the content itself is covered above).</summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class ChangelogApiTests
{
    [Fact]
    public async Task ChangelogEndpoint_ServesTheEmbeddedNotesAndVersion()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });

        using JsonDocument document = await handle.GetJsonAsync("/api/changelog");
        string markdown = document.RootElement.GetProperty("markdown").GetString()!;

        Assert.Contains("# Changelog", markdown, StringComparison.Ordinal);
        Assert.Equal(ChangelogContent.LatestVersion, document.RootElement.GetProperty("version").GetString());
    }
}
