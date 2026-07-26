using System.Net;
using System.Net.Sockets;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class OpcUaBrowseServiceTimeoutTests
{
    private static OpcUaSourceClientOptions OptionsFor(string endpointUrl, int sessionTimeoutMs) => new()
    {
        EndpointUrl = endpointUrl,
        SecurityMode = "None",
        SecurityPolicy = "None",
        SessionTimeoutMs = sessionTimeoutMs,
        AutoAcceptUntrustedCertificates = true
    };

    /// <summary>
    /// Accepts TCP connections but never completes the UA handshake so discovery blocks until cancel.
    /// </summary>
    private sealed class BlackHoleTcpServer : IAsyncDisposable
    {
        private readonly TcpListener listener_;
        private readonly CancellationTokenSource accept_loop_cts_ = new();
        private readonly Task accept_loop_;

        public int Port { get; }

        private BlackHoleTcpServer(TcpListener listener, int port)
        {
            listener_ = listener;
            Port = port;
            accept_loop_ = AcceptLoopAsync(accept_loop_cts_.Token);
        }

        public static BlackHoleTcpServer Start()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new BlackHoleTcpServer(listener, port);
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client = await listener_.AcceptTcpClientAsync(cancellationToken)
                        .ConfigureAwait(false);
                    // Hold the socket open without responding so the UA stack waits on the timeout CTS.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        finally
                        {
                            client.Dispose();
                        }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            accept_loop_cts_.Cancel();
            listener_.Stop();
            try
            {
                await accept_loop_.ConfigureAwait(false);
            }
            catch
            {
            }

            accept_loop_cts_.Dispose();
        }
    }

    [Fact]
    public async Task TestConnection_CallerCancellation_PropagatesOCE()
    {
        OpcUaBrowseService service = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TestConnectionAsync(
                OptionsFor("opc.tcp://127.0.0.1:1", sessionTimeoutMs: 15_000),
                cts.Token));
    }

    [Fact]
    public async Task Browse_CallerCancellation_PropagatesOCE()
    {
        OpcUaBrowseService service = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.BrowseAsync(
                OptionsFor("opc.tcp://127.0.0.1:1", sessionTimeoutMs: 15_000),
                nodeId: "i=85",
                maxNodes: 5,
                cts.Token));
    }

    [Fact]
    public async Task TestConnection_OperationTimeout_ReturnsOkFalseTimeout()
    {
        await using BlackHoleTcpServer blackHole = BlackHoleTcpServer.Start();
        OpcUaBrowseService service = new();
        OpcUaSourceClientOptions options = OptionsFor(
            $"opc.tcp://127.0.0.1:{blackHole.Port}",
            sessionTimeoutMs: 500);

        UaTestConnectionResult result = await service.TestConnectionAsync(options, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Connection timed out.", result.Error);
    }

    [Fact]
    public async Task Browse_OperationTimeout_ReturnsBrowseTimedOut()
    {
        await using BlackHoleTcpServer blackHole = BlackHoleTcpServer.Start();
        OpcUaBrowseService service = new();
        OpcUaSourceClientOptions options = OptionsFor(
            $"opc.tcp://127.0.0.1:{blackHole.Port}",
            sessionTimeoutMs: 500);

        UaBrowseResult result = await service.BrowseAsync(
            options,
            nodeId: "i=85",
            maxNodes: 5,
            CancellationToken.None);

        Assert.Empty(result.Nodes);
        Assert.Equal("Browse timed out.", result.Error);
    }
}
