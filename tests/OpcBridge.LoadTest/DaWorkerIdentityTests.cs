using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaWorkerIdentityTests
{
    [Fact]
    public void NormalizeAccountName_QualifiesLocalAccounts()
    {
        Assert.Equal(Environment.MachineName + "\\mesadm1", DaWorkerIdentity.NormalizeAccountName(".\\mesadm1", null));
        Assert.Equal(Environment.MachineName + "\\mesadm1", DaWorkerIdentity.NormalizeAccountName("mesadm1", null));
        Assert.Equal("DOMAIN\\opcu1", DaWorkerIdentity.NormalizeAccountName("opcu1", "DOMAIN"));
        Assert.Equal("DOMAIN\\opcu1", DaWorkerIdentity.NormalizeAccountName("DOMAIN\\opcu1", null));
        Assert.Equal("DOMAIN\\opcu1", DaWorkerIdentity.NormalizeAccountName(" DOMAIN\\opcu1 ", null));
    }

    [Fact]
    public void QuoteArgument_UsesTheWindowsCommandLineRules()
    {
        Assert.Equal("plain", DaWorkerIdentity.QuoteArgument("plain"));
        Assert.Equal("\"a b\"", DaWorkerIdentity.QuoteArgument("a b"));
        Assert.Equal("\"C:\\Program Files\\x\"", DaWorkerIdentity.QuoteArgument("C:\\Program Files\\x"));
        Assert.Equal("\"a\\\"b\"", DaWorkerIdentity.QuoteArgument("a\"b"));
        Assert.Equal("\"C:\\Program Files\\\\\"", DaWorkerIdentity.QuoteArgument("C:\\Program Files\\"));
    }

    [Fact]
    public void Start_OnNonWindows_ThrowsPlatformUnsupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() => DaWorkerIdentity.Start(
            "dotnet",
            new[] { "OpcBridge.App.dll", "--da-worker" },
            new DaWorkerOptions(DaWorkerModes.Own, ".\\someone-else", "pw")));
    }

    [Fact]
    public void Start_RunAsWithoutPassword_IsRejectedBeforeSpawning()
    {
        // Platform-independent: a different account with no password can never log on, so the
        // check must fire before any spawn is attempted (also on the Linux test host).
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => DaWorkerIdentity.Start(
            "dotnet",
            new[] { "OpcBridge.App.dll", "--da-worker" },
            new DaWorkerOptions(DaWorkerModes.Own, ".\\someone-else", null)));

        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CurrentAccountName_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(DaWorkerIdentity.CurrentAccountName()));
    }
}
