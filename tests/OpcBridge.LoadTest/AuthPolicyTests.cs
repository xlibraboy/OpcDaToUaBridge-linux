using OpcBridge.App.Auth;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The role policy table is the whole enforcement contract: reads need Viewer, any
/// mutation needs Engineer, and named endpoints override that. These tests pin the
/// table so a new endpoint cannot quietly land outside it.
/// </summary>
public sealed class AuthPolicyTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/me")]
    [InlineData("/api/app-info")]
    [InlineData("/api/version")]
    public void PublicPaths_NeedNoSession(string path) => Assert.True(AuthPolicy.IsPublic(path));

    [Theory]
    [InlineData("/api/mappings")]
    [InlineData("/api/dashboard")]
    [InlineData("/api/logs")]
    [InlineData("/api/auth/users")]
    [InlineData("/api/hmi/write")]
    public void ProtectedPaths_AreNotPublic(string path) => Assert.False(AuthPolicy.IsPublic(path));

    [Theory]
    [InlineData("/hmi")]
    [InlineData("/hmi/negotiate")]
    [InlineData("/api/values")]
    [InlineData("/api/hmi/tags")]
    [InlineData("/api/hmi/trends")]
    [InlineData("/api/hmi/displays")]
    [InlineData("/api/hmi/displays/line-1")]
    [InlineData("/api/hmi/write")]
    public void HmiPaths_AreRecognised(string path) => Assert.True(AuthPolicy.IsHmiPath(path));

    [Theory]
    [InlineData("/api/hmi/tagsFoo")] // prefix must not leak across a segment boundary
    [InlineData("/api/hmi")]
    [InlineData("/api/mappings")]
    public void NonHmiPaths_AreNotHmi(string path) => Assert.False(AuthPolicy.IsHmiPath(path));

    [Theory]
    [InlineData("GET", "/api/mappings", UserRole.Viewer)]
    [InlineData("GET", "/api/dashboard", UserRole.Viewer)]
    [InlineData("GET", "/api/da/sources", UserRole.Viewer)]
    [InlineData("GET", "/api/auth/users", UserRole.Admin)]
    [InlineData("POST", "/api/auth/users", UserRole.Admin)]
    [InlineData("POST", "/api/auth/users/remove", UserRole.Admin)]
    [InlineData("POST", "/api/session/resolve", UserRole.Admin)]
    [InlineData("POST", "/api/hmi/write", UserRole.Operator)]
    [InlineData("PUT", "/api/hmi/displays/line-1", UserRole.Engineer)]
    [InlineData("DELETE", "/api/hmi/displays/line-1", UserRole.Engineer)]
    [InlineData("POST", "/api/mappings/add", UserRole.Engineer)]
    [InlineData("POST", "/api/da/sources", UserRole.Engineer)]
    [InlineData("POST", "/api/mqtt/config", UserRole.Engineer)]
    [InlineData("POST", "/api/influx/config", UserRole.Engineer)]
    [InlineData("POST", "/api/config/import", UserRole.Engineer)]
    public void RequiredRole_MatchesTheEndpointTable(string method, string path, UserRole expected) =>
        Assert.Equal(expected, AuthPolicy.RequiredRole(method, path));

    [Fact]
    public void RoleHierarchy_IsOrdered()
    {
        Assert.True(UserRoles.TryParse("admin", out UserRole admin));
        Assert.True(UserRoles.TryParse("ENGINEER", out UserRole engineer));
        Assert.True(UserRoles.TryParse("Operator", out UserRole op));
        Assert.True(UserRoles.TryParse("viewer", out UserRole viewer));

        Assert.True((int)admin > (int)engineer);
        Assert.True((int)engineer > (int)op);
        Assert.True((int)op > (int)viewer);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("None")]
    [InlineData("Superuser")]
    public void UnknownRoleText_IsRejected(string? text) => Assert.False(UserRoles.TryParse(text, out _));
}
