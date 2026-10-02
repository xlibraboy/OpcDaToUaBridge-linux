using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class SecretProtectorTests
{
    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        string? stored = SecretProtector.Protect("s3cret");

        Assert.Equal("s3cret", SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void Protect_IsIdempotent()
    {
        string? once = SecretProtector.Protect("s3cret");

        Assert.Equal(once, SecretProtector.Protect(once));
    }

    [Fact]
    public void Unprotect_Plaintext_PassesThrough()
    {
        Assert.Equal("plain", SecretProtector.Unprotect("plain"));
    }

    [Fact]
    public void Unprotect_MalformedProtectedValue_ReturnsNull()
    {
        Assert.Null(SecretProtector.Unprotect(SecretProtector.Prefix + "not-base64!"));
    }

    [Fact]
    public void Protect_Empty_ReturnsEmpty()
    {
        Assert.Null(SecretProtector.Protect(null));
        Assert.Equal(string.Empty, SecretProtector.Protect(string.Empty));
    }

    [Fact]
    public void OnNonWindows_Protect_LeavesTheValueAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("s3cret", SecretProtector.Protect("s3cret"));
        Assert.False(SecretProtector.IsProtected("s3cret"));
    }
}
