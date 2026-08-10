using OpcBridge.Core;
using OpcBridge.Drivers.MxComponent;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MxComponentClientTests
{
    [Fact]
    public async Task ReadAsync_Word_ReturnsBridgeValue()
    {
        var session = new ScriptedMxSession();
        session.ReadResponses.Enqueue(new ushort[] { 0x0012 });

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "mx", ItemId = "D100", Enabled = true, Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.True(values[0].IsGood);
        Assert.Equal(0xC0, values[0].DaQuality);
        Assert.Equal("D100", values[0].ItemId);
        Assert.Equal("mx", values[0].SourceId);
        Assert.Equal((short)0x0012, values[0].Value);

        Assert.Single(session.Reads);
        Assert.Equal(("D100", 1), session.Reads[0]);
    }

    [Fact]
    public async Task ReadAsync_Bit_ReturnsBool()
    {
        var session = new ScriptedMxSession();
        session.ReadResponses.Enqueue(new ushort[] { 0x0400 }); // M10 = ON (bit 10 of the M0 word)

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "mx", ItemId = "M10", Enabled = true, Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.True(values[0].IsGood);
        Assert.Equal(true, values[0].Value);

        // MX Component packs 16 bits per word and requires a multiple-of-16 start:
        // reading M10 must align down to M0 and read one word.
        Assert.Equal(("M0", 1), session.Reads[0]);
    }

    [Fact]
    public async Task ReadAsync_InvalidAddress_ReturnsBadQuality_NoIo()
    {
        var session = new ScriptedMxSession();

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "mx", ItemId = "Z999", Enabled = true, Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.False(values[0].IsGood);
        Assert.Equal(0x00, values[0].DaQuality);
        Assert.Empty(session.Reads);
    }

    [Fact]
    public async Task ReadAsync_ConsecutiveWords_BatchesSingleCall()
    {
        var session = new ScriptedMxSession();
        session.ReadResponses.Enqueue(new ushort[] { 1, 2 }); // D10, D11

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "mx", ItemId = "D10", Mode = TagMode.Source },
                new TagMapping { SourceId = "mx", ItemId = "D11", Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Equal(2, values.Count);
        Assert.All(values, v => Assert.True(v.IsGood));
        Assert.Equal((short)1, values[0].Value);
        Assert.Equal((short)2, values[1].Value);

        Assert.Single(session.Reads);
        Assert.Equal(("D10", 2), session.Reads[0]);
    }

    [Fact]
    public async Task ReadAsync_BitInWord_ReadsWordDevice()
    {
        var session = new ScriptedMxSession();
        session.ReadResponses.Enqueue(new ushort[] { 0x0004 }); // bit 2 set in D5

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "mx", ItemId = "D5:2", Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.True(values[0].IsGood);
        Assert.Equal(true, values[0].Value);
        // Must read the plain word device, not the bit-in-word address.
        Assert.Equal(("D5", 1), session.Reads[0]);
    }

    [Fact]
    public async Task ReadAsync_Failure_RetriesThenBad()
    {
        var session = new ScriptedMxSession { FailReads = true };

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 1 }, // 2 attempts
            session);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "mx", ItemId = "D100", Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.False(values[0].IsGood);
        Assert.Equal(2, session.Reads.Count);
    }

    [Fact]
    public async Task WriteAsync_Word_SendsWrite()
    {
        var session = new ScriptedMxSession();

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        bool ok = await client.WriteAsync("D100", 0x1234, CancellationToken.None);
        Assert.True(ok);
        Assert.Single(session.Writes);
        Assert.Equal("D100", session.Writes[0].Device);
        Assert.Equal(new ushort[] { 0x1234 }, session.Writes[0].Words);
    }

    [Fact]
    public async Task WriteAsync_Bit_SendsZeroOrOne()
    {
        var session = new ScriptedMxSession();

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        Assert.True(await client.WriteAsync("M10", true, CancellationToken.None));
        Assert.True(await client.WriteAsync("M11", false, CancellationToken.None));

        // Single-bit writes go through WriteDeviceRandom2 (per-bit), not a full word write.
        Assert.Equal(2, session.BitWrites.Count);
        Assert.Equal(("M10", true), session.BitWrites[0]);
        Assert.Equal(("M11", false), session.BitWrites[1]);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task WriteAsync_BitInWord_RmW()
    {
        var session = new ScriptedMxSession();
        session.ReadResponses.Enqueue(new ushort[] { 0x0001 }); // current D10

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        // Set bit 1 on D10 (current 0x0001 → 0x0003).
        bool ok = await client.WriteAsync("D10:1", true, CancellationToken.None);
        Assert.True(ok);

        Assert.Equal(("D10", 1), session.Reads[0]);
        Assert.Single(session.Writes);
        Assert.Equal("D10", session.Writes[0].Device);
        Assert.Equal(new ushort[] { 0x0003 }, session.Writes[0].Words);
    }

    [Fact]
    public async Task WriteAsync_InvalidAddress_ReturnsFalse()
    {
        var session = new ScriptedMxSession();

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);
        await client.ConnectAsync(CancellationToken.None);

        bool ok = await client.WriteAsync("not-an-address", 1, CancellationToken.None);
        Assert.False(ok);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task ConnectAsync_SessionProbeFailure_Throws()
    {
        var session = new ScriptedMxSession { FailConnect = true };

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(CancellationToken.None));
        // The session was never opened (probe failed before Open succeeded).
        Assert.False(session.IsOpen);
        Assert.Equal(0, session.OpenCalls);
    }

    [Fact]
    public async Task ConnectAsync_Idempotent_OpensOnce()
    {
        var session = new ScriptedMxSession();

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);

        await client.ConnectAsync(CancellationToken.None);
        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(1, session.OpenCalls);
        Assert.True(session.IsOpen);
    }

    [Fact]
    public void TryGetTagMetadata_BitAndWord()
    {
        var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx" },
            new ScriptedMxSession());

        Assert.True(client.TryGetTagMetadata("M10", out short? bitType, out int? bitAccess));
        Assert.Equal((short)11, bitType); // VT_BOOL
        Assert.Equal(3, bitAccess);

        Assert.True(client.TryGetTagMetadata("D100", out short? wordType, out int? wordAccess));
        Assert.Equal((short)2, wordType); // VT_I2
        Assert.Equal(3, wordAccess);

        Assert.True(client.TryGetTagMetadata("D10:3", out short? biwType, out _));
        Assert.Equal((short)11, biwType);

        Assert.False(client.TryGetTagMetadata("ZZZ", out _, out _));
    }

    private sealed class ScriptedMxSession : IMxComponentSession
    {
        public bool IsOpen { get; private set; }
        public int OpenCalls { get; private set; }
        public List<(string Device, int Count)> Reads { get; } = new();
        public List<(string Device, IReadOnlyList<ushort> Words)> Writes { get; } = new();
        public List<(string Device, bool Value)> BitWrites { get; } = new();
        public Queue<ushort[]> ReadResponses { get; } = new();
        public bool FailConnect { get; set; }
        public bool FailReads { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (FailConnect)
            {
                throw new InvalidOperationException("MX Component connect failed (probe).");
            }

            OpenCalls++;
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadWordsAsync(string device, int count, CancellationToken cancellationToken)
        {
            Reads.Add((device, count));
            if (FailReads)
            {
                throw new InvalidOperationException("read failed");
            }

            ushort[] response = ReadResponses.Count > 0
                ? ReadResponses.Dequeue()
                : new ushort[count];
            if (response.Length != count)
            {
                response = response.Take(count).ToArray();
            }

            return Task.FromResult(response);
        }

        public Task WriteWordsAsync(string device, IReadOnlyList<ushort> words, CancellationToken cancellationToken)
        {
            Writes.Add((device, words.ToArray()));
            return Task.CompletedTask;
        }

        public Task WriteBitAsync(string device, bool value, CancellationToken cancellationToken)
        {
            BitWrites.Add((device, value));
            return Task.CompletedTask;
        }

        public Task<(string CpuName, string CpuCode)> GetCpuTypeAsync(CancellationToken cancellationToken)
            => Task.FromResult(("A3NCPU", "0030"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
