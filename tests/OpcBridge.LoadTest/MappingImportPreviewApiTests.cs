using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Maps import preview (issue #30): the bridge parses the picked file, compares it with the
/// tags already mapped, and reports what the source really exposes so a name the server does not
/// have is visible before anything is added. A source that cannot be read — OPC DA browsing is
/// COM, so it needs Windows and a reachable server — degrades to the mapped comparison and says
/// so rather than condemning every row as missing.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MappingImportPreviewApiTests
{
    private const string TwoTagExport = """
        #MX_DataTags;
        "LocationPath","Name","Description","Enable","AddressAsString"
        "\Address Space\DRYEND_PLC\Input_X","X000","Stretch","Yes","X0"
        "\Address Space\DRYEND_PLC\Input_X","X001","Rope","Yes","X1"
        "\Address Space\DRYEND_PLC\Output_Y","Y100","","Yes","Y100"
        """;

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

    private static async Task<JsonElement> PreviewAsync(TestAppHandle handle, string sourceId, string text)
    {
        return await ReadJsonAsync(await handle.Client.PostAsync(
            "/api/mappings/import/preview",
            JsonBody(new { sourceId, text })));
    }

    private static async Task AddTagAsync(TestAppHandle handle, string itemId, string? description = null)
    {
        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "default", itemId, displayName = itemId, description, dataType = "Auto", mode = "Source", accessRights = "Read", enabled = true }
                }
            }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static JsonElement RowFor(JsonElement preview, string itemId) =>
        preview.GetProperty("rows").EnumerateArray()
            .Single(row => string.Equals(row.GetProperty("itemId").GetString(), itemId, StringComparison.Ordinal));

    private static string? StatusOf(JsonElement preview, string itemId) =>
        RowFor(preview, itemId).GetProperty("status").GetString();

    [Fact]
    public async Task Preview_SeparatesNewMappedAndDifferingRows()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);
        // One of the file's tags is already mapped, with a description of its own. The mapping is
        // keyed by the file's path-qualified item id, not the bare tag name.
        await AddTagAsync(handle, "DRYEND_PLC.Input_X.X000", "Old description");

        JsonElement preview = await PreviewAsync(handle, "default", TwoTagExport);

        Assert.Equal("default", preview.GetProperty("sourceId").GetString());
        Assert.Equal(1, preview.GetProperty("mappedTags").GetInt32());
        Assert.Equal(3, preview.GetProperty("rowCount").GetInt32());
        Assert.Equal("differs", StatusOf(preview, "DRYEND_PLC.Input_X.X000"));
        Assert.Equal("Old description", RowFor(preview, "DRYEND_PLC.Input_X.X000").GetProperty("existingDescription").GetString());
        Assert.Equal("new", StatusOf(preview, "DRYEND_PLC.Input_X.X001"));
        Assert.Equal("Rope", RowFor(preview, "DRYEND_PLC.Input_X.X001").GetProperty("description").GetString());
        // A blank description in the file is no opinion about the stored one.
        Assert.Equal("new", StatusOf(preview, "DRYEND_PLC.Output_Y.Y100"));
        Assert.Equal(JsonValueKind.Null, RowFor(preview, "DRYEND_PLC.Output_Y.Y100").GetProperty("description").ValueKind);
        // Each row carries both the tag's name and the path-qualified id it maps as, plus the
        // row anchor the dialog groups by.
        Assert.Equal("X001", RowFor(preview, "DRYEND_PLC.Input_X.X001").GetProperty("name").GetString());
        Assert.Equal(@"\Address Space\DRYEND_PLC\Input_X", RowFor(preview, "DRYEND_PLC.Input_X.X001").GetProperty("group").GetString());
    }

    [Fact]
    public async Task Preview_ReportsTheSourceTagsTheFileLeavesOut()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        JsonElement preview = await PreviewAsync(handle, "default", TwoTagExport);

        // The reconciliation's other half always travels: the source's own tags the file does
        // not mention, each with its mapped state. A source check that did not run — this host is
        // not Windows, or the DA server is not reachable — cannot invent any.
        Assert.Equal(JsonValueKind.Array, preview.GetProperty("sourceOnly").ValueKind);
        Assert.True(preview.TryGetProperty("sourceOnlyCount", out JsonElement sourceOnlyCount));
        Assert.True(preview.TryGetProperty("sourceOnlyTruncated", out _));
        Assert.True(preview.TryGetProperty("sourceTagCount", out JsonElement sourceTagCount));
        if (!preview.GetProperty("sourceChecked").GetBoolean())
        {
            Assert.Equal(0, preview.GetProperty("sourceOnly").GetArrayLength());
            Assert.Equal(0, sourceOnlyCount.GetInt32());
            Assert.Equal(0, sourceTagCount.GetInt32());
        }
    }

    [Fact]
    public async Task Preview_SaysSoWhenTheSourceCannotBeRead()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        JsonElement preview = await PreviewAsync(handle, "default", TwoTagExport);

        // The source check either ran (a Windows host with the DA server installed) or it was
        // skipped — and a skipped check must come with the reason, because the dialog prints it
        // instead of claiming the tags are missing from the server.
        bool sourceChecked = preview.GetProperty("sourceChecked").GetBoolean();
        if (sourceChecked)
        {
            Assert.Equal(JsonValueKind.Null, preview.GetProperty("sourceError").ValueKind);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(preview.GetProperty("sourceError").GetString()));
            Assert.Equal(JsonValueKind.Null, RowFor(preview, "DRYEND_PLC.Input_X.X001").GetProperty("onSource").ValueKind);
        }
    }

    [Fact]
    public async Task Preview_ForAUaSource_ReportsWhyTheTagListWasNotRead()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);
        using HttpResponseMessage created = await handle.Client.PostAsync(
            "/api/da/sources",
            JsonBody(new
            {
                sourceId = "ua1",
                displayName = "UA one",
                sourceType = "OpcUa",
                // Nothing listens here, so the tag walk fails immediately: the endpoint must
                // report that instead of hanging or throwing, and the rows must still compare
                // against the stored mappings.
                endpointUrl = "opc.tcp://127.0.0.1:59599/OpcBridge",
                securityMode = "None",
                securityPolicy = "None",
                maxMappedTags = 500,
                updateRateMs = 1000
            }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        JsonElement preview = await PreviewAsync(handle, "ua1", TwoTagExport);

        Assert.False(preview.GetProperty("sourceChecked").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(preview.GetProperty("sourceError").GetString()));
        Assert.Equal("new", StatusOf(preview, "DRYEND_PLC.Input_X.X001"));
    }

    [Fact]
    public async Task Preview_UnknownSource_IsRejected()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/import/preview",
            JsonBody(new { sourceId = "nope", text = TwoTagExport }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("not found", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preview_FileWithoutATagTable_IsRejected()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        using HttpResponseMessage res = await handle.Client.PostAsync(
            "/api/mappings/import/preview",
            JsonBody(new { sourceId = "default", text = "Id,Name\n1,Nope\n" }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("#MX_DataTags", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AddedMapping_CarriesTheAddStamp_AndAnEditKeepsIt()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);

        await AddTagAsync(handle, "Stamped.Tag", "First");
        JsonElement stored = await GetMappingAsync(handle, "Stamped.Tag");
        string stamp = stored.GetProperty("addedUtc").GetString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(stamp));

        // The faceplate's Apply (and any other client today) sends the mapping back without the
        // stamp: an edit must not turn into a fresh "added" time.
        using HttpResponseMessage updated = await handle.Client.PostAsync(
            "/api/mappings/update",
            JsonBody(new
            {
                tag = new
                {
                    sourceId = "default",
                    itemId = "Stamped.Tag",
                    displayName = "Stamped.Tag",
                    description = "Second",
                    dataType = "Auto",
                    mode = "Source",
                    accessRights = "Read",
                    enabled = true
                }
            }));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        JsonElement after = await GetMappingAsync(handle, "Stamped.Tag");
        Assert.Equal(stamp, after.GetProperty("addedUtc").GetString());
        Assert.Equal("Second", after.GetProperty("description").GetString());
    }

    private static async Task<JsonElement> GetMappingAsync(TestAppHandle handle, string itemId)
    {
        using JsonDocument mappings = await handle.GetJsonAsync("/api/mappings");
        return mappings.RootElement.GetProperty("mappings").EnumerateArray()
            .Single(mapping => string.Equals(mapping.GetProperty("itemId").GetString(), itemId, StringComparison.Ordinal))
            .Clone();
    }
}
