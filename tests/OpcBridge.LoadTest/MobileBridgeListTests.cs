using System.Net;
using System.Text;
using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class BridgeListCodecTests
{
    [Fact]
    public void RoundTripsTheSavedList()
    {
        SavedBridge[] bridges =
        {
            new("a1", "Line 1", "http://10.3.1.50:8080"),
            new("b2", "Packaging", "http://10.3.1.74:18080")
        };

        IReadOnlyList<SavedBridge> restored = BridgeListCodec.Deserialize(BridgeListCodec.Serialize(bridges));

        Assert.Equal(2, restored.Count);
        Assert.Equal("Line 1", restored[0].Name);
        Assert.Equal("http://10.3.1.74:18080", restored[1].Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"bridges\":[]}")]
    public void UnreadableStoresReadAsEmpty(string? json)
    {
        Assert.Empty(BridgeListCodec.Deserialize(json));
    }

    [Fact]
    public void EntriesWithoutAnAddressAreDropped()
    {
        IReadOnlyList<SavedBridge> restored = BridgeListCodec.Deserialize(
            "[{\"id\":\"a1\",\"name\":\"Line 1\",\"url\":\"http://10.0.0.1\"},{\"id\":\"b2\",\"name\":\"Ghost\",\"url\":\"\"}]");

        SavedBridge only = Assert.Single(restored);
        Assert.Equal("a1", only.Id);
    }

    [Fact]
    public void DefaultNameIsTheHostAndPortTheOperatorTyped()
    {
        Assert.Equal("10.3.1.50:8080", BridgeListCodec.DefaultName("http://10.3.1.50:8080"));
        Assert.Equal("10.3.1.50", BridgeListCodec.DefaultName("http://10.3.1.50/"));
    }

    [Fact]
    public void SameAddressIgnoresSchemeCaseAndTrailingSlash()
    {
        Assert.True(BridgeListCodec.SameAddress("http://10.3.1.50:8080", "http://10.3.1.50:8080/"));
        Assert.False(BridgeListCodec.SameAddress("http://10.3.1.50:8080", "http://10.3.1.50:8081"));
    }
}

public sealed class BridgeCoordinatorTests
{
    [Fact]
    public async Task AddingTwoBridgesKeepsBothAndSwitchesTheActiveOne()
    {
        using FakeBridge line1 = FakeBridge.Start("Line 1 interlock", "Permit", value: true);
        using FakeBridge line2 = FakeBridge.Start("Filler 02 interlock", "Filler.Permit", value: false);
        FakeSettings settings = new();
        using BridgeCoordinator coordinator = new(settings);

        MobileResult<BridgeConnection> first = await coordinator.AddOrConnectAsync($"127.0.0.1:{line1.Port}", "Line 1", CancellationToken.None);
        Assert.True(first.Ok);
        Assert.Equal("Line 1 interlock", first.Value!.Overview.Blocks[0].Name);
        Assert.True(first.Value.IsConnected);
        Assert.Same(first.Value, coordinator.Active);

        MobileResult<BridgeConnection> second = await coordinator.AddOrConnectAsync($"127.0.0.1:{line2.Port}", null, CancellationToken.None);
        Assert.True(second.Ok);
        Assert.Equal("Filler 02 interlock", second.Value!.Overview.Blocks[0].Name);

        // Both bridges stay on the phone; the second is the one being shown.
        Assert.Equal(2, coordinator.Bridges.Count);
        Assert.Same(second.Value, coordinator.Active);
        Assert.True(second.Value.IsActive);
        Assert.False(first.Value.IsActive);
        Assert.Equal($"127.0.0.1:{line2.Port}", second.Value.DisplayName);

        // Each bridge keeps its own tags: same item id, different live values.
        Assert.True(coordinator.Tags.TryGet(TagBindingKey.Create(first.Value.CacheKey, "sim", "Permit"), out MultiBridgeTagEntry? one));
        Assert.True(coordinator.Tags.TryGet(TagBindingKey.Create(second.Value.CacheKey, "sim", "Filler.Permit"), out MultiBridgeTagEntry? two));
        Assert.NotEqual(one!.SourceName, two!.SourceName);
    }

    [Fact]
    public async Task SwitchingBackKeepsTheFirstBridgesSnapshot()
    {
        using FakeBridge line1 = FakeBridge.Start("Line 1 interlock", "Permit", value: true);
        using FakeBridge line2 = FakeBridge.Start("Filler 02 interlock", "Filler.Permit", value: false);
        FakeSettings settings = new();
        using BridgeCoordinator coordinator = new(settings);

        BridgeConnection first = (await coordinator.AddOrConnectAsync($"127.0.0.1:{line1.Port}", "Line 1", CancellationToken.None)).Value!;
        BridgeConnection second = (await coordinator.AddOrConnectAsync($"127.0.0.1:{line2.Port}", "Filler", CancellationToken.None)).Value!;

        await coordinator.ActivateAsync(first, CancellationToken.None);

        Assert.Same(first, coordinator.Active);
        Assert.Equal("Line 1 interlock", first.Overview.Blocks[0].Name);
        Assert.Equal("Filler 02 interlock", second.Overview.Blocks[0].Name);
    }

    [Fact]
    public async Task TheSavedListAndActiveBridgeSurviveARestart()
    {
        using FakeBridge line1 = FakeBridge.Start("Line 1 interlock", "Permit", value: true);
        using FakeBridge line2 = FakeBridge.Start("Filler 02 interlock", "Filler.Permit", value: false);
        FakeSettings settings = new();

        string secondId;
        using (BridgeCoordinator coordinator = new(settings))
        {
            await coordinator.AddOrConnectAsync($"127.0.0.1:{line1.Port}", "Line 1", CancellationToken.None);
            BridgeConnection second = (await coordinator.AddOrConnectAsync($"127.0.0.1:{line2.Port}", "Filler", CancellationToken.None)).Value!;
            secondId = second.Bridge.Id;
        }

        using BridgeCoordinator restarted = new(settings);
        restarted.Load();

        Assert.Equal(2, restarted.Bridges.Count);
        Assert.Equal("Line 1", restarted.Bridges[0].Bridge.Name);
        Assert.Equal(secondId, restarted.Active!.Bridge.Id);
        // A restored bridge is not connected until the Logic tab opens.
        Assert.Equal("not connected", restarted.Active.StatusText);
    }

    [Fact]
    public async Task RemovingTheActiveBridgeFallsBackToTheNextOne()
    {
        using FakeBridge line1 = FakeBridge.Start("Line 1 interlock", "Permit", value: true);
        using FakeBridge line2 = FakeBridge.Start("Filler 02 interlock", "Filler.Permit", value: false);
        FakeSettings settings = new();
        using BridgeCoordinator coordinator = new(settings);

        BridgeConnection first = (await coordinator.AddOrConnectAsync($"127.0.0.1:{line1.Port}", "Line 1", CancellationToken.None)).Value!;
        BridgeConnection second = (await coordinator.AddOrConnectAsync($"127.0.0.1:{line2.Port}", "Filler", CancellationToken.None)).Value!;

        await coordinator.RemoveAsync(second);

        Assert.Single(coordinator.Bridges);
        Assert.Same(first, coordinator.Active);
        Assert.False(coordinator.Tags.TryGet(TagBindingKey.Create(second.CacheKey, "sim", "Filler.Permit"), out _));
        Assert.Empty(BridgeListCodec.Deserialize(settings.Get(BridgeCoordinator.BridgesKey, string.Empty)).Skip(1));
    }

    [Fact]
    public async Task AnAddressThatAnswersNothingIsReportedNotSaved()
    {
        using BridgeCoordinator coordinator = new(new FakeSettings());

        MobileResult<BridgeConnection> result = await coordinator.AddOrConnectAsync("127.0.0.1:17777", "Ghost", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no bridge answered", result.Error, StringComparison.Ordinal);
        Assert.Empty(coordinator.Bridges);
        Assert.Null(coordinator.Active);
    }

    [Fact]
    public async Task SignInFailureNamesTheBridgeError()
    {
        using FakeBridge bridge = FakeBridge.Start("Line 1 interlock", "Permit", value: true, loginStatus: 401, loginBody: "{\"error\":\"Invalid user or password.\"}");
        using BridgeCoordinator coordinator = new(new FakeSettings());
        BridgeConnection connection = (await coordinator.AddOrConnectAsync($"127.0.0.1:{bridge.Port}", "Line 1", CancellationToken.None)).Value!;

        MobileResult<MobileSession> result = await coordinator.SignInAsync(connection, "op", "wrong", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Invalid user or password.", result.Error);
        Assert.Equal(401, result.StatusCode);
        Assert.True(result.IsSignInRequired);
    }

    private sealed class FakeSettings : IBridgeSettings
    {
        private readonly Dictionary<string, string> values_ = new();

        public string Get(string key, string fallback) => values_.TryGetValue(key, out string? value) ? value : fallback;

        public void Set(string key, string value) => values_[key] = value;
    }

    /// <summary>A minimal bridge over HttpListener: the probe, the logic, the tags and the state.</summary>
    private sealed class FakeBridge : IDisposable
    {
        private readonly HttpListener listener_ = new();
        private readonly string blockName_;
        private readonly string itemId_;
        private readonly bool value_;
        private readonly int loginStatus_;
        private readonly string loginBody_;
        private readonly Guid blockId_ = Guid.NewGuid();
        private readonly Guid conditionId_ = Guid.NewGuid();

        private FakeBridge(string blockName, string itemId, bool value, int loginStatus, string loginBody)
        {
            blockName_ = blockName;
            itemId_ = itemId;
            value_ = value;
            loginStatus_ = loginStatus;
            loginBody_ = loginBody;
        }

        public int Port { get; private set; }

        public static FakeBridge Start(string blockName, string itemId, bool value, int loginStatus = 200, string loginBody = "{\"authenticated\":true,\"username\":\"op\",\"role\":\"Operator\"}")
        {
            FakeBridge bridge = new(blockName, itemId, value, loginStatus, loginBody);
            bridge.Port = OpcBridge.Core.PortHelper.FindAvailablePort(19100, 19199);
            bridge.listener_.Prefixes.Add($"http://127.0.0.1:{bridge.Port}/");
            bridge.listener_.Start();
            _ = Task.Run(bridge.ServeAsync);
            return bridge;
        }

        public void Dispose() => listener_.Stop();

        private async Task ServeAsync()
        {
            while (listener_.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener_.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                (int status, string body, string contentType) = Route(context.Request.Url!.AbsolutePath);
                context.Response.StatusCode = status;
                context.Response.ContentType = contentType;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private (int Status, string Body, string ContentType) Route(string path) => path switch
        {
            "/api/auth/me" => (200, "{\"authenticated\":false,\"authEnabled\":false}", "application/json"),
            "/api/auth/login" => (loginStatus_, loginBody_, "application/json"),
            "/api/logic" => (200, $"{{\"blocks\":[{{\"id\":\"{blockId_}\",\"name\":\"{blockName_}\",\"kind\":\"interlock\",\"enabled\":true," +
                $"\"conditions\":[{{\"id\":\"{conditionId_}\",\"text\":\"Permit must be given\",\"sourceId\":\"sim\",\"itemId\":\"{itemId_}\",\"op\":\"on\"}}]}}]}}", "application/json"),
            "/api/logic/state" => (200, $"{{\"blocks\":[{{\"id\":\"{blockId_}\",\"state\":\"{state_}\",\"conditions\":[{{\"id\":\"{conditionId_}\",\"state\":\"{state_}\"}}]}}]}}", "application/json"),
            "/api/hmi/tags" => (200, $"{{\"tags\":[{{\"sourceId\":\"sim\",\"sourceName\":\"{blockName_} source\",\"itemId\":\"{itemId_}\",\"displayName\":\"Permit\",\"digital\":true," +
                $"\"onText\":\"Permit\",\"offText\":\"Blocked\",\"value\":{value_.ToString().ToLowerInvariant()},\"isGood\":true}}]}}", "application/json"),
            // Anything else (the SignalR negotiate included) is refused: the link is allowed to fail.
            _ => (404, "{\"error\":\"not found\"}", "application/json")
        };

        private string state_ => value_ ? "true" : "false";
    }
}
