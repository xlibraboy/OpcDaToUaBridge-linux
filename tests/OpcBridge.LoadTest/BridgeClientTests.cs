using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpcBridge.Core;
using OpcBridge.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The app's server-side bridge client: the configured URL wins over discovery, calls and
/// responses pass through verbatim (status and body, in both directions), a dead bridge reads
/// as the 503 error shape the SPA renders, and discovery accepts only endpoints that answer
/// the bridge's anonymous identity probe.
/// </summary>
public sealed class BridgeClientTests
{
    [Fact]
    public async Task ForwardAsync_PassesTheBridgesStatusAndBodyThroughVerbatim()
    {
        int port = ReservePort();
        using FakeBridgeListener bridge = new(port, context =>
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"error\":\"Block name already exists.\"}");
            context.Response.StatusCode = 409;
            context.Response.ContentType = "application/json";
            return WriteAsync(context, body);
        });

        using BridgeClient client = CreateClient($"http://127.0.0.1:{port}");
        ForwardedResponse response = await client.ForwardAsync(HttpMethod.Post, "/api/logic/blocks", "{}", "application/json", CancellationToken.None);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("Block name already exists.", Encoding.UTF8.GetString(response.Body));
        Assert.Equal($"http://127.0.0.1:{port}", client.BaseUrl);
    }

    [Fact]
    public async Task ForwardAsync_SendsTheRequestBodyVerbatim()
    {
        int port = ReservePort();
        string? receivedMethod = null;
        string? receivedBody = null;
        using FakeBridgeListener bridge = new(port, async context =>
        {
            receivedMethod = context.Request.HttpMethod;
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            receivedBody = await reader.ReadToEndAsync();
            await WriteAsync(context, Encoding.UTF8.GetBytes("{\"blocks\":[]}"));
        });

        using BridgeClient client = CreateClient($"http://127.0.0.1:{port}");
        ForwardedResponse response = await client.ForwardAsync(HttpMethod.Post, "/api/logic/blocks", "{\"block\":{\"name\":\"x\"}}", "application/json", CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("POST", receivedMethod);
        Assert.Equal("{\"block\":{\"name\":\"x\"}}", receivedBody);
    }

    [Fact]
    public async Task ForwardAsync_ReportsUnreachableAsThe503ErrorShape()
    {
        int port = ReservePort();
        using BridgeClient client = CreateClient($"http://127.0.0.1:{port}");

        ForwardedResponse response = await client.ForwardAsync(HttpMethod.Get, "/api/logic", null, null, CancellationToken.None);

        Assert.Equal(503, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(response.Body));
        string error = body.RootElement.GetProperty("error").GetString() ?? string.Empty;
        Assert.Contains("unreachable", error);
        Assert.Contains(port.ToString(), error);
    }

    [Fact]
    public async Task Discover_FindsTheBridgeThatAnswersTheIdentityProbe()
    {
        int bridgePort = ReservePort();
        int otherPort = ReservePort();
        using FakeBridgeListener bridge = new(bridgePort, IdentityHandler());
        using FakeBridgeListener other = new(otherPort, context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });

        using HttpClient probe = new() { Timeout = Timeout.InfiniteTimeSpan };
        string? found = await BridgeLocator.DiscoverAsync(probe, NullLogger.Instance, new[] { otherPort, bridgePort }, CancellationToken.None);

        Assert.Equal($"http://127.0.0.1:{bridgePort}", found);
    }

    [Fact]
    public async Task Discover_PicksTheLowestAnsweringPortWhenSeveralBridgesReply()
    {
        int lower = ReservePort();
        int higher = ReservePort();
        using FakeBridgeListener first = new(lower, IdentityHandler());
        using FakeBridgeListener second = new(higher, IdentityHandler());

        using HttpClient probe = new() { Timeout = Timeout.InfiniteTimeSpan };
        // Handed out of order on purpose: the sweep is parallel, so "first responder" would
        // otherwise be whichever probe finished fastest, not the lowest bridge.
        string? found = await BridgeLocator.DiscoverAsync(probe, NullLogger.Instance, new[] { higher, lower }, CancellationToken.None);

        Assert.Equal($"http://127.0.0.1:{lower}", found);
    }

    [Fact]
    public async Task Discover_ReturnsNullWhenNoCandidateAnswersTheProbe()
    {
        int port = ReservePort();
        using FakeBridgeListener other = new(port, context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });

        using HttpClient probe = new() { Timeout = Timeout.InfiniteTimeSpan };
        string? found = await BridgeLocator.DiscoverAsync(probe, NullLogger.Instance, new[] { port }, CancellationToken.None);

        Assert.Null(found);
    }

    private static BridgeClient CreateClient(string baseUrl)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bridge:BaseUrl"] = baseUrl })
            .Build();
        return new BridgeClient(configuration, NullLogger<BridgeClient>.Instance);
    }

    private static Func<HttpListenerContext, Task> IdentityHandler() => context =>
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"authenticated\":false,\"authEnabled\":true}");
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        return WriteAsync(context, body);
    };

    private static async Task WriteAsync(HttpListenerContext context, byte[] body)
    {
        context.Response.ContentType ??= "application/json";
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static int next_candidate_port_ = 19600;

    private static int ReservePort()
    {
        while (true)
        {
            int candidate = Interlocked.Increment(ref next_candidate_port_);
            if (candidate > 19799)
            {
                throw new InvalidOperationException("Ran out of ports for the bridge-client tests.");
            }

            if (PortHelper.IsPortAvailable(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A loopback endpoint that answers every request through the given handler.</summary>
    private sealed class FakeBridgeListener : IDisposable
    {
        private readonly HttpListener listener_ = new();

        public FakeBridgeListener(int port, Func<HttpListenerContext, Task> handler)
        {
            listener_.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener_.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (listener_.IsListening)
                    {
                        HttpListenerContext context = await listener_.GetContextAsync();
                        try
                        {
                            await handler(context);
                        }
                        catch (Exception)
                        {
                            // A handler that fails closes its own response; keep serving the rest.
                            try
                            {
                                context.Response.Close();
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // The listener is stopped with the test.
                }
            });
        }

        public void Dispose()
        {
            try
            {
                listener_.Stop();
            }
            catch (Exception)
            {
            }
        }
    }
}
