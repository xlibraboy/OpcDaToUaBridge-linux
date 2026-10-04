using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpcBridge.App;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Release notes are per-app files, each app versioning and releasing on its own: the
/// server's CHANGELOG.md (the server's release authority, served at /api/changelog and
/// reported by the bridge) on ServerVersion, and one file per desktop app embedded in
/// its own assembly and stamped with its own version (HmiVersion, DesignerVersion).
/// These tests fail if a file, the shipped copy or the version its assembly reports
/// drift apart.
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

        // Newest first: the top released section is the version the server assembly reports.
        Assert.Equal(ReleaseNotes.InformationalVersion(typeof(AppInfoSnapshot).Assembly), released[0].Groups[1].Value);
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
    public void EachApp_ReportsItsOwnVersion()
    {
        // Each app's newest changelog section is the version its own assembly reports:
        // ServerVersion, HmiVersion and DesignerVersion in Directory.Build.props.
        AssertChangelogMatchesAssembly(ServerFile, typeof(AppInfoSnapshot).Assembly);
        AssertChangelogMatchesAssembly(HmiFile, typeof(OpcBridge.Hmi.Views.MainWindow).Assembly);
        AssertChangelogMatchesAssembly(DesignerFile, typeof(OpcBridge.Hmi.Designer.Views.DesignerWindow).Assembly);
    }

    private static void AssertChangelogMatchesAssembly(string relativePath, Assembly assembly)
    {
        string changelogVersion = ReleaseNotes.ParseLatestVersion(OnDisk(relativePath));
        Assert.False(string.IsNullOrEmpty(changelogVersion), $"{relativePath} has no released version section");

        // The version the app shows under Help ▸ Release notes and reports to callers.
        Assert.Equal(changelogVersion, ReleaseNotes.InformationalVersion(assembly));

        // And the raw assembly version (Major.Minor.Build).
        Version assemblyVersion = assembly.GetName().Version!;
        Assert.Equal(changelogVersion, $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");
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
