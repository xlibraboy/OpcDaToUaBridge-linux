using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The troubleshoot endpoint on a non-Windows host must answer with the platform error, not a
/// 500 — the dashboard turns it into a message. The Windows paths are covered on the lab rig.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class DaTroubleshootApiTests
{
    [Fact]
    public async Task TroubleshootEndpoint_OnNonWindows_ReportsWindowsRequired()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(_ => { });

        using HttpResponseMessage response = await handle.Client.PostAsync(
            "/api/da/troubleshoot",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Windows", document.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }
}
