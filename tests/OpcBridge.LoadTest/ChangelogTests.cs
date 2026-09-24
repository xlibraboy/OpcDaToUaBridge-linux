using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Release notes are one artifact with three faces: CHANGELOG.md in the repository,
/// the embedded copy served at /api/changelog, and the version the app reports. These
/// tests fail if any of the three drifts.
/// </summary>
public sealed class ChangelogTests
{
    private static readonly Regex ReleasedSection = new(
        @"^##\s+\[\s*(\d+\.\d+\.\d+)\s*\]\s+-\s+(\d{4}-\d{2}-\d{2})\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

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
        File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md")).Replace("\r\n", "\n");

    [Fact]
    public void Changelog_FollowsKeepAChangelog()
    {
        string markdown = OnDisk();

        Assert.StartsWith("# Changelog", markdown, StringComparison.Ordinal);
        Assert.Contains("Keep a Changelog", markdown, StringComparison.Ordinal);
        Assert.Contains("Semantic Versioning", markdown, StringComparison.Ordinal);
        Assert.Contains("## [Unreleased]", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReleasedSection_CarriesAVersionAndDate()
    {
        MatchCollection released = ReleasedSection.Matches(OnDisk());

        Assert.True(released.Count >= 2, $"expected at least the 1.0.0 baseline and the current release, got {released.Count}");

        // Newest first: the top released section is the current version.
        Assert.Equal("1.3.0", released[0].Groups[1].Value);
        Assert.Equal("1.0.0", released[^1].Groups[1].Value);
    }

    [Fact]
    public void ShippedNotes_MatchTheRepositoryFile()
    {
        Assert.Equal(OnDisk(), ChangelogContent.Markdown);
        Assert.Equal(ChangelogContent.ParseLatestVersion(OnDisk()), ChangelogContent.LatestVersion);
    }

    [Fact]
    public void ChangelogVersion_MatchesTheReportedApplicationVersion()
    {
        string changelogVersion = ChangelogContent.ParseLatestVersion(OnDisk());
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
        string markdown = OnDisk();
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
