using System.IO.Ports;
using OpcBridge.Drivers.S7.Transport;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class S7TransportTests
{
    [Fact]
    public void SerialS7Transport_Ctor_RejectsEmptyPort()
    {
        Assert.Throws<ArgumentException>(() =>
            new SerialS7Transport("", 9600, 8, Parity.Even, StopBits.One));
    }

    [Fact]
    public void SerialS7Transport_Ctor_RejectsBadBaud()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SerialS7Transport("/dev/ttyUSB0", 0, 8, Parity.Even, StopBits.One));
    }

    [Fact]
    public void SerialS7Transport_Ctor_RejectsBadDataBits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SerialS7Transport("/dev/ttyUSB0", 9600, 9, Parity.Even, StopBits.One));
    }

    [Fact]
    public async Task Scripted_Open_Write_Read_RecordsAndReturns()
    {
        await using var transport = new ScriptedS7Transport();
        var request = new byte[] { 0x68, 0x03, 0x03, 0x68, 0x02, 0x00, 0x6C };
        var response = new byte[] { 0xE5 };

        transport.EnqueueRead(response);

        Assert.False(transport.IsOpen);
        await transport.OpenAsync(CancellationToken.None);
        Assert.True(transport.IsOpen);

        await transport.WriteAsync(request, CancellationToken.None);
        Assert.Single(transport.Writes);
        Assert.Equal(request, transport.Writes[0]);

        var buf = new byte[8];
        int n = await transport.ReadAsync(buf, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(1, n);
        Assert.Equal(0xE5, buf[0]);
    }

    [Fact]
    public async Task Scripted_Read_WithoutData_ThrowsTimeout()
    {
        await using var transport = new ScriptedS7Transport();
        await transport.OpenAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            transport.ReadAsync(new byte[4], TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task Scripted_Close_ClearsIsOpen()
    {
        await using var transport = new ScriptedS7Transport();
        await transport.OpenAsync(CancellationToken.None);
        Assert.True(transport.IsOpen);
        await transport.CloseAsync(CancellationToken.None);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task Scripted_MultipleReads_DequeueInOrder()
    {
        await using var transport = new ScriptedS7Transport();
        await transport.OpenAsync(CancellationToken.None);

        transport.EnqueueRead(new byte[] { 0xE5 });
        transport.EnqueueRead(new byte[] { 0x68, 0x03, 0x03, 0x68, 0x00, 0x02, 0x08, 0x00, 0x16 });

        var a = new byte[1];
        Assert.Equal(1, await transport.ReadAsync(a, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(0xE5, a[0]);

        var b = new byte[16];
        int n = await transport.ReadAsync(b, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(9, n);
        Assert.Equal(0x68, b[0]);
        Assert.Equal(0x16, b[8]);
    }

    [Fact]
    public async Task Scripted_Write_WhenClosed_Throws()
    {
        await using var transport = new ScriptedS7Transport();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.WriteAsync(new byte[] { 1 }, CancellationToken.None));
    }
}

/// <summary>
/// In-memory <see cref="IS7Transport"/> for client unit tests.
/// </summary>
internal sealed class ScriptedS7Transport : IS7Transport
{
    private readonly Queue<byte[]> _reads = new();
    private byte[]? _current;
    private int _offset;

    public List<byte[]> Writes { get; } = new();

    public bool IsOpen { get; private set; }

    public void EnqueueRead(byte[] chunk)
    {
        _reads.Enqueue(chunk);
    }

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

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOpen)
        {
            throw new InvalidOperationException("S7 serial transport is not open.");
        }

        Writes.Add(data.ToArray());
        return Task.CompletedTask;
    }

    public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOpen)
        {
            throw new InvalidOperationException("S7 serial transport is not open.");
        }

        if (buffer.Length == 0)
        {
            return Task.FromResult(0);
        }

        if (_current is null || _offset >= _current.Length)
        {
            if (_reads.Count == 0)
            {
                throw new TimeoutException("No scripted PPI response");
            }

            _current = _reads.Dequeue();
            _offset = 0;
        }

        int n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, n).CopyTo(buffer.Span);
        _offset += n;
        if (_offset >= _current.Length)
        {
            _current = null;
            _offset = 0;
        }

        return Task.FromResult(n);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
