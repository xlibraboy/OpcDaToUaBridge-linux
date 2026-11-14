using System.Net;
using Microsoft.AspNetCore.Http;
using OpcBridge.App.Auth;
using OpcBridge.App.Presence;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class ClientPresenceTests
{
    private const string EdgeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    [Fact]
    public void Reader_NamesBrowserByAddressAndUserAgent()
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?client=browser&clientId=browser-1");
        http.Request.Headers.UserAgent = EdgeOnWindows;
        http.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.168.1.20");
        http.Items[AuthContextKeys.Username] = "engineer";
        http.Items[AuthContextKeys.Role] = UserRole.Engineer;

        ClientPresenceConnection connection = ClientPresenceReader.Read("conn-1", http);

        Assert.Equal("browser", connection.Kind);
        Assert.Equal("browser-1", connection.ClientId);
        Assert.Equal("192.168.1.20", connection.Address);
        Assert.Equal("engineer", connection.Username);
        Assert.Equal("Windows 10/11 · Edge 131", connection.Platform);
        Assert.Equal(string.Empty, connection.MachineName);
    }

    [Fact]
    public void Reader_TakesTheHmiIdentityFromItsQuery()
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(
            "?client=hmi&clientId=hmi-1&machine=DESKTOP-PLANT&os=Microsoft%20Windows%2010.0.19045&version=1.8.2");

        ClientPresenceConnection connection = ClientPresenceReader.Read("conn-1", http);

        Assert.Equal("hmi", connection.Kind);
        Assert.Equal("hmi-1", connection.ClientId);
        Assert.Equal("DESKTOP-PLANT", connection.MachineName);
        Assert.Equal("Windows 10.0.19045", connection.Platform);
        Assert.Equal("1.8.2", connection.AppVersion);
        Assert.Equal(string.Empty, connection.Username);
    }

    [Fact]
    public void Reader_FallsBackToConnectionIdAndInfersBrowserKind()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = EdgeOnWindows;

        ClientPresenceConnection connection = ClientPresenceReader.Read("connection-42", http);

        Assert.Equal("connection-42", connection.ClientId);
        Assert.Equal("browser", connection.Kind);
        Assert.Equal("Windows 10/11 · Edge 131", connection.Platform);
    }

    [Fact]
    public void Reader_CapsAndRejectsUnclaimedValues()
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(
            "?client=admin&machine=" + new string('M', 120) + "&version=" + new string('9', 60));

        ClientPresenceConnection connection = ClientPresenceReader.Read("conn-1", http);

        Assert.Equal("unknown", connection.Kind);
        Assert.Equal(80, connection.MachineName.Length);
        Assert.Equal(40, connection.AppVersion.Length);
    }

    [Fact]
    public void Platform_DescribesAndroidAndFirefox()
    {
        const string android =
            "Mozilla/5.0 (Linux; Android 15; moto g06 power) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";
        const string firefox =
            "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0";

        Assert.Equal("Android 15 · Chrome 131", ClientPlatform.Describe(android));
        Assert.Equal("Linux · Firefox 132", ClientPlatform.Describe(firefox));
    }

    [Fact]
    public void Store_CollapsesConnectionsAndDropsTheClientWithItsLastLink()
    {
        var store = new ClientPresenceStore();
        ClientPresenceConnection browser = Browser("conn-1", "browser-1", "op1");

        store.Connect(browser);
        Assert.Equal(1, store.Count);
        ClientPresenceEntry first = Assert.Single(store.Snapshot());
        Assert.Equal(1, first.ConnectionCount);

        // An automatic reconnect is a second link for the same app instance, not a new client.
        store.Connect(browser with { ConnectionId = "conn-2" });
        Assert.Equal(1, store.Count);
        Assert.Equal(2, Assert.Single(store.Snapshot()).ConnectionCount);

        store.Disconnect("conn-1");
        Assert.Equal(1, Assert.Single(store.Snapshot()).ConnectionCount);

        store.Disconnect("conn-2");
        Assert.Equal(0, store.Count);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Store_KeepsAccountsSharingABrowserApart()
    {
        var store = new ClientPresenceStore();

        store.Connect(Browser("conn-1", "browser-1", "operator-a"));
        store.Connect(Browser("conn-2", "browser-1", "operator-b"));

        Assert.Equal(2, store.Count);
        Assert.Equal(
            new[] { "operator-a", "operator-b" },
            store.Snapshot().Select(entry => entry.Username).OrderBy(name => name).ToArray());
    }

    [Fact]
    public void Store_OrdersLiveClientsByKindThenMachine()
    {
        var store = new ClientPresenceStore();
        store.Connect(Browser("conn-1", "browser-1", "operator"));
        store.Connect(Hmi("conn-2", "hmi-1", "DESKTOP-B"));
        store.Connect(Hmi("conn-3", "hmi-2", "DESKTOP-A"));

        Assert.Equal(
            new[] { "hmi", "hmi", "browser" },
            store.Snapshot().Select(entry => entry.Kind).ToArray());
        Assert.Equal("DESKTOP-A", store.Snapshot()[0].MachineName);
    }

    [Fact]
    public void IdentityQuery_EncodesBlankFieldsAway()
    {
        string query = ClientIdentity.HubQuery("logic", "id 1", "PC & 1", string.Empty, null);

        Assert.Contains("client=logic", query);
        Assert.Contains("clientId=id%201", query);
        Assert.Contains("machine=PC%20%26%201", query);
        Assert.DoesNotContain("os=", query);
        Assert.DoesNotContain("version=", query);
        Assert.Equal("/hmi?client=logic", ClientIdentity.AppendQuery("/hmi", "client=logic"));
        Assert.Equal("/hmi?x=1&client=logic", ClientIdentity.AppendQuery("/hmi?x=1", "client=logic"));
    }

    private static ClientPresenceConnection Browser(string connectionId, string clientId, string username) =>
        new(connectionId, clientId + "\u001fbrowser\u001f" + username, clientId, "browser", string.Empty,
            "192.168.1.20", username, "Windows 10/11 · Edge 131", EdgeOnWindows, string.Empty);

    private static ClientPresenceConnection Hmi(string connectionId, string clientId, string machine) =>
        new(connectionId, clientId + "\u001fhmi\u001f", clientId, "hmi", machine,
            "192.168.1.30", string.Empty, "Windows 10.0.19045", string.Empty, "1.8.2");
}
