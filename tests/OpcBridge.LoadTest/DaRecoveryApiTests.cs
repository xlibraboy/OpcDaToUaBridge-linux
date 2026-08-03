using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(DaLinkApiAppCollection))]
public sealed class DaRecoveryApiTests
{
    [Fact]
    public async Task PostSource_WithRecoveryFields_RoundTrips()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "recover1",
                progId = "Matrikon.OPC.Simulation.1",
                host = "localhost",
                maxConsecutiveFailures = 4,
                watchdogTimeoutMs = 120000
            }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using JsonDocument get = await app.GetJsonAsync("/api/da/sources");
        JsonElement source = Single(get.RootElement.GetProperty("sources"), "recover1");
        Assert.Equal(4, source.GetProperty("maxConsecutiveFailures").GetInt32());
        Assert.Equal(120000, source.GetProperty("watchdogTimeoutMs").GetInt32());
    }

    [Fact]
    public async Task PostSource_RecoveryFieldsOmitted_UsesDefaults()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage post = await app.Client.PostAsync(
            "/api/da/sources",
            Json(new
            {
                sourceId = "recover2",
                progId = "Matrikon.OPC.Simulation.1",
                host = "localhost"
            }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using JsonDocument get = await app.GetJsonAsync("/api/da/sources");
        JsonElement source = Single(get.RootElement.GetProperty("sources"), "recover2");
        Assert.Equal(1, source.GetProperty("maxConsecutiveFailures").GetInt32());
        Assert.Equal(60000, source.GetProperty("watchdogTimeoutMs").GetInt32());
    }

    [Fact]
    public async Task ExportImport_RoundTripsRecoveryFields()
    {
        await using TestAppHandle srcApp = await TestAppHandle.StartAsync(dir =>
        {
            File.WriteAllText(
                Path.Combine(dir, "sources.json"),
                JsonSerializer.Serialize(new DaRuntimeSettingsSnapshot(
                    1000,
                    true,
                    new[]
                    {
                        new DaSourceRuntimeSettings(
                            "recover3",
                            "Recover 3",
                            SourceTypes.OpcDa,
                            1000,
                            true,
                            50000,
                            new OpcDaSourceOptions(
                                "Matrikon.OPC.Simulation.1",
                                "localhost",
                                null,
                                null,
                                null,
                                5,
                                0),
                            null,
                            null,
                            null)
                    },
                    0)));
        });

        using JsonDocument export = await srcApp.GetJsonAsync("/api/config/export");
        JsonElement exportedSource = Single(export.RootElement.GetProperty("daSources").GetProperty("sources"), "recover3");
        Assert.Equal(5, exportedSource.GetProperty("maxConsecutiveFailures").GetInt32());
        Assert.Equal(0, exportedSource.GetProperty("watchdogTimeoutMs").GetInt32());
        string exported = export.RootElement.GetRawText();

        await using TestAppHandle target = await TestAppHandle.StartAsync(_ => { });
        using HttpResponseMessage import = await target.Client.PostAsync(
            "/api/config/import",
            new StringContent(exported, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        using JsonDocument get = await target.GetJsonAsync("/api/da/sources");
        JsonElement source = Single(get.RootElement.GetProperty("sources"), "recover3");
        Assert.Equal(5, source.GetProperty("maxConsecutiveFailures").GetInt32());
        Assert.Equal(0, source.GetProperty("watchdogTimeoutMs").GetInt32());
    }

    private static StringContent Json(object body) => new(
        JsonSerializer.Serialize(body),
        Encoding.UTF8,
        "application/json");

    private static JsonElement Single(JsonElement array, string sourceId)
    {
        foreach (JsonElement el in array.EnumerateArray())
        {
            if (el.TryGetProperty("sourceId", out JsonElement sid) &&
                string.Equals(sid.GetString(), sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return el;
            }
        }

        throw new Xunit.Sdk.XunitException($"Source '{sourceId}' not found in response.");
    }
}
