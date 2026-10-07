using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Renaming a saved tag through the mapping API (#43): the import dialog's Rename writes the name
/// template's rendering as the stored mapping's display name, through the same full-replace update
/// the description flow uses. The update must carry the stored mapping back — including the DA
/// group the tag is assigned to — or a rename would quietly drop settings that are none of its
/// business.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MappingRenameApiTests
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> MappingAsync(TestAppHandle handle, string itemId)
    {
        JsonElement payload = await ReadJsonAsync(await handle.Client.GetAsync("/api/mappings"));
        return payload.GetProperty("mappings").EnumerateArray()
            .Single(mapping => string.Equals(mapping.GetProperty("itemId").GetString(), itemId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_EchoesTheStoredMapping_AndKeepsItsGroupRateAndStamp()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        // A tag saved the way an import from before the template stored it: a name of its own, and
        // assigned to a named DA group so it shares the group's rate.
        using (HttpResponseMessage added = await handle.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new
                    {
                        sourceId = "default",
                        itemId = "DRYEND_PLC.Input_X.X000",
                        displayName = "X000",
                        description = "Stretch",
                        dataType = "Auto",
                        uaNodeId = "ns=2;s=default/DRYEND_PLC.Input_X.X000",
                        mode = "Source",
                        accessRights = "Read",
                        enabled = true,
                        daGroup = "Fast",
                        pollRateMs = 250
                    }
                }
            })))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        JsonElement before = await MappingAsync(handle, "DRYEND_PLC.Input_X.X000");
        Assert.Equal("X000", before.GetProperty("displayName").GetString());
        Assert.Equal("Fast", before.GetProperty("daGroup").GetString());
        string? addedUtc = before.GetProperty("addedUtc").GetString();

        // The rename the dialog performs: the stored mapping echoed back with only the name changed
        // to the template's rendering — mappingReplacePayload() in the dashboard.
        using (HttpResponseMessage updated = await handle.Client.PostAsync(
            "/api/mappings/update",
            JsonBody(new
            {
                tag = new
                {
                    sourceId = "default",
                    itemId = "DRYEND_PLC.Input_X.X000",
                    displayName = "DRYEND_PLC.Input_X.X000",
                    description = "Stretch",
                    dataType = "Auto",
                    uaNodeId = "ns=2;s=default/DRYEND_PLC.Input_X.X000",
                    enabled = true,
                    mode = "Source",
                    pollRateMs = 250,
                    daGroup = "Fast",
                    subscription = "",
                    deadbandPct = 0,
                    writeable = false,
                    accessRights = "Read",
                    mqttEnabled = false,
                    influxEnabled = false,
                    trendStyle = "Continuous"
                }
            })))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        JsonElement after = await MappingAsync(handle, "DRYEND_PLC.Input_X.X000");
        Assert.Equal("DRYEND_PLC.Input_X.X000", after.GetProperty("displayName").GetString());
        // Everything the rename is not about survives the full replace — the group above all, which
        // the payload must carry or the tag would fall back to the source's default rate.
        Assert.Equal("Fast", after.GetProperty("daGroup").GetString());
        Assert.Equal(250, after.GetProperty("pollRateMs").GetInt32());
        Assert.Equal("Stretch", after.GetProperty("description").GetString());
        Assert.Equal(addedUtc, after.GetProperty("addedUtc").GetString());
    }
}
