using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The phone finds the bridge from whatever the operator typed: a bare host, a host:port or a
/// full URL. With an explicit port only that one is probed; otherwise the known container
/// publish port and the bridge's own 8080–8180 range are walked.
/// </summary>
public sealed class BridgeProbeTests
{
    [Theory]
    [InlineData("10.3.1.50", "10.3.1.50", null)]
    [InlineData("http://10.3.1.50:8080", "10.3.1.50", 8080)]
    [InlineData("10.3.1.50:9090", "10.3.1.50", 9090)]
    [InlineData("  http://bridge.local  ", "bridge.local", null)]
    public void ParseHost_StripsSchemePathAndPort(string input, string expectedHost, int? expectedPort)
    {
        (string host, int? port) = BridgeProbe.ParseHost(input);

        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void ParseHost_IgnoresBlankInput()
    {
        Assert.Equal((string.Empty, (int?)null), BridgeProbe.ParseHost("   "));
        Assert.Equal((string.Empty, (int?)null), BridgeProbe.ParseHost(null));
    }

    [Fact]
    public async Task FindAsync_ReturnsTheAddressThatAnswersThePortProbe()
    {
        int port = ReservePort();
        using HttpListener listener = StartBridgeListener(port);
        try
        {
            string? found = await BridgeProbe.FindAsync($"127.0.0.1:{port}", timeoutMs: 500);

            Assert.Equal($"http://127.0.0.1:{port}", found);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FindAsync_ReturnsNullWhenNothingAnswers()
    {
        int port = ReservePort();

        string? found = await BridgeProbe.FindAsync($"127.0.0.1:{port}", timeoutMs: 200);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindAsync_RespectsTheSearchBudget()
    {
        Stopwatch clock = Stopwatch.StartNew();
        string? found = await BridgeProbe.FindAsync("127.0.0.1", timeoutMs: 2000, searchBudgetMs: 1);
        clock.Stop();

        Assert.Null(found);
        Assert.True(clock.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public async Task FindAsync_ReturnsAtTheSearchBudgetWhenAProbeHangs()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    await Task.Delay(5000);
                }
                catch (Exception)
                {
                    // Listener stopped with the test.
                }
            });

            Stopwatch clock = Stopwatch.StartNew();
            string? found = await BridgeProbe.FindAsync(
                $"127.0.0.1:{port}",
                timeoutMs: 5000,
                searchBudgetMs: 600);
            clock.Stop();

            Assert.Null(found);
            Assert.True(clock.ElapsedMilliseconds < 3000);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FindAsync_StopsWhenTheSearchIsCancelled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BridgeProbe.FindAsync("127.0.0.1:19000", timeoutMs: 100, cts.Token));
    }

    [Fact]
    public async Task FindAsync_FindsAuthEnabledBridgeWhosePortProbeIsGated()
    {
        int port = ReservePort();
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext context = await listener.GetContextAsync();
                        // Auth is enabled: /api/status/ports wants a session, /api/auth/me does not.
                        if (context.Request.Url!.AbsolutePath == "/api/status/ports")
                        {
                            context.Response.StatusCode = 401;
                            context.Response.Close();
                            continue;
                        }

                        byte[] body = Encoding.UTF8.GetBytes("{\"authenticated\":false,\"authEnabled\":true}");
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(body);
                        context.Response.Close();
                    }
                }
                catch (Exception)
                {
                    // Listener stopped with the test.
                }
            });

            string? found = await BridgeProbe.FindAsync($"127.0.0.1:{port}", timeoutMs: 500);

            Assert.Equal($"http://127.0.0.1:{port}", found);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task IsBridgeAsync_TreatsAClosedSocketAsNotABridge()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    // Read the request, then abort before any response: Android's HTTP handler
                    // reports this as WebException("Socket closed"), not HttpRequestException.
                    byte[] request = new byte[1024];
                    await client.GetStream().ReadAsync(request);
                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Close();
                }
                catch (Exception)
                {
                    // Listener stopped with the test.
                }
            });

            Assert.False(await BridgeProbe.IsBridgeAsync($"http://127.0.0.1:{port}", timeoutMs: 500));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task IsBridgeAsync_RejectsNonBridgeEndpoints()
    {
        int port = ReservePort();
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        try
        {
            _ = Task.Run(async () =>
            {
                HttpListenerContext context = await listener.GetContextAsync();
                context.Response.StatusCode = 404;
                context.Response.Close();
            });

            Assert.False(await BridgeProbe.IsBridgeAsync($"http://127.0.0.1:{port}", timeoutMs: 500));
            Assert.False(await BridgeProbe.IsBridgeAsync(""));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static HttpListener StartBridgeListener(int port)
    {
        HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    byte[] body = Encoding.UTF8.GetBytes("{\"httpPort\":" + port + ",\"authEnabled\":false}");
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            }
            catch (Exception)
            {
                // Listener stopped with the test.
            }
        });
        return listener;
    }

    private static int ReservePort() => OpcBridge.Core.PortHelper.FindAvailablePort(19000, 19099);
}
