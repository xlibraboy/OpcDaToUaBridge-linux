using System.Net;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The workers endpoints on a non-Windows host must answer gracefully (the feature is
/// Windows-only), not a 500. The Windows paths — spawn, restart, kill, quarantine — are
/// covered on the lab rig.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class WorkersApiTests
{
    [Fact]
    public async Task WorkersEndpoint_OnNonWindows_ReportsUnsupported()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage response = await handle.Client.GetAsync("/api/workers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("supported").GetBoolean());
        Assert.Equal("non-windows", document.RootElement.GetProperty("platform").GetString());
        Assert.Empty(document.RootElement.GetProperty("workers").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("history").EnumerateArray());
    }

    [Fact]
    public async Task WorkerActions_OnNonWindows_AreRejected()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage restart = await handle.Client.PostAsync("/api/workers/own:pmd/restart", null);
        using HttpResponseMessage kill = await handle.Client.PostAsync("/api/workers/own:pmd/kill?confirm=true", null);

        Assert.Equal(HttpStatusCode.BadRequest, restart.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, kill.StatusCode);
    }

    [Fact]
    public async Task Kill_WithoutConfirm_IsRejected()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage response = await handle.Client.PostAsync("/api/workers/own:pmd/kill", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
