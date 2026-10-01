using OpcBridge.Client;

namespace OpcBridge.App;

/// <summary>
/// The server's issues record: its ISSUES.md is embedded at build time (see
/// OpcBridge.App.csproj) and served at <c>GET /api/issues</c>, which the dashboard renders
/// under Ops ▸ Issues for Admin users. One copy means the repository file, the served text
/// and the released version cannot drift apart.
/// </summary>
internal static class IssuesContent
{
    private const string ResourceName = "OpcBridge.App.ISSUES.md";

    private static readonly Lazy<string> Markdown_ =
        new(() => ReleaseNotes.Load(typeof(IssuesContent).Assembly, ResourceName));

    public static string Markdown => Markdown_.Value;
}
