using System.Text.Json;
using OpcBridge.Hmi.Designer.Services;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Designer's bridge client against a real test host: source listing, the auth wall, and
/// sign-in (the Designer's session cookie must carry to later calls).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class DesignerBridgeClientTests
{
    internal static void WriteAppsettings(string dir, bool authEnabled = false)
    {
        var appsettings = new
        {
            Da = new
            {
                SourceId = "line1",
                DisplayName = "Line 1",
                ProgId = "Matrikon.OPC.Simulation.1",
                Host = "localhost",
                UpdateRateMs = 1000,
                UseSubscriptions = true
            },
            Ua = new
            {
                ApplicationName = "OpcDaToUaBridge",
                EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge",
                AutoAcceptUntrustedCertificates = true,
                RequireAuthentication = false,
                Username = "",
                Password = "",
                AllowedIpAddresses = Array.Empty<string>()
            },
            Bridge = new
            {
                RateLimits = new { },
                ExpectedTagCount = 10,
                Mappings = new object[]
                {
                    new
                    {
                        SourceId = "line1",
                        ItemId = "Random.Int1",
                        DisplayName = "Int1",
                        DataType = "Int32",
                        UaNodeId = "",
                        Enabled = true,
                        Mode = "Source",
                        Writeable = true,
                        AccessRights = "Read-Write"
                    }
                }
            },
            Mqtt = new
            {
                Enabled = false,
                BrokerUrl = "tcp://localhost:1883",
                ClientId = "OpcDaToUaBridge",
                UserName = (string?)null,
                Password = (string?)null,
                Tls = false,
                IgnoreCertErrors = false,
                TopicPrefix = "bridge/tags",
                PayloadFields = "Value, Timestamp"
            },
            Hmi = new { BroadcastFlushMs = 100 },
            Auth = new { Enabled = authEnabled, SessionHours = 12, IdleMinutes = 30, TrustHmi = true }
        };
        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true }));
        string mapPath = Path.Combine(dir, "mappings.json");
        if (File.Exists(mapPath))
        {
            File.Delete(mapPath);
        }
    }

    [Fact]
    public async Task Sources_ListsConfiguredSource()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAppsettings(dir));
        using var client = new DesignerBridgeClient();
        client.SetBaseAddress(handle.Client.BaseAddress!.ToString());

        SourceListResult result = await client.GetSourcesAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        BridgeSourceInfo source = Assert.Single(result.Sources);
        Assert.Equal("line1", source.SourceId);
        Assert.Equal("Line 1", source.DisplayName);
        Assert.Equal("OpcDa", source.SourceType);
        Assert.Equal("DA", source.TypeLabel);
        Assert.Equal("Matrikon.OPC.Simulation.1 @ localhost", source.EndpointSummary);
    }

    [Fact]
    public async Task Sources_RequireSignIn_ThenUnlockWithASession()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(
            dir => WriteAppsettings(dir, authEnabled: true));
        using var client = new DesignerBridgeClient();
        client.SetBaseAddress(handle.Client.BaseAddress!.ToString());

        SourceListResult anonymous = await client.GetSourcesAsync(CancellationToken.None);
        Assert.False(anonymous.Ok);
        Assert.True(anonymous.AuthRequired);

        (bool wrongOk, string? wrongError) = await client.SignInAsync("admin", "not-the-password", CancellationToken.None);
        Assert.False(wrongOk);
        Assert.False(string.IsNullOrWhiteSpace(wrongError));

        (bool ok, string? error) = await client.SignInAsync("admin", "admin", CancellationToken.None);
        Assert.True(ok, error);

        SourceListResult signedIn = await client.GetSourcesAsync(CancellationToken.None);
        Assert.True(signedIn.Ok, signedIn.Error);
        Assert.Contains(signedIn.Sources, source => source.SourceId == "line1");
    }

    [Fact]
    public async Task AuthInfo_ReportsTheSignedInUser()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(
            dir => WriteAppsettings(dir, authEnabled: true));
        using var client = new DesignerBridgeClient();
        client.SetBaseAddress(handle.Client.BaseAddress!.ToString());

        BridgeAuthInfo? before = await client.GetAuthInfoAsync(CancellationToken.None);
        Assert.NotNull(before);
        Assert.True(before!.AuthEnabled);
        Assert.False(before.Authenticated);

        (bool ok, string? error) = await client.SignInAsync("admin", "admin", CancellationToken.None);
        Assert.True(ok, error);

        BridgeAuthInfo? after = await client.GetAuthInfoAsync(CancellationToken.None);
        Assert.NotNull(after);
        Assert.True(after!.Authenticated);
        Assert.Equal("admin", after.Username);
    }

    [Fact]
    public async Task Sources_ReportAuthAsOptional_WhenAuthIsDisabled()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteAppsettings(dir));
        using var client = new DesignerBridgeClient();
        client.SetBaseAddress(handle.Client.BaseAddress!.ToString());

        BridgeAuthInfo? info = await client.GetAuthInfoAsync(CancellationToken.None);
        Assert.NotNull(info);
        Assert.False(info!.AuthEnabled);
    }
}
