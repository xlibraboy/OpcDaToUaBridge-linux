using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The plant access point gate: live data is allowed only while the phone is associated with
/// the exact BSSID bound in the app, and the gate is asked before any socket is opened.
/// </summary>
public sealed class MobileAccessPointTests
{
    private const string BoundBssid = "A4:2B:8C:11:9F:20";
    private const string OtherBssid = "A4:2B:8C:11:9F:21";

    [Fact]
    public void Unbound_KeepsOnlineClosed()
    {
        FakeSettings settings = new();
        FakeNetwork network = new();
        using AccessPointGate gate = new(settings, new MemorySecretStore(), network);

        gate.Load();

        Assert.Null(gate.Binding);
        Assert.Equal(AccessPointGateState.NotBound, gate.Status.State);
        Assert.False(gate.IsOnlineAllowed);
    }

    [Fact]
    public async Task Binding_StoresTheBssidAndSecret_AndOpensOnlyOnThatBssid()
    {
        FakeSettings settings = new();
        MemorySecretStore secrets = new();
        FakeNetwork network = new();
        using AccessPointGate gate = new(settings, secrets, network);
        gate.Load();

        MobileResult<AccessPointGateStatus> result = await gate.BindAsync(
            new SavedAccessPoint("PLANT-AP-01", "a4-2b-8c-11-9f-20", AccessPointSecurity.Wpa2),
            "plant-secret",
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("A4:2B:8C:11:9F:20", gate.Binding!.Bssid);
        Assert.Equal("plant-secret", network.LastPassword);
        Assert.Equal("plant-secret", secrets.Get(AccessPointCodec.SecretKey(BoundBssid)));
        Assert.True(gate.HasStoredPassword());
        Assert.True(gate.IsOnlineAllowed);

        // The passphrase must not land in the ordinary settings JSON.
        Assert.DoesNotContain("plant-secret", settings.Get(AccessPointCodec.BindingKey, string.Empty));
    }

    [Fact]
    public async Task SameSsidOnAnotherBssid_ClosesTheGate()
    {
        FakeSettings settings = new();
        FakeNetwork network = new();
        using AccessPointGate gate = new(settings, new MemorySecretStore(), network);
        gate.Load();
        await gate.BindAsync(
            new SavedAccessPoint("PLANT-AP-01", BoundBssid, AccessPointSecurity.Wpa2),
            "plant-secret",
            CancellationToken.None);

        network.Set(new AccessPointConnection(true, false, true, true, "PLANT-AP-01", OtherBssid));

        Assert.Equal(AccessPointGateState.ConnectedToOther, gate.Status.State);
        Assert.False(gate.IsOnlineAllowed);
        Assert.Contains("not the bound access point", gate.Status.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedBinding_ReadsAsNotBound()
    {
        FakeSettings settings = new();
        settings.Set(AccessPointCodec.BindingKey, "{\"ssid\":\"PLANT-AP-01\",\"bssid\":\"nope\"}");
        using AccessPointGate gate = new(settings, new MemorySecretStore(), new FakeNetwork());

        gate.Load();

        Assert.Null(gate.Binding);
        Assert.Equal(AccessPointGateState.NotBound, gate.Status.State);
    }

    [Fact]
    public async Task Forget_ClearsTheBindingAndSecret()
    {
        FakeSettings settings = new();
        MemorySecretStore secrets = new();
        FakeNetwork network = new();
        using AccessPointGate gate = new(settings, secrets, network);
        gate.Load();
        await gate.BindAsync(
            new SavedAccessPoint("PLANT-AP-01", BoundBssid, AccessPointSecurity.Wpa2),
            "plant-secret",
            CancellationToken.None);

        await gate.ForgetAsync();

        Assert.Null(gate.Binding);
        Assert.Null(secrets.Get(AccessPointCodec.SecretKey(BoundBssid)));
        Assert.False(gate.HasStoredPassword());
        Assert.Equal(string.Empty, settings.Get(AccessPointCodec.BindingKey, "missing"));
        Assert.True(network.DisconnectCalled);
        Assert.False(gate.IsOnlineAllowed);
    }

    [Fact]
    public async Task Coordinator_ProbesBeforeProvisioning_ButRefusesOnABoundWrongAp()
    {
        using FakeBridge bridge = FakeBridge.Start("Line 1 interlock", "Permit", value: true);
        FakeSettings settings = new();
        FakeNetwork network = new();
        using AccessPointGate gate = new(settings, new MemorySecretStore(), network);
        gate.Load();
        using BridgeCoordinator coordinator = new(settings, accessPointGate: gate);

        // With no AP provisioned, the installer can find and save a bridge on the plant
        // network; the connection still carries no live data until the BSSID is bound.
        MobileResult<BridgeConnection> added = await coordinator.AddOrConnectAsync(
            bridge.Url,
            "Line 1",
            CancellationToken.None);
        Assert.True(added.Ok, added.Error);
        Assert.Single(coordinator.Bridges);
        Assert.True(added.Value!.IsOffline);

        await gate.BindAsync(
            new SavedAccessPoint("PLANT-AP-01", BoundBssid, AccessPointSecurity.Wpa2),
            "plant-secret",
            CancellationToken.None);
        network.Set(new AccessPointConnection(true, false, true, true, "PLANT-AP-01", OtherBssid));

        MobileResult<BridgeConnection> refused = await coordinator.AddOrConnectAsync(
            bridge.Url,
            "Line 1",
            CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("plant access point", refused.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignIn_IsAllowedBeforeProvisioning_ButNoLiveDataIsRead()
    {
        using FakeBridge bridge = FakeBridge.Start("Line 1 interlock", "Permit", value: true);
        FakeSettings settings = new();
        using AccessPointGate gate = new(settings, new MemorySecretStore(), new FakeNetwork());
        gate.Load();
        using BridgeConnection connection = new(
            new SavedBridge("b1", "Line 1", bridge.Url),
            new MultiBridgeTagCache(),
            accessPointGate: gate);

        MobileResult<MobileSession> result = await connection.SignInAsync("op", "secret", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Value!.Authenticated);
        Assert.True(connection.IsOffline);
        Assert.False(connection.OnlineAllowed);
    }

    [Fact]
    public void ControlRoles_AreEngineerAndAdmin()
    {
        Assert.True(new MobileSession(true, "a", "Admin One", "Admin", true).IsControlUser);
        Assert.True(new MobileSession(true, "e", "Engineer One", "Engineer", true).IsControlUser);
        Assert.False(new MobileSession(true, "o", "Operator One", "Operator", true).IsControlUser);
        Assert.False(new MobileSession(true, "v", "Viewer One", "Viewer", true).IsControlUser);
    }

    [Fact]
    public async Task ApiClient_RefusesEveryRequestWhenTheGateIsClosed()
    {
        using LogicApiClient client = new()
        {
            CanGoOnline = () => false
        };
        client.SetBaseAddress("http://127.0.0.1:1");

        MobileResult<LogicApiClient.SessionResponse> result = await client.GetSessionAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(AccessPointGate.BlockedText, result.Error);
    }

    [Fact]
    public async Task Connection_MarksOfflineWithoutProbingWhenTheGateIsClosed()
    {
        FakeSettings settings = new();
        using AccessPointGate gate = new(settings, new MemorySecretStore(), new FakeNetwork());
        gate.Load();
        using BridgeConnection connection = new(
            new SavedBridge("b1", "Line 1", "http://127.0.0.1:1"),
            new MultiBridgeTagCache(),
            accessPointGate: gate);

        await connection.ConnectAsync(CancellationToken.None);

        Assert.True(connection.IsOffline);
        Assert.Equal(AccessPointGate.BlockedText, connection.StatusText);
        Assert.StartsWith("offline · " + AccessPointGate.BlockedText, connection.OfflineText);
    }

    [Fact]
    public async Task Coordinator_SuspendsTheActiveBridgeWhenThePhoneLeavesTheBoundAp()
    {
        using FakeBridge bridge = FakeBridge.Start("Line 1 interlock", "Permit", value: true);
        FakeSettings settings = new();
        FakeNetwork network = new();
        using AccessPointGate gate = new(settings, new MemorySecretStore(), network);
        gate.Load();
        await gate.BindAsync(
            new SavedAccessPoint("PLANT-AP-01", BoundBssid, AccessPointSecurity.Wpa2),
            "plant-secret",
            CancellationToken.None);
        using BridgeCoordinator coordinator = new(settings, accessPointGate: gate);

        MobileResult<BridgeConnection> added = await coordinator.AddOrConnectAsync(
            bridge.Url,
            "Line 1",
            CancellationToken.None);
        Assert.True(added.Ok, added.Error);
        Assert.True(added.Value!.IsConnected);

        network.Set(new AccessPointConnection(true, false, true, true, "PLANT-AP-01", OtherBssid));

        await WaitUntilAsync(() => added.Value.IsOffline, TimeSpan.FromSeconds(5));
        Assert.Equal(AccessPointGate.BlockedText, added.Value.StatusText);
        Assert.Contains("not on the plant access point", added.Value.OfflineText, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "condition was not met before the timeout");
    }

    private sealed class FakeSettings : IBridgeSettings
    {
        private readonly Dictionary<string, string> values_ = new(StringComparer.Ordinal);

        public string Get(string key, string fallback) => values_.TryGetValue(key, out string? value) ? value : fallback;

        public void Set(string key, string value) => values_[key] = value;
    }

    private sealed class FakeNetwork : IAccessPointNetwork
    {
        private AccessPointConnection current_ = new(true, false, false, true, string.Empty, string.Empty);

        public event Action? Changed;

        public AccessPointConnection Current => current_;

        public string? LastPassword { get; private set; }

        public bool DisconnectCalled { get; private set; }

        public void Refresh()
        {
        }

        public Task<MobileResult<IReadOnlyList<NearbyAccessPoint>>> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult<MobileResult<IReadOnlyList<NearbyAccessPoint>>>(
                MobileResult<IReadOnlyList<NearbyAccessPoint>>.Success(new List<NearbyAccessPoint>()));

        public Task<MobileResult<AccessPointConnection>> ConnectAsync(
            SavedAccessPoint accessPoint,
            string? password,
            CancellationToken cancellationToken)
        {
            LastPassword = password;
            Set(new AccessPointConnection(true, false, true, true, accessPoint.Ssid, accessPoint.Bssid));
            return Task.FromResult(MobileResult<AccessPointConnection>.Success(current_));
        }

        public Task DisconnectAsync()
        {
            DisconnectCalled = true;
            Set(new AccessPointConnection(true, false, false, true, string.Empty, string.Empty));
            return Task.CompletedTask;
        }

        public void Set(AccessPointConnection connection)
        {
            current_ = connection;
            Changed?.Invoke();
        }
    }
}
