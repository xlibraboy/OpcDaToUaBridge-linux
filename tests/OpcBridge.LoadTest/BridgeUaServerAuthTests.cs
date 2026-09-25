using Opc.Ua;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Issue #14: the credential gate for external OPC UA clients. The comparison must
/// be exact (ordinal, no trimming) and must never accept blank/null on either side,
/// so an unconfigured or half-configured server can never authenticate anyone and
/// empty-string credentials can never match. The endpoint must also advertise what
/// it will accept: with the gate on, the anonymous token policy is gone.
/// </summary>
public sealed class BridgeUaServerAuthTests
{
    [Theory]
    [InlineData("uaclient", "s3cret", "uaclient", "s3cret", true)]
    [InlineData("uaclient", "s3cret", "uaclient", "wrong", false)]
    [InlineData("uaclient", "s3cret", "UaClient", "s3cret", false)]
    [InlineData("uaclient", "s3cret", "uaclient", "S3cret", false)]
    [InlineData("uaclient", "s3cret", " uaclient", "s3cret", false)]
    [InlineData("uaclient ", "s3cret", "uaclient", "s3cret", false)]
    [InlineData("", "s3cret", "uaclient", "s3cret", false)]
    [InlineData(null, "s3cret", "uaclient", "s3cret", false)]
    [InlineData("uaclient", "", "uaclient", "s3cret", false)]
    [InlineData("uaclient", null, "uaclient", "s3cret", false)]
    [InlineData("uaclient", "s3cret", "", "s3cret", false)]
    [InlineData("uaclient", "s3cret", "uaclient", "", false)]
    [InlineData("uaclient", "s3cret", null, null, false)]
    [InlineData("", "", "", "", false)]
    public void ValidateUserNameCredential_RequiresExactNonBlankMatch(
        string? username, string? password, string? storedUsername, string? storedPassword, bool expected)
    {
        bool actual = BridgeUaServer.ValidateUserNameCredential(username, password, storedUsername, storedPassword);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SelectUserTokenPolicies_WhenNotRequired_LeavesTheSdkDefaultsAlone()
    {
        UserTokenPolicyCollection defaults = new() { new UserTokenPolicy(UserTokenType.Anonymous) };

        UserTokenPolicyCollection actual = BridgeUaServer.SelectUserTokenPolicies(defaults, requireAuthentication: false);

        Assert.Same(defaults, actual);
        Assert.Contains(defaults, policy => policy.TokenType == UserTokenType.Anonymous);
    }

    [Fact]
    public void SelectUserTokenPolicies_WhenRequired_AdvertisesUserNameAlone()
    {
        UserTokenPolicyCollection defaults = new()
        {
            new UserTokenPolicy(UserTokenType.Anonymous),
            new UserTokenPolicy(UserTokenType.UserName) { PolicyId = "sdk-default" }
        };

        UserTokenPolicyCollection actual = BridgeUaServer.SelectUserTokenPolicies(defaults, requireAuthentication: true);

        UserTokenPolicy policy = Assert.Single(actual);
        Assert.Equal(UserTokenType.UserName, policy.TokenType);
        Assert.Equal("username", policy.PolicyId);
        // Plaintext on purpose: the endpoint's only channel policy is None, and an
        // encrypted token cannot be decrypted on that path (logins fail with a null
        // password), so Basic256Sha256 here would make the gate unusable.
        Assert.Equal(SecurityPolicies.None, policy.SecurityPolicyUri);
    }
}
