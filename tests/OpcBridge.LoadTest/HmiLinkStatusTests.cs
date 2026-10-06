using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpcBridge.Hmi.Core;
using OpcBridge.Hmi.Services;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The bridge link is a live thing, not a fact latched at connect time: dropping it must
/// read as Reconnecting on every surface, and coming back must re-sync the tag cache
/// without the operator pressing Connect. A bridge that is down while its peers are
/// healthy must not block them, and must join on its own once it is reachable.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class HmiLinkStatusTests
{
    private static readonly BridgeManagerOptions FastOptions = new(
        PollInterval: TimeSpan.FromMilliseconds(250),
        SnapshotRetryDelays: new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(100) },
        // Long enough that the first retry cannot overwrite the connect summary while the
        // test asserts it; the join-when-it-comes-up case waits one schedule step.
        BridgeRetryDelays: new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) },
        HubTiming: new HmiHubTiming(
            KeepAliveInterval: TimeSpan.FromSeconds(1),
            ServerTimeout: TimeSpan.FromSeconds(20),
            ReconnectDelays: new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250) }));

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

    private static string TempConfigPath() => Path.Combine(
        Path.GetTempPath(),
        "hmi-link-" + Guid.NewGuid().ToString("N"),
        "hmi-config.json");

    private static void DeleteConfig(string configPath)
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
        catch
        {
        }
    }

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 20000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task DroppedLink_ReadsReconnecting_ThenResyncsOnRecovery()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);
        await using var forwarder = new TcpForwarder(new Uri(handle.Client.BaseAddress!.ToString()));
        forwarder.Start();

        string configPath = TempConfigPath();
        var connections = new BridgeConnectionManager(FastOptions);
        var vm = new MainViewModel(connections, new PopupWindowService(), ownsServices: true, configPath: configPath);

        try
        {
            vm.BridgeRows[0].Address = forwarder.BaseUrl;
            await vm.ConnectCommand.ExecuteAsync(null);

            Assert.True(vm.IsConnected);
            Assert.Equal("Connected", vm.ConnectionState);
            Assert.True(vm.IsPillOk);
            Assert.False(vm.IsPillWarn);
            Assert.False(vm.IsPillBad);
            Assert.Equal("Connected", vm.BridgeRows[0].LinkStateText);
            Assert.Equal(BridgeLinkState.Connected, Assert.Single(connections.LinkStatuses).State);

            // The network drops while the bridge itself stays up.
            forwarder.Stop();

            Assert.True(
                await WaitAsync(() => vm.ConnectionState == "Reconnecting"),
                $"state stayed '{vm.ConnectionState}'");
            Assert.True(vm.IsPillWarn);
            Assert.False(vm.IsPillOk);
            Assert.Equal("Reconnecting", vm.BridgeRows[0].LinkStateText);
            Assert.Equal(BridgeLinkState.Reconnecting, Assert.Single(connections.LinkStatuses).State);
            Assert.Contains("lost", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

            // A tag is added on the bridge while the HMI cannot hear it: the missed delta
            // makes this invisible until the post-reconnect snapshot lands.
            using (HttpResponseMessage added = await handle.Client.PostAsync(
                "/api/mappings/add",
                JsonBody(new
                {
                    tags = new[]
                    {
                        new
                        {
                            sourceId = "default",
                            itemId = "While.Down",
                            displayName = "While Down",
                            dataType = "Int32",
                            mode = "Source",
                            accessRights = "Read",
                            enabled = true
                        }
                    }
                })))
            {
                Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            }

            // The link comes back: the HMI must rejoin and refresh its values on its own.
            forwarder.Start();

            Assert.True(
                await WaitAsync(() => vm.StatusMessage.Contains("values refreshed", StringComparison.OrdinalIgnoreCase)),
                $"status stayed '{vm.StatusMessage}'");
            Assert.True(vm.IsConnected);
            Assert.Equal("Connected", vm.ConnectionState);
            Assert.True(vm.IsPillOk);
            Assert.Equal("Connected", vm.BridgeRows[0].LinkStateText);
            Assert.Contains(vm.Tags, tag => tag.DisplayName == "While Down");
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task FailedBridge_DoesNotBlockTheHealthyOne_AndJoinsWhenItComesUp()
    {
        await using TestAppHandle handle = await TestAppHandle.StartAsync(WriteMinimalAppsettings);
        await using var forwarder = new TcpForwarder(new Uri(handle.Client.BaseAddress!.ToString()));
        forwarder.Start();
        forwarder.Stop();

        string configPath = TempConfigPath();
        var connections = new BridgeConnectionManager(FastOptions);
        var vm = new MainViewModel(connections, new PopupWindowService(), ownsServices: true, configPath: configPath);

        try
        {
            vm.BridgeRows[0].Address = handle.Client.BaseAddress!.ToString().TrimEnd('/');
            vm.AddBridgeCommand.Execute(null);
            vm.BridgeRows[^1].Name = "later";
            vm.BridgeRows[^1].Address = forwarder.BaseUrl;

            await vm.ConnectCommand.ExecuteAsync(null);

            // The healthy bridge connected; the unreachable one is reported, not fatal.
            Assert.True(vm.IsConnected);
            Assert.Contains("default", vm.BridgeSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1 unreachable", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Connected", vm.BridgeRows[0].LinkStateText);
            Assert.Equal("Failed", vm.BridgeRows[^1].LinkStateText);
            Assert.False(string.IsNullOrWhiteSpace(vm.BridgeRows[^1].LinkStateTooltip));

            IReadOnlyList<BridgeLinkStatus> statuses = connections.LinkStatuses;
            Assert.Equal(BridgeLinkState.Connected, statuses.Single(status => status.BridgeId == "default").State);
            BridgeLinkStatus failed = statuses.Single(status => status.BridgeId == "later");
            Assert.Equal(BridgeLinkState.Failed, failed.State);
            Assert.False(string.IsNullOrWhiteSpace(failed.Error));
            Assert.Equal("Failed", vm.ConnectionState);

            // When the second bridge comes up, the retry joins it without another Connect.
            forwarder.Start();

            Assert.True(
                await WaitAsync(() => connections.LinkStatuses.All(status => status.State == BridgeLinkState.Connected)),
                "the failed bridge never joined");
            Assert.Equal("Connected", vm.BridgeRows[^1].LinkStateText);
            Assert.True(vm.IsPillOk);
            Assert.Equal("Connected", vm.ConnectionState);
            Assert.Contains("later", vm.BridgeSummary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    [Fact]
    public async Task AllBridgesUnreachable_StillFailsTheConnect()
    {
        string configPath = TempConfigPath();
        var vm = new MainViewModel(new BridgeConnectionManager(FastOptions), new PopupWindowService(), ownsServices: true, configPath: configPath);

        try
        {
            vm.BridgeRows[0].Address = "http://127.0.0.1:59999";
            await vm.ConnectCommand.ExecuteAsync(null);

            Assert.False(vm.IsConnected);
            Assert.Equal("Disconnected", vm.ConnectionState);
            Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
            Assert.True(vm.IsPillBad);
        }
        finally
        {
            await vm.DisposeAsync();
            DeleteConfig(configPath);
        }
    }

    /// <summary>
    /// A raw TCP pass-through in front of the test bridge. Closing it drops the HMI's sockets
    /// exactly like a pulled network cable; starting it again lets the client reconnect to the
    /// same port. Plain TCP keeps SignalR's WebSocket transport intact.
    /// </summary>
    private sealed class TcpForwarder : IAsyncDisposable
    {
        private readonly string backendHost_;
        private readonly int backendPort_;
        private readonly List<TcpClient> clients_ = new();
        private readonly object sync_ = new();
        private TcpListener? listener_;
        private int port_;
        private volatile bool accepting_;

        public TcpForwarder(Uri backend)
        {
            backendHost_ = backend.Host;
            backendPort_ = backend.Port;
        }

        public string BaseUrl => $"http://127.0.0.1:{port_}";

        public void Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, port_);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            port_ = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener_ = listener;
            accepting_ = true;
            _ = Task.Run(() => AcceptLoopAsync(listener));
        }

        public void Stop()
        {
            accepting_ = false;
            listener_?.Stop();
            listener_ = null;
            lock (sync_)
            {
                foreach (TcpClient client in clients_)
                {
                    client.Dispose();
                }

                clients_.Clear();
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            while (accepting_)
            {
                TcpClient inbound;
                try
                {
                    inbound = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                _ = HandleClientAsync(inbound);
            }
        }

        private async Task HandleClientAsync(TcpClient inbound)
        {
            lock (sync_)
            {
                clients_.Add(inbound);
            }

            try
            {
                using (inbound)
                using (var outbound = new TcpClient())
                {
                    await outbound.ConnectAsync(backendHost_, backendPort_);
                    lock (sync_)
                    {
                        clients_.Add(outbound);
                    }

                    Task up = PumpAsync(inbound.GetStream(), outbound.GetStream());
                    Task down = PumpAsync(outbound.GetStream(), inbound.GetStream());
                    await Task.WhenAny(up, down);
                }
            }
            catch
            {
                // the peer went away; the pumps end on their own
            }
        }

        private static async Task PumpAsync(NetworkStream from, NetworkStream to)
        {
            byte[] buffer = new byte[8192];
            try
            {
                while (true)
                {
                    int read = await from.ReadAsync(buffer);
                    if (read <= 0)
                    {
                        return;
                    }

                    await to.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            catch
            {
                // connection closed
            }
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }
    }
}
