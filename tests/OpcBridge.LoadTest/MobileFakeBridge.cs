using System.Net;
using System.Text;

namespace OpcBridge.LoadTest;

/// <summary>
/// A minimal bridge over HttpListener: the probe, the logic, the tags and the state. Shared by
/// the mobile tests that need a real HTTP round-trip without the full bridge host.
/// </summary>
internal sealed class FakeBridge : IDisposable
{
    /// <summary>Test classes run in parallel; two fakes must never race for the same port.</summary>
    private static readonly object PortGate = new();

    private HttpListener listener_ = new();
    private readonly string itemId_;
    private readonly bool value_;
    private readonly int loginStatus_;
    private readonly string loginBody_;
    private readonly Guid blockId_ = Guid.NewGuid();
    private readonly Guid conditionId_ = Guid.NewGuid();

    private FakeBridge(string blockName, string itemId, bool value, int loginStatus, string loginBody)
    {
        BlockName = blockName;
        itemId_ = itemId;
        value_ = value;
        loginStatus_ = loginStatus;
        loginBody_ = loginBody;
    }

    public int Port { get; private set; }

    /// <summary>The block <c>/api/logic</c> serves; a test changes it to prove a resync replaces the dataset.</summary>
    public string BlockName { get; set; }

    /// <summary>Status <c>/api/logic</c> answers; a test flips it to 401 to expire a session.</summary>
    public int LogicStatus { get; set; } = 200;

    public static FakeBridge Start(string blockName, string itemId, bool value, int loginStatus = 200, string loginBody = "{\"authenticated\":true,\"username\":\"op\",\"role\":\"Operator\"}")
    {
        lock (PortGate)
        {
            FakeBridge bridge = new(blockName, itemId, value, loginStatus, loginBody);
            for (int attempt = 0; ; attempt++)
            {
                bridge.Port = OpcBridge.Core.PortHelper.FindAvailablePort(19100, 19199);
                bridge.listener_ = new HttpListener();
                bridge.listener_.Prefixes.Add($"http://127.0.0.1:{bridge.Port}/");
                try
                {
                    bridge.listener_.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 50)
                {
                    // A port check and a listener registration are two steps; another test
                    // can land on the same port between them. Take the next free one.
                    bridge.listener_.Close();
                }
            }

            _ = Task.Run(bridge.ServeAsync);
            return bridge;
        }
    }

    public string Url => "http://127.0.0.1:" + Port;

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
        "/api/auth/logout" => (200, "{\"authenticated\":false}", "application/json"),
        "/api/logic" => LogicStatus == 200
            ? (200, $"{{\"blocks\":[{{\"id\":\"{blockId_}\",\"name\":\"{BlockName}\",\"kind\":\"interlock\",\"enabled\":true," +
                $"\"conditions\":[{{\"id\":\"{conditionId_}\",\"text\":\"Permit must be given\",\"sourceId\":\"sim\",\"itemId\":\"{itemId_}\",\"op\":\"on\"}}]}}]}}", "application/json")
            : (LogicStatus, "{\"error\":\"Sign in required.\"}", "application/json"),
        "/api/logic/state" => (200, $"{{\"blocks\":[{{\"id\":\"{blockId_}\",\"state\":\"{state_}\",\"conditions\":[{{\"id\":\"{conditionId_}\",\"state\":\"{state_}\"}}]}}]}}", "application/json"),
        "/api/logic/notes" => (200, "{\"notes\":[]}", "application/json"),
        "/api/hmi/tags" => (200, $"{{\"tags\":[{{\"sourceId\":\"sim\",\"sourceName\":\"{BlockName} source\",\"itemId\":\"{itemId_}\",\"displayName\":\"Permit\",\"digital\":true," +
            $"\"onText\":\"Permit\",\"offText\":\"Blocked\",\"value\":{value_.ToString().ToLowerInvariant()},\"isGood\":true}}]}}", "application/json"),
        // Anything else (the SignalR negotiate included) is refused: the link is allowed to fail.
        _ => (404, "{\"error\":\"not found\"}", "application/json")
    };

    private string state_ => value_ ? "true" : "false";
}
