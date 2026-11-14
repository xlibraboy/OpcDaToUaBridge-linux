using System.Text.Json;
using OpcBridge.Hmi.Services;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The real HMI hub client against a real bridge: the identity it puts on the URL has to
/// arrive in /api/diagnostics as a connected HMI with this PC's name.
/// </summary>
public sealed class ClientPresenceHubTests
{
    [Fact]
    public async Task HmiHubConnection_AppearsInDiagnosticsWithThisPc()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(_ => { });
        var hub = new HmiHubClient();
        try
        {
            await hub.ConnectAsync(
                app.Client.BaseAddress!.ToString(),
                _ => Task.CompletedTask,
                _ => Task.CompletedTask,
                CancellationToken.None);

            using JsonDocument doc = await app.GetJsonAsync("/api/diagnostics");
            JsonElement items = doc.RootElement.GetProperty("clients").GetProperty("items");
            JsonElement hmi = items.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "hmi");

            Assert.Equal(Environment.MachineName, hmi.GetProperty("machineName").GetString());
            Assert.Equal(1, hmi.GetProperty("connectionCount").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(hmi.GetProperty("appVersion").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(hmi.GetProperty("address").GetString()));
        }
        finally
        {
            await hub.DisposeAsync();
        }
    }
}
