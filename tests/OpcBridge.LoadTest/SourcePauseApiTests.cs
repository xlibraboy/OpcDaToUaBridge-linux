using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// API contract for the temporary source pause (#5): POST /api/da/sources/pause
/// flips a runtime-only flag, GET /api/da/sources reports it, and the bridge
/// worker releases the paused source's connection instead of connecting.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class SourcePauseApiTests
{
    private static StringContent JsonBody(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static async Task SeedMxSourceAsync(TestAppHandle app, string sourceId)
    {
        using HttpResponseMessage post = await app.Client.PostAsync(
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
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
    }

    private static async Task<JsonElement> GetSourceAsync(TestAppHandle app, string sourceId)
    {
        using JsonDocument doc = await app.GetJsonAsync("/api/da/sources");
        return doc.RootElement.GetProperty("sources").EnumerateArray()
            .First(s => s.GetProperty("sourceId").GetString() == sourceId)
            .Clone();
    }

    [Fact]
    public async Task PauseRoundTrip_ReportsFlagInSourceList()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });
        await SeedMxSourceAsync(app, "mxp");

        using JsonDocument before = await app.GetJsonAsync("/api/da/sources");
        JsonElement unpauseed = before.RootElement.GetProperty("sources").EnumerateArray()
            .First(s => s.GetProperty("sourceId").GetString() == "mxp");
        Assert.False(unpauseed.GetProperty("paused").GetBoolean());

        using HttpResponseMessage pause = await app.Client.PostAsync(
            "/api/da/sources/pause", JsonBody(new { sourceId = "mxp", paused = true }));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        using JsonDocument pauseBody = JsonDocument.Parse(await pause.Content.ReadAsStringAsync());
        Assert.True(pauseBody.RootElement.GetProperty("paused").GetBoolean());
        Assert.Equal("mxp", pauseBody.RootElement.GetProperty("sourceId").GetString());

        JsonElement pausedSource = await GetSourceAsync(app, "mxp");
        Assert.True(pausedSource.GetProperty("paused").GetBoolean());

        using HttpResponseMessage resume = await app.Client.PostAsync(
            "/api/da/sources/pause", JsonBody(new { sourceId = "mxp", paused = false }));
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        using JsonDocument resumeBody = JsonDocument.Parse(await resume.Content.ReadAsStringAsync());
        Assert.False(resumeBody.RootElement.GetProperty("paused").GetBoolean());

        JsonElement resumedSource = await GetSourceAsync(app, "mxp");
        Assert.False(resumedSource.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task Pause_UnknownSource_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage missing = await app.Client.PostAsync(
            "/api/da/sources/pause", JsonBody(new { sourceId = "nope", paused = true }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        string body = await missing.Content.ReadAsStringAsync();
        Assert.Contains("Source not found", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pause_MissingSourceId_Returns400()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage blank = await app.Client.PostAsync(
            "/api/da/sources/pause", JsonBody(new { sourceId = "", paused = true }));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task PausedSource_ReleasesItsConnection_AndNeverReconnects()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });
        await SeedMxSourceAsync(app, "mxpaused");

        using HttpResponseMessage up = await app.Client.PostAsync(
            "/api/mappings/add",
            JsonBody(new
            {
                tags = new[]
                {
                    new { sourceId = "mxpaused", itemId = "D100", uaNodeId = "ns=2;s=mxpaused/D100" }
                }
            }));
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        // Wait for the worker's first reconcile pass to register the source and
        // attempt the connection (health can beat the first worker tick).
        string prePause = "";
        for (int i = 0; i < 100; i++)
        {
            using JsonDocument doc = await app.GetJsonAsync("/api/status");
            JsonElement source = doc.RootElement.GetProperty("bridge").GetProperty("sources")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("sourceId").GetString() == "mxpaused");
            if (source.ValueKind == JsonValueKind.Object)
            {
                prePause = source.GetProperty("connectionState").GetString()!;
                break;
            }

            await Task.Delay(100);
        }

        Assert.False(string.IsNullOrEmpty(prePause), "source must appear in /api/status before the pause");
        // The MX client cannot reach a real PLC here; any pre-pause state is fine
        // as long as it is not already Paused.
        Assert.NotEqual("Paused", prePause);

        using HttpResponseMessage pause = await app.Client.PostAsync(
            "/api/da/sources/pause", JsonBody(new { sourceId = "mxpaused", paused = true }));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

        // The worker reconciles on its next tick: paused → session disposed, state Paused.
        bool reached = false;
        for (int i = 0; i < 100 && !reached; i++)
        {
            await Task.Delay(100);
            using JsonDocument poll = await app.GetJsonAsync("/api/status");
            string state = poll.RootElement.GetProperty("bridge").GetProperty("sources")
                .EnumerateArray().First(s => s.GetProperty("sourceId").GetString() == "mxpaused")
                .GetProperty("connectionState").GetString()!;
            reached = state == "Paused";
        }

        Assert.True(reached, "paused source must settle in the Paused state");

        // It stays paused (no reconnect churn) while the flag is held.
        await Task.Delay(1500);
        using JsonDocument settled = await app.GetJsonAsync("/api/status");
        string settledState = settled.RootElement.GetProperty("bridge").GetProperty("sources")
            .EnumerateArray().First(s => s.GetProperty("sourceId").GetString() == "mxpaused")
            .GetProperty("connectionState").GetString()!;
        Assert.Equal("Paused", settledState);

        // Resuming hands the source back to the normal connect loop.
        using HttpResponseMessage resume = await app.Client.PostAsync(
            "/api/da/sources/pause", JsonBody(new { sourceId = "mxpaused", paused = false }));
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);

        bool leftPaused = true;
        for (int i = 0; i < 100 && leftPaused; i++)
        {
            await Task.Delay(100);
            using JsonDocument poll = await app.GetJsonAsync("/api/status");
            string state = poll.RootElement.GetProperty("bridge").GetProperty("sources")
                .EnumerateArray().First(s => s.GetProperty("sourceId").GetString() == "mxpaused")
                .GetProperty("connectionState").GetString()!;
            leftPaused = state == "Paused";
        }

        Assert.False(leftPaused, "resumed source must leave the Paused state");
    }
}
