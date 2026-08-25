using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The source default update rate is FIXED at 1000 ms (1 s): every normalization and
/// mutation path clamps to it. Rate customization belongs to PLC Groups (or legacy
/// per-tag PollRateMs) — decision: fixed 1 s source default.
/// </summary>
public sealed class FixedUpdateRateTests : IDisposable
{
    private const int Fixed = DaRuntimeSettings.FixedUpdateRateMs;

    private readonly string _sourcesJsonPath;

    public FixedUpdateRateTests()
    {
        _sourcesJsonPath = Path.Combine(AppContext.BaseDirectory, "sources.json");
        if (File.Exists(_sourcesJsonPath))
        {
            File.Delete(_sourcesJsonPath);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_sourcesJsonPath))
        {
            File.Delete(_sourcesJsonPath);
        }
    }

    private static DaRuntimeSettings CreateSettings()
    {
        return new DaRuntimeSettings(Options.Create(new DaClientOptions()));
    }

    private static (DaRuntimeSettings Settings, string SourceId) CreateWithMxSource()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.UpsertSource(new DaSourceRuntimeSettings(
            "mx1",
            "MX1",
            SourceTypes.MxComponent,
            1000,
            true,
            50000,
            OpcDa: null,
            OpcUa: null,
            Melsec: null,
            S7200: null,
            MxComponent: new MxComponentSourceOptions(0, 3000, 2)));
        return (settings, "mx1");
    }

    [Fact]
    public void FixedUpdateRateMs_Is1000()
    {
        Assert.Equal(1000, Fixed);
    }

    [Fact]
    public void SetUpdateRate_ClampsAnyValueToFixed()
    {
        DaRuntimeSettings settings = CreateSettings();
        foreach (int requested in new[] { 0, -5, 1, 100, 500, 2500, 60000 })
        {
            DaRuntimeSettingsSnapshot snapshot = settings.SetUpdateRate(requested);
            Assert.Equal(Fixed, snapshot.UpdateRateMs);
        }
    }

    [Fact]
    public void SetSourceUpdateRate_ClampsAnyValueToFixed()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        DaRuntimeSettingsSnapshot snapshot = settings.SetSourceUpdateRate(sourceId, 500);
        Assert.Equal(Fixed, snapshot.GetSource(sourceId)!.UpdateRateMs);
        Assert.Equal(Fixed, snapshot.UpdateRateMs);
    }

    [Fact]
    public void FromDto_NormalizesStoredRatesToFixed_AndRoundTripsAsFixed()
    {
        SourceConfigDto dto = new()
        {
            SourceId = "mx1",
            SourceType = SourceTypes.MxComponent,
            UpdateRateMs = 250,
            MxComponent = new MxComponentSourceOptionsDto { LogicalStationNumber = 0, TimeoutMs = 3000, RetryCount = 2 }
        };

        DaSourceRuntimeSettings restored = SourceConfigMigration.FromDto(dto, 1000);
        Assert.Equal(Fixed, restored.UpdateRateMs);

        SourceConfigDto back = SourceConfigMigration.ToDto(restored);
        Assert.Equal(Fixed, back.UpdateRateMs);
    }
}

/// <summary>Endpoint contract: strict rejection with an operator-readable message.</summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class FixedUpdateRateApiTests
{
    private static void WriteMinimalAppsettings(string dir)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new
            {
                ApplicationName = "OpcBridge",
                EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge",
                AutoAcceptUntrustedCertificates = true,
                RequireAuthentication = false,
                Username = "",
                Password = "",
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
        string sourcesPath = Path.Combine(dir, "sources.json");
        if (File.Exists(sourcesPath))
        {
            File.Delete(sourcesPath);
        }
    }

    private static StringContent JsonBody(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static async Task SeedMxSourceAsync(TestAppHandle handle, string sourceId)
    {
        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId,
                displayName = "MX " + sourceId,
                sourceType = "MxComponent",
                logicalStationNumber = 3,
                timeoutMs = 3000,
                retryCount = 2,
                maxMappedTags = 500,
                updateRateMs = 1000
            }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task GlobalRateEndpoint_RejectsNon1000_Accepts1000()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));

        using HttpResponseMessage rejected = await handle.Client.PostAsync(
            "/api/da/update-rate", JsonBody(new { updateRateMs = 600 }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        string body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("fixed at 1000 ms", body);

        using HttpResponseMessage ok = await handle.Client.PostAsync(
            "/api/da/update-rate", JsonBody(new { updateRateMs = 1000 }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task PerSourceRateEndpoint_RejectsNon1000_AndStoresFixed()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedMxSourceAsync(handle, "mx1");

        using HttpResponseMessage rejected = await handle.Client.PostAsync(
            "/api/da/sources/update-rate", JsonBody(new { sourceId = "mx1", updateRateMs = 250 }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        string body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("fixed at 1000 ms", body);

        using HttpResponseMessage ok = await handle.Client.PostAsync(
            "/api/da/sources/update-rate", JsonBody(new { sourceId = "mx1", updateRateMs = 1000 }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using HttpResponseMessage sources = await handle.Client.GetAsync("/api/da/sources");
        string src = await sources.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(src);
        int stored = doc.RootElement.GetProperty("sources").EnumerateArray()
            .First(s => s.GetProperty("sourceId").GetString() == "mx1")
            .GetProperty("updateRateMs").GetInt32();
        Assert.Equal(1000, stored);
    }

    [Fact]
    public async Task SourceUpsert_IgnoresRequestedRate_AndStoresFixed()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedMxSourceAsync(handle, "mx1");

        // Re-upsert the same source asking for a different rate — must store fixed 1000.
        using HttpResponseMessage up = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "mx1",
                displayName = "MX mx1",
                sourceType = "MxComponent",
                logicalStationNumber = 3,
                timeoutMs = 3000,
                retryCount = 2,
                maxMappedTags = 500,
                updateRateMs = 5000
            }));
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        using HttpResponseMessage sources = await handle.Client.GetAsync("/api/da/sources");
        string src = await sources.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(src);
        int stored = doc.RootElement.GetProperty("sources").EnumerateArray()
            .First(s => s.GetProperty("sourceId").GetString() == "mx1")
            .GetProperty("updateRateMs").GetInt32();
        Assert.Equal(1000, stored);
    }
}
