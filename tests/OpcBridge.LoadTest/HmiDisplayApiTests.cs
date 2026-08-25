using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(InterlinkApiAppCollection))]
public sealed class HmiDisplayApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static void WriteAppsettings(string dir)
    {
        var appsettings = new
        {
            Da = new { ProgId = "Matrikon.OPC.Simulation.1", Host = "localhost", UpdateRateMs = 1000, UseSubscriptions = true },
            Ua = new { ApplicationName = "OpcDaToUaBridge", EndpointUrl = "opc.tcp://0.0.0.0:4840/OpcBridge", AutoAcceptUntrustedCertificates = true, RequireAuthentication = false, Username = "", Password = "", AllowedIpAddresses = Array.Empty<string>() },
            Bridge = new
            {
                RateLimits = new { },
                ExpectedTagCount = 10,
                Mappings = new object[]
                {
                    new
                    {
                        SourceId = "default",
                        DaItemId = "Random.Int1",
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
            Mqtt = new { Enabled = false, BrokerUrl = "tcp://localhost:1883", ClientId = "OpcDaToUaBridge", UserName = (string?)null, Password = (string?)null, Tls = false, IgnoreCertErrors = false, TopicPrefix = "bridge/tags", PayloadFields = "Value, Timestamp" },
            Hmi = new { BroadcastFlushMs = 100 }
        };
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), JsonSerializer.Serialize(appsettings, JsonOptions));
        string mapPath = Path.Combine(dir, "mappings.json");
        if (File.Exists(mapPath))
        {
            File.Delete(mapPath);
        }
    }

    private static DisplayDocumentDto SampleDoc(string id, int version, string name = "Plant Overview") => new()
    {
        SchemaVersion = 1,
        Id = id,
        Name = name,
        Version = version,
        Width = 1920,
        Height = 1080,
        Widgets =
        [
            new DisplayWidgetDto
            {
                Id = "w1",
                Type = "numeric",
                X = 10,
                Y = 20,
                W = 100,
                H = 40,
                Binding = new TagBindingDto
                {
                    BridgeId = "line1",
                    SourceId = "default",
                    DaItemId = "Tank.Level"
                }
            }
        ]
    };

    [Fact]
    public async Task Displays_ListEmpty_ThenCreateGetUpdateConflictDelete()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);

        using (JsonDocument emptyList = await handle.GetJsonAsync("/api/hmi/displays"))
        {
            Assert.True(emptyList.RootElement.TryGetProperty("items", out JsonElement items));
            Assert.Equal(JsonValueKind.Array, items.ValueKind);
            Assert.Equal(0, items.GetArrayLength());
        }

        DisplayDocumentDto createBody = SampleDoc("plant-overview", version: 0);
        using HttpResponseMessage createResponse = await handle.Client.PutAsJsonAsync(
            "/api/hmi/displays/plant-overview",
            createBody,
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        DisplayDocumentDto? created = await createResponse.Content.ReadFromJsonAsync<DisplayDocumentDto>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(1, created!.Version);
        Assert.Equal("plant-overview", created.Id);

        using JsonDocument listed = await handle.GetJsonAsync("/api/hmi/displays");
        Assert.Equal(1, listed.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("plant-overview", listed.RootElement.GetProperty("items")[0].GetProperty("id").GetString());

        using HttpResponseMessage getResponse = await handle.Client.GetAsync("/api/hmi/displays/plant-overview");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        DisplayDocumentDto? got = await getResponse.Content.ReadFromJsonAsync<DisplayDocumentDto>(JsonOptions);
        Assert.NotNull(got);
        Assert.Equal(1, got!.Version);
        Assert.Single(got.Widgets);

        DisplayDocumentDto updateBody = SampleDoc("plant-overview", version: 1, name: "Updated Plant");
        using HttpResponseMessage updateResponse = await handle.Client.PutAsJsonAsync(
            "/api/hmi/displays/plant-overview",
            updateBody,
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        DisplayDocumentDto? updated = await updateResponse.Content.ReadFromJsonAsync<DisplayDocumentDto>(JsonOptions);
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Version);
        Assert.Equal("Updated Plant", updated.Name);

        DisplayDocumentDto stale = SampleDoc("plant-overview", version: 1, name: "Stale");
        using HttpResponseMessage conflictResponse = await handle.Client.PutAsJsonAsync(
            "/api/hmi/displays/plant-overview",
            stale,
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        using JsonDocument conflictDoc = JsonDocument.Parse(await conflictResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, conflictDoc.RootElement.GetProperty("currentVersion").GetInt32());

        using HttpResponseMessage deleteResponse = await handle.Client.DeleteAsync("/api/hmi/displays/plant-overview");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using HttpResponseMessage missing = await handle.Client.GetAsync("/api/hmi/displays/plant-overview");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using JsonDocument afterDelete = await handle.GetJsonAsync("/api/hmi/displays");
        Assert.Equal(0, afterDelete.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Displays_InvalidId_Returns400()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);

        using var content = new StringContent(
            JsonSerializer.Serialize(SampleDoc("bad", 0), JsonOptions),
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await handle.Client.PutAsync("/api/hmi/displays/bad..id", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Displays_BadSchemaVersion_Returns400()
    {
        await using var handle = await TestAppHandle.StartAsync(WriteAppsettings);
        DisplayDocumentDto doc = SampleDoc("ok-page", 0);
        doc.SchemaVersion = 99;
        using HttpResponseMessage response = await handle.Client.PutAsJsonAsync("/api/hmi/displays/ok-page", doc, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
