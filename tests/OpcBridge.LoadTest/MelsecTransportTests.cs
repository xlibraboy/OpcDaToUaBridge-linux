using System.Text;
using OpcBridge.Drivers.Melsec.Transport;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MelsecTransportTests
{
    [Fact]
    public async Task Scripted_Open_Transact_RecordsRequest_AndReturnsResponse()
    {
        await using var transport = new ScriptedMelsecTransport();
        var request = Encoding.ASCII.GetBytes("\u000500FFWR0D01000001XX\r");
        var response = Encoding.ASCII.GetBytes("\u0006\u00020012\u0003AB\r");
        transport.Responses.Enqueue(response);

        Assert.False(transport.IsOpen);
        await transport.OpenAsync(CancellationToken.None);
        Assert.True(transport.IsOpen);

        var actual = await transport.TransactAsync(request, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(response, actual);
        Assert.Single(transport.Requests);
        Assert.Equal(request, transport.Requests[0]);
    }

    [Fact]
    public async Task Scripted_Transact_WithoutResponse_ThrowsTimeout()
    {
        await using var transport = new ScriptedMelsecTransport();
        await transport.OpenAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            transport.TransactAsync(
                Encoding.ASCII.GetBytes("ping"),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None));
    }

    [Fact]
    public async Task Scripted_Close_ClearsIsOpen()
    {
        await using var transport = new ScriptedMelsecTransport();
        await transport.OpenAsync(CancellationToken.None);
        Assert.True(transport.IsOpen);

        await transport.CloseAsync(CancellationToken.None);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task Scripted_MultipleResponses_AreDequeuedInOrder()
    {
        await using var transport = new ScriptedMelsecTransport();
        await transport.OpenAsync(CancellationToken.None);

        var first = new byte[] { 0x06, 0x0D };
        var second = Encoding.ASCII.GetBytes("\u0002DATA\u0003FF\r");
        transport.Responses.Enqueue(first);
        transport.Responses.Enqueue(second);

        var a = await transport.TransactAsync(new byte[] { 1 }, TimeSpan.FromSeconds(1), CancellationToken.None);
        var b = await transport.TransactAsync(new byte[] { 2 }, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(first, a);
        Assert.Equal(second, b);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(new byte[] { 1 }, transport.Requests[0]);
        Assert.Equal(new byte[] { 2 }, transport.Requests[1]);
    }
}

/// <summary>
/// In-memory <see cref="IMelsecTransport"/> for client unit tests: queues responses and records requests.
/// </summary>
internal sealed class ScriptedMelsecTransport : IMelsecTransport
{
    public Queue<byte[]> Responses { get; } = new();

    public List<byte[]> Requests { get; } = new();

    public bool IsOpen { get; private set; }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsOpen = false;
        return Task.CompletedTask;
    }

    public Task<byte[]> TransactAsync(
        ReadOnlyMemory<byte> request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.ToArray());
        if (Responses.Count == 0)
        {
            throw new TimeoutException("No scripted response");
        }

        return Task.FromResult(Responses.Dequeue());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
