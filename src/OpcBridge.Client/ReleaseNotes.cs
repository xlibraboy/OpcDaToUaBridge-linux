using System.Reflection;
using System.Text.RegularExpressions;

namespace OpcBridge.Client;

/// <summary>
/// Release notes shipped inside an app's assembly: every OpcBridge app embeds its own
/// CHANGELOG.md at build time and shows it in-app (the dashboard under Help ▸ Release
/// Notes, the HMI runtime and designer under Help ▸ Release notes), so the repository
/// file, the shipped text and the released version cannot drift apart.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>Keep a Changelog version heading, e.g. "## [1.1.0] - 2026-09-23". "[Unreleased]" has no digits and is skipped.</summary>
    private static readonly Regex VersionHeading = new(
        @"^##\s+\[\s*(\d+\.\d+\.\d+)\s*\]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The embedded changelog text, normalised to \n line endings.</summary>
    public static string Load(Assembly assembly, string resourceName)
    {
        try
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return "Release notes are not available in this build.";
            }

            using StreamReader reader = new(stream);
            return reader.ReadToEnd().Replace("\r\n", "\n");
        }
        catch (Exception ex)
        {
            // Release notes are documentation: never fail a window or an endpoint over them.
            return $"Release notes could not be read: {ex.Message}";
        }
    }

    /// <summary>Newest released version in the changelog, e.g. "1.1.0". Empty when the file has no released section.</summary>
    public static string ParseLatestVersion(string markdown)
    {
        Match match = VersionHeading.Match(markdown);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>Informational version ("1.3.0", without the "+&lt;commit&gt;" suffix), or the assembly version when unset.</summary>
    public static string InformationalVersion(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            int metadataStart = informational.IndexOf('+');
            return metadataStart >= 0 ? informational[..metadataStart] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? string.Empty;
    }
}
