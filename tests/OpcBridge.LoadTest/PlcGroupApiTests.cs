using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// /api/plc/groups contract: upsert/remove happy paths, every 400 branch, and the GET payload
/// shape (definitions + member counts + effective distinct rates, MX sources only — spec §6).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class PlcGroupApiTests
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

    /// <summary>Seeds an MxComponent-typed source through the dashboard's own endpoint.</summary>
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

    /// <summary>Seeds an OpcDa-typed source through the dashboard's own endpoint.</summary>
    private static async Task SeedOpcDaSourceAsync(TestAppHandle handle, string sourceId)
    {
        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId,
                displayName = "DA " + sourceId,
                sourceType = "OpcDa",
                progId = "Matrikon.OPC.Simulation.1",
                host = "localhost"
            }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static JsonElement GetSource(JsonDocument doc, string sourceId)
    {
        foreach (JsonElement source in doc.RootElement.GetProperty("sources").EnumerateArray())
        {
            if (string.Equals(source.GetProperty("sourceId").GetString(), sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        throw new Xunit.Sdk.XunitException($"Source '{sourceId}' not found in GET /api/plc/groups response.");
    }

    private static async Task<HttpResponseMessage> AddMappingAsync(
        TestAppHandle handle, string sourceId, string itemId, string plcGroup, int pollRateMs)
    {
        return await handle.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId, itemId, uaNodeId = $"ns=2;s={itemId}", plcGroup, pollRateMs }
                }
            }));
    }

    [Fact]
    public async Task Upsert_Remove_RoundTrip_ReportsMovedMappings()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedMxSourceAsync(handle, "mx1");
        await SeedOpcDaSourceAsync(handle, "da1");

        // Seed one tag assigned to the group before the definition exists (tolerated at add time).
        using (HttpResponseMessage add = await AddMappingAsync(handle, "mx1", "D100", "Fast", 750))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        // 1 -> clamped to 100 server-side.
        using (HttpResponseMessage up = await handle.Client.PostAsync(
            "/api/plc/groups",
            JsonBody(new { sourceId = "mx1", name = "Fast", updateRateMs = 1 })))
        {
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await up.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(body.RootElement.TryGetProperty("version", out _));
        }

        // Clamp assertion: GET ?sourceId=mx1 shows updateRateMs 100 for "Fast".
        using (JsonDocument list = await handle.GetJsonAsync("/api/plc/groups?sourceId=mx1"))
        {
            JsonElement src = GetSource(list, "mx1");
            JsonElement groups = src.GetProperty("groups");
            Assert.Equal(1, groups.GetArrayLength());
            Assert.Equal("Fast", groups[0].GetProperty("name").GetString());
            Assert.Equal(100, groups[0].GetProperty("updateRateMs").GetInt32());
            Assert.Equal(1, groups[0].GetProperty("memberCount").GetInt32());
        }

        using (HttpResponseMessage rm = await handle.Client.PostAsync(
            "/api/plc/groups/remove",
            JsonBody(new { sourceId = "mx1", name = "Fast" })))
        {
            Assert.Equal(HttpStatusCode.OK, rm.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await rm.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(body.RootElement.TryGetProperty("version", out _));
            Assert.True(body.RootElement.GetProperty("movedMappings").GetInt32() >= 1);
        }

        // The removed definition is gone and its member tag moved back to the source default.
        using (JsonDocument list = await handle.GetJsonAsync("/api/plc/groups?sourceId=mx1"))
        {
            Assert.Equal(0, GetSource(list, "mx1").GetProperty("groups").GetArrayLength());
        }

        using (JsonDocument mappings = await handle.GetJsonAsync("/api/mappings"))
        {
            bool found = false;
            foreach (JsonElement tag in mappings.RootElement.GetProperty("mappings").EnumerateArray())
            {
                if (string.Equals(tag.GetProperty("itemId").GetString(), "D100", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    Assert.Equal(string.Empty, tag.GetProperty("plcGroup").GetString());
                }
            }

            Assert.True(found, "Expected mapping 'D100' in GET /api/mappings response.");
        }
    }

    [Fact]
    public async Task Upsert_NonMxSource_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedMxSourceAsync(handle, "mx1");
        await SeedOpcDaSourceAsync(handle, "da1");

        using HttpResponseMessage resp = await handle.Client.PostAsync(
            "/api/plc/groups",
            JsonBody(new { sourceId = "da1", name = "Fast", updateRateMs = 250 }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("MX Component", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_BlankName_Returns400()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedMxSourceAsync(handle, "mx1");

        using HttpResponseMessage resp = await handle.Client.PostAsync(
            "/api/plc/groups",
            JsonBody(new { sourceId = "mx1", name = "  ", updateRateMs = 250 }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("1-64", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_ListsOnlyMxSources_WithMemberCounts()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedMxSourceAsync(handle, "mx1");
        await SeedOpcDaSourceAsync(handle, "da1");

        using (HttpResponseMessage up = await handle.Client.PostAsync(
            "/api/plc/groups",
            JsonBody(new { sourceId = "mx1", name = "Fast", updateRateMs = 250 })))
        {
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        }

        // Group member: group rate wins over the per-tag rate. Ungrouped tag: per-tag rate stands.
        using (HttpResponseMessage grouped = await AddMappingAsync(handle, "mx1", "D100", "Fast", 750))
        {
            Assert.Equal(HttpStatusCode.OK, grouped.StatusCode);
        }

        using (HttpResponseMessage ungrouped = await AddMappingAsync(handle, "mx1", "D200", "", 500))
        {
            Assert.Equal(HttpStatusCode.OK, ungrouped.StatusCode);
        }

        using JsonDocument doc = await handle.GetJsonAsync("/api/plc/groups");
        string body = doc.RootElement.GetRawText();
        Assert.Contains("mx1", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sourceId\":\"da1\"", body);

        JsonElement src = GetSource(doc, "mx1");
        Assert.Equal(1000, src.GetProperty("defaultUpdateRateMs").GetInt32());

        // Effective distinct rates per spec §6: 250 (group wins) + 500 (per-tag).
        JsonElement rates = src.GetProperty("effectiveRates");
        Assert.Equal(2, rates.GetArrayLength());
        Assert.Equal(250, rates[0].GetInt32());
        Assert.Equal(500, rates[1].GetInt32());

        JsonElement groups = src.GetProperty("groups");
        Assert.Equal(1, groups.GetArrayLength());
        Assert.Equal("Fast", groups[0].GetProperty("name").GetString());
        Assert.Equal(250, groups[0].GetProperty("updateRateMs").GetInt32());
        Assert.Equal(1, groups[0].GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task DaSourceEdit_WithoutGroupPayload_PreservesExistingPlcGroups()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedMxSourceAsync(handle, "mx1");

        using (HttpResponseMessage up = await handle.Client.PostAsync(
            "/api/plc/groups",
            JsonBody(new { sourceId = "mx1", name = "Fast", updateRateMs = 250 })))
        {
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        }

        // Dashboard-style re-save of the SAME source with a changed field (timeout)
        // and no group payload: the edit must not silently delete the definitions.
        using (HttpResponseMessage edit = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "mx1",
                displayName = "MX mx1 edited",
                sourceType = "MxComponent",
                logicalStationNumber = 4,
                timeoutMs = 4500,
                retryCount = 2,
                maxMappedTags = 500,
                updateRateMs = 1000
            })))
        {
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

            // Sanity: the edit actually replaced the record (not a silent no-op).
            using JsonDocument body = JsonDocument.Parse(await edit.Content.ReadAsStringAsync());
            Assert.Equal(4500, body.RootElement.GetProperty("source").GetProperty("timeoutMs").GetInt32());
        }

        using (JsonDocument list = await handle.GetJsonAsync("/api/plc/groups?sourceId=mx1"))
        {
            JsonElement src = GetSource(list, "mx1");
            Assert.Equal(1, src.GetProperty("groups").GetArrayLength());
            Assert.Equal("Fast", src.GetProperty("groups")[0].GetProperty("name").GetString());
            Assert.Equal(250, src.GetProperty("groups")[0].GetProperty("updateRateMs").GetInt32());
        }
    }

    [Fact]
    public async Task MappingAdd_PlcGroupField_RoundTripsThroughAllMappingEndpoints()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(dir => WriteMinimalAppsettings(dir));
        await SeedMxSourceAsync(handle, "mx1");

        // add
        using (HttpResponseMessage add = await AddMappingAsync(handle, "mx1", "D300", "Slow", 2000))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        }

        // bulk-add (second tag, same pass-through path)
        using (HttpResponseMessage bulk = await handle.Client.PostAsync(
            "/api/mappings/bulk-add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "mx1", itemId = "D301", uaNodeId = "ns=2;s=D301", plcGroup = "Slow" }
                }
            })))
        {
            Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
        }

        // update flips the group via the shared mapper
        using (HttpResponseMessage upd = await handle.Client.PostAsync(
            "/api/mappings/update",
            JsonBody(new
            {
                tag = new { sourceId = "mx1", itemId = "D300", plcGroup = "Fast", pollRateMs = 120 }
            })))
        {
            Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        }

        using JsonDocument mappings = await handle.GetJsonAsync("/api/mappings");
        JsonElement all = mappings.RootElement.GetProperty("mappings");
        Assert.Equal(2, all.GetArrayLength());
        foreach (JsonElement tag in all.EnumerateArray())
        {
            string itemId = tag.GetProperty("itemId").GetString() ?? string.Empty;
            string expected = itemId == "D300" ? "Fast" : "Slow";
            Assert.Equal(expected, tag.GetProperty("plcGroup").GetString());
        }
    }
}
