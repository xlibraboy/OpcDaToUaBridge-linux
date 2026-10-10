using Microsoft.Extensions.Logging.Abstractions;
using OpcBridge.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Start Menu Logic shortcut the MSI installs is a .url written with the port the MSI was
/// built with (8090), while the app may come up on another port when that one is taken. Every
/// start rewrites just the URL value to the port it bound; these tests pin that patch, and
/// that the rest of the file — the icon properties the installer wrote and any property
/// storage after them — survives it untouched.
/// </summary>
public sealed class LogicShortcutTests
{
    private const string InstalledShortcut =
        "[InternetShortcut]\r\n" +
        "URL=http://localhost:8090/\r\n" +
        "IconFile=C:\\Program Files\\OpcBridge Logic\\OpcBridge.Logic.exe\r\n" +
        "IconIndex=0\r\n";

    [Fact]
    public void ReplaceUrl_RepointsTheUrlAndKeepsEveryOtherLine()
    {
        string? updated = LogicShortcut.ReplaceUrl(InstalledShortcut, "http://localhost:8091/");

        Assert.Equal(
            "[InternetShortcut]\r\n" +
            "URL=http://localhost:8091/\r\n" +
            "IconFile=C:\\Program Files\\OpcBridge Logic\\OpcBridge.Logic.exe\r\n" +
            "IconIndex=0\r\n",
            updated);
    }

    [Fact]
    public void ReplaceUrl_KeepsThePropertyStorageThatFollowsTheUrl()
    {
        // A .url can carry its icon as property storage appended after the text; that tail is
        // not text and must survive the rewrite byte for byte (the app reads and writes Latin-1
        // for exactly this reason).
        const string Tail = "\u0001\u00a0\u00ff\u0000\u00feproperty storage";
        string content = "[InternetShortcut]\r\nURL=http://localhost:8090/\r\n" + Tail;

        string? updated = LogicShortcut.ReplaceUrl(content, "http://localhost:9000/");

        Assert.Equal("[InternetShortcut]\r\nURL=http://localhost:9000/\r\n" + Tail, updated);
    }

    [Fact]
    public void ReplaceUrl_HandlesLfOnlyContentWithoutATrailingNewline()
    {
        string? updated = LogicShortcut.ReplaceUrl(
            "[InternetShortcut]\nURL=http://localhost:8090/",
            "http://localhost:8092/");

        Assert.Equal("[InternetShortcut]\nURL=http://localhost:8092/", updated);
    }

    [Fact]
    public void ReplaceUrl_IsIdempotent()
    {
        // The same value back means Update() can skip the write, so a start on the port the
        // shortcut already carries does not touch the file.
        string? updated = LogicShortcut.ReplaceUrl(InstalledShortcut, "http://localhost:8090/");

        Assert.Equal(InstalledShortcut, updated);
    }

    [Theory]
    [InlineData("[InternetShortcut]\r\nIconFile=C:\\Program Files\\OpcBridge Logic\\OpcBridge.Logic.exe\r\nIconIndex=0\r\n")]
    [InlineData("not a shortcut at all, just a URL=http://localhost:8090/ mention")]
    [InlineData("")]
    public void ReplaceUrl_LeavesAnythingThatIsNotAnInternetShortcutAlone(string content)
    {
        Assert.Null(LogicShortcut.ReplaceUrl(content, "http://localhost:8091/"));
    }

    [Fact]
    public void ReplaceUrl_IgnoresUrlMentionsThatDoNotStartALine()
    {
        string content = "[InternetShortcut]\r\nIconFile=C:\\tools\\URL=dodgy.exe\r\nURL=http://localhost:8090/\r\n";

        string? updated = LogicShortcut.ReplaceUrl(content, "http://localhost:8091/");

        Assert.Equal("[InternetShortcut]\r\nIconFile=C:\\tools\\URL=dodgy.exe\r\nURL=http://localhost:8091/\r\n", updated);
    }

    [Fact]
    public void DefaultPath_IsNullOutsideWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the Windows acceptance run covers the real path
        }

        Assert.Null(LogicShortcut.DefaultPath());
    }

    [Fact]
    public void Update_IsANoOpOutsideWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Must neither throw nor touch anything: container and test-host runs take this path.
        LogicShortcut.Update(8091, NullLogger.Instance);
    }
}
