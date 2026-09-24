using OpcBridge.Client;

namespace OpcBridge.App;

/// <summary>
/// The server's release notes: its CHANGELOG.md is embedded at build time (see
/// OpcBridge.App.csproj) and served at <c>GET /api/changelog</c>, which the dashboard
/// renders under Help ▸ Release Notes. One copy means the repository file, the served
/// text and the released version cannot drift apart. The loading and parsing live in
/// <see cref="ReleaseNotes"/> so the HMI runtime and designer can ship theirs the same
/// way.
/// </summary>
internal static class ChangelogContent
{
    private const string ResourceName = "OpcBridge.App.CHANGELOG.md";

    private static readonly Lazy<string> Markdown_ =
        new(() => ReleaseNotes.Load(typeof(ChangelogContent).Assembly, ResourceName));

    private static readonly Lazy<string> LatestVersion_ = new(() => ParseLatestVersion(Markdown_.Value));

    public static string Markdown => Markdown_.Value;

    /// <summary>Newest released version in the changelog, e.g. "1.1.0". Empty when the file has no released section.</summary>
    public static string LatestVersion => LatestVersion_.Value;

    internal static string ParseLatestVersion(string markdown) => ReleaseNotes.ParseLatestVersion(markdown);
}
