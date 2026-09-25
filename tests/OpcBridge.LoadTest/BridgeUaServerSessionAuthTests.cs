using System.Text.Json;
using OpcBridge.Da;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Issue #14 end to end: a real OPC UA client session against the bridge's own
/// server. The unit tests pin the comparison and the advertised policy set; this
/// proves the stack-level flow — the credentials an operator types into the
/// dashboard card actually activate a session, and an anonymous or
/// wrong-password client never gets one.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class BridgeUaServerSessionAuthTests
{
    private const string ConfiguredUsername = "uaclient";
    private const string ConfiguredPassword = "s3cret";

    private static void WriteAppsettings(string dir, bool requireAuthentication)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new
            {
                ApplicationName = "OpcBridge",
                EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge",
                AutoAcceptUntrustedCertificates = true,
                RequireAuthentication = requireAuthentication,
                Username = ConfiguredUsername,
                Password = ConfiguredPassword,
                AllowedIpAddresses = Array.Empty<string>()
            },
            Bridge = new { RateLimits = new { }, ExpectedTagCount = 100, Mappings = Array.Empty<object>() },
            Mqtt = new
            {
                Enabled = false,
                BrokerUrl = "tcp://localhost:1883",
                ClientId = "OpcBridge",
                UserName = (string?)null,
                Password = (string?)null,
                Tls = false,
                IgnoreCertErrors = false,
                TopicPrefix = "bridge/tags",
                PayloadFields = "Value, Timestamp"
            }
        };

        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, "mappings.json"), "[]");
    }

    private static OpcUaSourceClient CreateClient(TestAppHandle handle, string? username, string? password)
    {
        return new OpcUaSourceClient(new OpcUaSourceClientOptions
        {
            SourceId = "auth-probe",
            ApplicationName = "OpcBridge.AuthProbe",
            EndpointUrl = $"opc.tcp://127.0.0.1:{handle.UaPort}/OpcBridge",
            SecurityMode = "None",
            SecurityPolicy = "None",
            Username = username,
            Password = password,
            // A per-test PKI root keeps parallel app hosts from racing on one cert store.
            PkiRoot = "pki/ua-client-" + Guid.NewGuid().ToString("N")
        });
    }

    /// <summary>
    /// Connects the way a real client does — retrying — because the app reports its UA
    /// server as Running before the endpoint actually serves requests (the wait for the
    /// first attempt would otherwise measure that race, not the credential gate).
    /// </summary>
    private static async Task ConnectWithRetryAsync(TestAppHandle handle, string? username, string? password)
    {
        await using OpcUaSourceClient client = CreateClient(handle, username, password);

        SourceConnectionLostException? lastFailure = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await client.ConnectAsync(CancellationToken.None);
                return;
            }
            catch (SourceConnectionLostException ex)
            {
                lastFailure = ex;
                await Task.Delay(250);
            }
        }

        throw lastFailure!;
    }

    [Fact]
    public async Task ExternalClient_AnonymousAndWrongPassword_AreRefused_CorrectCredentialsConnect()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(
            dir => WriteAppsettings(dir, requireAuthentication: true));

        // The credentials connect first, so the refusals below cannot be a server that
        // was not up yet.
        await ConnectWithRetryAsync(handle, ConfiguredUsername, ConfiguredPassword);

        // Anonymous dies on the client: with the gate on the endpoint advertises no
        // anonymous policy, so a client never gets as far as sending an empty identity.
        await using (OpcUaSourceClient anonymous = CreateClient(handle, username: null, password: null))
        {
            SourceConnectionLostException failure = await Assert.ThrowsAsync<SourceConnectionLostException>(
                () => anonymous.ConnectAsync(CancellationToken.None));
            Assert.Contains("identity", failure.Message, StringComparison.OrdinalIgnoreCase);
        }

        // A wrong password gets as far as the server, which rejects it outright.
        await using (OpcUaSourceClient wrongPassword = CreateClient(handle, ConfiguredUsername, "wrong-password"))
        {
            SourceConnectionLostException failure = await Assert.ThrowsAsync<SourceConnectionLostException>(
                () => wrongPassword.ConnectAsync(CancellationToken.None));
            Assert.Contains("BadIdentityTokenInvalid", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExternalClient_AnonymousConnects_WhenTheGateIsOff()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(
            dir => WriteAppsettings(dir, requireAuthentication: false));

        await ConnectWithRetryAsync(handle, username: null, password: null);
    }
}
