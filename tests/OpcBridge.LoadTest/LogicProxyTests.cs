using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The app's forwarding contract, end to end: the SPA's calls go through OpcBridge.Logic to a
/// real bridge and come back verbatim — definitions, state, and the bridge's own errors (the
/// 409 duplicate name, the 503 shape when the bridge is gone).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class LogicProxyTests
{
    private static LogicBlockDto Block(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = LogicBlockKinds.Interlock,
        Conditions =
        {
            new LogicConditionDto
            {
                Id = Guid.NewGuid(),
                Text = "Proxy condition",
                SourceId = "sim",
                ItemId = "tag",
                Op = LogicConditionOps.On
            }
        }
    };

    [Fact]
    public async Task Proxy_ForwardsDefinitionsStateAndDeletes()
    {
        await using TestAppHandle bridge = await TestAppHandle.StartAsync(static _ => { });
        await using LogicAppHandle logic = await LogicAppHandle.StartAsync(bridge.Client.BaseAddress!.ToString());

        LogicBlockDto block = Block("Proxy Line 01");
        using (HttpResponseMessage save = await logic.Client.PostAsJsonAsync("/api/logic/blocks", new { block }))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        JsonDocument list = await logic.GetJsonAsync("/api/logic");
        JsonElement stored = list.RootElement.GetProperty("blocks")[0];
        Assert.Equal(block.Id, stored.GetProperty("id").GetGuid());
        Assert.Equal("Proxy Line 01", stored.GetProperty("name").GetString());

        using (HttpResponseMessage state = await logic.Client.GetAsync("/api/logic/state"))
        {
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        }

        using (HttpResponseMessage delete = await logic.Client.DeleteAsync($"/api/logic/blocks/{block.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        JsonDocument after = await logic.GetJsonAsync("/api/logic");
        Assert.Empty(after.RootElement.GetProperty("blocks").EnumerateArray());
    }

    [Fact]
    public async Task Proxy_PassesTheBridgesDuplicateNameConflictThroughVerbatim()
    {
        await using TestAppHandle bridge = await TestAppHandle.StartAsync(static _ => { });
        await using LogicAppHandle logic = await LogicAppHandle.StartAsync(bridge.Client.BaseAddress!.ToString());

        using (HttpResponseMessage first = await logic.Client.PostAsJsonAsync("/api/logic/blocks", new { block = Block("Proxy Conflict") }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using HttpResponseMessage conflict = await logic.Client.PostAsJsonAsync("/api/logic/blocks", new { block = Block("Proxy Conflict") });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("Block name already exists.", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Proxy_ReportsTheBridgesUnreachableShapeWhenItIsGone()
    {
        TestAppHandle bridge = await TestAppHandle.StartAsync(static _ => { });
        string bridgeUrl = bridge.Client.BaseAddress!.ToString();
        await bridge.DisposeAsync();

        await using LogicAppHandle logic = await LogicAppHandle.StartAsync(bridgeUrl);
        using HttpResponseMessage response = await logic.Client.GetAsync("/api/logic");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("unreachable", body.RootElement.GetProperty("error").GetString());
    }
}
