using System.Text;
using Microsoft.Extensions.Logging;

namespace OpcBridge.Logic;

/// <summary>
/// Keeps the installer's Start Menu "OpcBridge Logic" shortcut pointed at the port this run
/// bound.
/// <para>
/// The MSI writes the shortcut with the port it was built with (8090, via the
/// <c>LogicPort</c> build define) and cannot know the runtime one, so a host where the
/// default was taken kept a Start Menu entry aimed at a port nothing listens on. The app is
/// the only party that knows the port it actually got, so startup rewrites just the URL
/// value: the <c>[InternetShortcut]</c> header, the icon the installer pinned and any
/// property storage after it stay byte-for-byte intact.
/// </para>
/// <para>
/// The shortcut is rewritten in place, never created or deleted — the file belongs to the
/// MSI (component <c>LogicShortcut</c>, <c>packaging/msi/OpcBridge.Logic.wxs</c>), which also
/// removes it on uninstall. A run from a publish folder leaves it alone.
/// </para>
/// </summary>
public static class LogicShortcut
{
    /// <summary>Start Menu folder the MSI creates (<c>ProgramMenuFolder\OpcBridge</c>).</summary>
    public const string MenuSubFolder = "OpcBridge";

    /// <summary>Shortcut file name the MSI creates (<c>util:InternetShortcut</c>, <c>Type="url"</c>).</summary>
    public const string FileName = "OpcBridge Logic.url";

    private const string UrlKey = "URL=";
    private const string Header = "[InternetShortcut]";

    /// <summary>
    /// Path of the all-users Start Menu shortcut, or null when this host cannot have one
    /// (a non-Windows build, i.e. the container and the test host).
    /// </summary>
    public static string? DefaultPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        return string.IsNullOrEmpty(programs)
            ? null
            : Path.Combine(programs, MenuSubFolder, FileName);
    }

    /// <summary>
    /// Replaces the URL value of an Internet shortcut document, leaving every other line —
    /// the <c>[InternetShortcut]</c> header, the installer's <c>IconFile</c>/<c>IconIndex</c>,
    /// any property storage after them — untouched. Returns null when <paramref name="content"/>
    /// is not an Internet shortcut document or has no URL line to replace.
    /// </summary>
    public static string? ReplaceUrl(string? content, string url)
    {
        if (string.IsNullOrEmpty(content)
            || content.IndexOf(Header, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        int index = IndexOfUrlKey(content);
        if (index < 0)
        {
            return null;
        }

        int valueStart = index + UrlKey.Length;
        int valueEnd = content.IndexOfAny(['\r', '\n'], valueStart);
        if (valueEnd < 0)
        {
            valueEnd = content.Length;
        }

        return content[..valueStart] + url + content[valueEnd..];
    }

    /// <summary>
    /// Points the installed shortcut at <paramref name="httpPort"/>. Best effort by design:
    /// this runs during startup and must never stop the app from coming up, so every failure
    /// is a log line, and a missing shortcut (publish folder) is not one.
    /// </summary>
    public static void Update(int httpPort, ILogger logger)
    {
        string? path = DefaultPath();
        if (path is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                logger.LogDebug("No Start Menu Logic shortcut to update at {Path}.", path);
                return;
            }

            // Latin-1 round-trips every byte, so the property storage the installer appends
            // to the .url (the pinned icon) survives the read/patch/write untouched.
            string content = File.ReadAllText(path, Encoding.Latin1);
            string? updated = ReplaceUrl(content, $"http://localhost:{httpPort}/");
            if (updated is null)
            {
                logger.LogDebug("Start Menu Logic shortcut at {Path} is not an Internet shortcut document; left unchanged.", path);
                return;
            }

            if (string.Equals(updated, content, StringComparison.Ordinal))
            {
                return;
            }

            File.WriteAllText(path, updated, Encoding.Latin1);
            logger.LogInformation("Start Menu Logic shortcut updated to http://localhost:{Port}/.", httpPort);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not update the Start Menu Logic shortcut at {Path}.", path);
        }
    }

    /// <summary>Index of the URL key when it starts a line, else -1.</summary>
    private static int IndexOfUrlKey(string content)
    {
        int index = content.IndexOf(UrlKey, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            if (index == 0 || content[index - 1] is '\r' or '\n')
            {
                return index;
            }

            index = content.IndexOf(UrlKey, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }
}
