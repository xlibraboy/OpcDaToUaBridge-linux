using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The insert-only add endpoints (single and bulk) answer with what they actually did:
/// <c>added</c>, <c>skippedExisting</c> and — capped — which keys were already mapped.
/// Without it the dashboard showed nothing at all when a tag was already on the source
/// (issue #7), so the operator could not tell a duplicate from a failure.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MappingAddFeedbackApiTests
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
    }

    private static StringContent JsonBody(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static object TagPayload(string itemId, string? displayName = null) => new
    {
        sourceId = "default",
        itemId,
        displayName = displayName ?? itemId,
        dataType = "Auto",
        mode = "Source",
        accessRights = "Read",
        enabled = true
    };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostAdd(TestAppHandle handle, params object[] tags)
        => await ReadJsonAsync(await handle.Client.PostAsync("/api/mappings/add", JsonBody(new { tags })));

    private static async Task<JsonElement> PostBulkAdd(TestAppHandle handle, params object[] tags)
        => await ReadJsonAsync(await handle.Client.PostAsync("/api/mappings/bulk-add", JsonBody(new { tags })));

    private static string[] ExistingItemIds(JsonElement payload) =>
        payload.GetProperty("existing").EnumerateArray()
            .Select(e => e.GetProperty("itemId").GetString() ?? string.Empty)
            .ToArray();

    [Fact]
    public async Task Add_NewTag_ReportsAddedAndNoSkips()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        JsonElement payload = await PostAdd(handle, TagPayload("Fresh.Tag"));

        Assert.Equal(1, payload.GetProperty("added").GetInt32());
        Assert.Equal(0, payload.GetProperty("skippedExisting").GetInt32());
        Assert.Empty(ExistingItemIds(payload));
    }

    [Fact]
    public async Task Add_SameTagAgain_ReportsSkipAndNamesTheTag()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        JsonElement first = await PostAdd(handle, TagPayload("Duplicate.Tag"));
        long version = first.GetProperty("version").GetInt64();

        JsonElement second = await PostAdd(handle, TagPayload("Duplicate.Tag"));

        Assert.Equal(0, second.GetProperty("added").GetInt32());
        Assert.Equal(1, second.GetProperty("skippedExisting").GetInt32());
        Assert.Equal("Duplicate.Tag", Assert.Single(ExistingItemIds(second)));
        // Nothing was written, so the store version must not move.
        Assert.Equal(version, second.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Add_SameTagTwiceInOnePayload_AddsOnceAndReportsTheDuplicate()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        JsonElement payload = await PostAdd(handle, TagPayload("Repeated.Tag"), TagPayload("Repeated.Tag"));

        Assert.Equal(1, payload.GetProperty("added").GetInt32());
        Assert.Equal(1, payload.GetProperty("skippedExisting").GetInt32());
        Assert.Equal("Repeated.Tag", Assert.Single(ExistingItemIds(payload)));
    }

    [Fact]
    public async Task BulkAdd_MixedNewAndExisting_ReportsBothCounts()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        await PostAdd(handle, TagPayload("Already.Tag"));

        JsonElement payload = await PostBulkAdd(handle, TagPayload("Already.Tag"), TagPayload("Brand.New.Tag"));

        Assert.Equal(2, payload.GetProperty("received").GetInt32());
        Assert.Equal(1, payload.GetProperty("added").GetInt32());
        Assert.Equal(1, payload.GetProperty("skippedExisting").GetInt32());
        Assert.Equal("Already.Tag", Assert.Single(ExistingItemIds(payload)));
    }

    [Fact]
    public async Task BulkAdd_ExistingKey_KeepsTheStoredMappingUntouched()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        await PostAdd(handle, TagPayload("Keep.Original", displayName: "Original Name"));

        // Insert-only means the second payload must be skipped wholesale, not merged.
        using HttpResponseMessage add = await handle.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "default", itemId = "Keep.Original", displayName = "Renamed", dataType = "Double", mode = "Source", accessRights = "Read", enabled = true }
                }
            }));
        JsonElement skipped = await ReadJsonAsync(add);
        Assert.Equal(0, skipped.GetProperty("added").GetInt32());
        Assert.Equal(1, skipped.GetProperty("skippedExisting").GetInt32());

        using JsonDocument mappings = await handle.GetJsonAsync("/api/mappings");
        JsonElement stored = mappings.RootElement.GetProperty("mappings").EnumerateArray()
            .Single(m => m.GetProperty("itemId").GetString() == "Keep.Original");
        Assert.Equal("Original Name", stored.GetProperty("displayName").GetString());
        Assert.Equal("Auto", stored.GetProperty("dataType").GetString());
    }

    [Fact]
    public async Task BulkAdd_ManyDuplicates_ReportsCountButCapsTheNamedKeys()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        object[] tags = Enumerable.Range(1, 25)
            .Select(i => TagPayload($"Cap.Tag{i:00}"))
            .ToArray();
        JsonElement seeded = await PostBulkAdd(handle, tags);
        Assert.Equal(25, seeded.GetProperty("added").GetInt32());

        JsonElement replayed = await PostBulkAdd(handle, tags);

        Assert.Equal(0, replayed.GetProperty("added").GetInt32());
        Assert.Equal(25, replayed.GetProperty("skippedExisting").GetInt32());
        // The count is exact; the key list stays capped so a 100k re-import cannot answer with 100k keys.
        Assert.Equal(20, replayed.GetProperty("existing").GetArrayLength());
    }
}
