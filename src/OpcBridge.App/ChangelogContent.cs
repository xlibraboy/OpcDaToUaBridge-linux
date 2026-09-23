using System.Reflection;
using System.Text.RegularExpressions;

namespace OpcBridge.App;

/// <summary>
/// The release notes shipped inside the assembly: CHANGELOG.md is embedded at build
/// time (see OpcBridge.App.csproj) and served at <c>GET /api/changelog</c>, which the
/// dashboard renders under Help ▸ Release Notes. One copy means the repository file,
/// the served text and the released version cannot drift apart.
/// </summary>
internal static class ChangelogContent
{
    private const string ResourceName = "OpcBridge.App.CHANGELOG.md";

    /// <summary>Keep a Changelog version heading, e.g. "## [1.1.0] - 2026-09-23". "[Unreleased]" has no digits and is skipped.</summary>
    private static readonly Regex VersionHeading = new(
        @"^##\s+\[\s*(\d+\.\d+\.\d+)\s*\]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Lazy<string> Markdown_ = new(Load);
    private static readonly Lazy<string> LatestVersion_ = new(() => ParseLatestVersion(Markdown_.Value));

    public static string Markdown => Markdown_.Value;

    /// <summary>Newest released version in the changelog, e.g. "1.1.0". Empty when the file has no released section.</summary>
    public static string LatestVersion => LatestVersion_.Value;

    internal static string ParseLatestVersion(string markdown)
    {
        Match match = VersionHeading.Match(markdown);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string Load()
    {
        try
        {
            Assembly assembly = typeof(ChangelogContent).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return "Release notes are not available in this build.";
            }

            using StreamReader reader = new(stream);
            return reader.ReadToEnd().Replace("\r\n", "\n");
        }
        catch (Exception ex)
        {
            // Release notes are documentation: never fail the endpoint over them.
            return $"Release notes could not be read: {ex.Message}";
        }
    }
}
