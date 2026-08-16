using System.Globalization;
using System.Text;
using OpcBridge.Core;
using OpcBridge.Drivers.Melsec;
using OpcBridge.Drivers.Melsec.Protocol;
using OpcBridge.Drivers.Melsec.Transport;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class MelsecA3nClientTests
{
    [Fact]
    public async Task ReadAsync_Word_ReturnsBridgeValue()
    {
        var transport = new ScriptedMelsecTransport();
        // Connect probe (WR D0) then D100 read.
        transport.Responses.Enqueue(BuildStxDataResponse("0000"));
        transport.Responses.Enqueue(BuildStxDataResponse("0012"));

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);

        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping
                {
                    SourceId = "a3n",
                    ItemId = "D100",
                    Enabled = true,
                    Mode = TagMode.Source
                }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.True(values[0].IsGood);
        Assert.Equal(0xC0, values[0].DaQuality);
        Assert.Equal("D100", values[0].ItemId);
        Assert.Equal("a3n", values[0].SourceId);
        Assert.Equal((short)0x0012, values[0].Value);
        Assert.Equal(2, transport.Requests.Count);

        // Probe then read both WR.
        Assert.Contains("WR", Encoding.ASCII.GetString(transport.Requests[0]), StringComparison.Ordinal);
        Assert.Contains("D0000", Encoding.ASCII.GetString(transport.Requests[0]), StringComparison.Ordinal);
        Assert.Contains("D0100", Encoding.ASCII.GetString(transport.Requests[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_Bit_ReturnsBool()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000")); // probe
        transport.Responses.Enqueue(BuildStxDataResponse("1")); // M10 = ON

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "a3n", ItemId = "M10", Enabled = true, Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.True(values[0].IsGood);
        Assert.Equal(true, values[0].Value);
        Assert.Contains("BR", Encoding.ASCII.GetString(transport.Requests[1]), StringComparison.Ordinal);
        Assert.Contains("M0010", Encoding.ASCII.GetString(transport.Requests[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_InvalidAddress_ReturnsBadQuality()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000")); // probe only

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "a3n", ItemId = "Z999", Enabled = true, Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.False(values[0].IsGood);
        Assert.Equal("Z999", values[0].ItemId);
        Assert.Single(transport.Requests); // probe only
    }

    [Fact]
    public async Task WriteAsync_Bit_SendsBwAndReturnsTrue()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000")); // probe
        transport.Responses.Enqueue(new byte[] { Melsec1CFrameCodec.Ack, Melsec1CFrameCodec.Cr });

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        bool ok = await client.WriteAsync("M10", true, CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(2, transport.Requests.Count);

        string writeAscii = Encoding.ASCII.GetString(transport.Requests[1]);
        Assert.Contains("BW", writeAscii, StringComparison.Ordinal);
        Assert.Contains("M0010", writeAscii, StringComparison.Ordinal);
        Assert.Contains("00011", writeAscii, StringComparison.Ordinal); // count 0001 + bit '1'
    }

    [Fact]
    public async Task WriteAsync_InvalidAddress_ReturnsFalse()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000"));

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        bool ok = await client.WriteAsync("not-an-address", 1, CancellationToken.None);
        Assert.False(ok);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task WriteAsync_Word_SendsWw()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000"));
        transport.Responses.Enqueue(new byte[] { Melsec1CFrameCodec.Ack });

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        bool ok = await client.WriteAsync("D100", 0x1234, CancellationToken.None);
        Assert.True(ok);

        string writeAscii = Encoding.ASCII.GetString(transport.Requests[1]);
        Assert.Contains("WW", writeAscii, StringComparison.Ordinal);
        Assert.Contains("D0100", writeAscii, StringComparison.Ordinal);
        Assert.Contains("1234", writeAscii, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_BitInWord_RmW()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000")); // probe
        transport.Responses.Enqueue(BuildStxDataResponse("0001")); // current word bit0 set
        transport.Responses.Enqueue(new byte[] { Melsec1CFrameCodec.Ack }); // WW

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        // Set bit 1 on D10 (current 0x0001 → 0x0003)
        bool ok = await client.WriteAsync("D10:1", true, CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(3, transport.Requests.Count);

        string readAscii = Encoding.ASCII.GetString(transport.Requests[1]);
        string writeAscii = Encoding.ASCII.GetString(transport.Requests[2]);
        Assert.Contains("WR", readAscii, StringComparison.Ordinal);
        Assert.Contains("D0010", readAscii, StringComparison.Ordinal);
        Assert.Contains("WW", writeAscii, StringComparison.Ordinal);
        Assert.Contains("0003", writeAscii, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_BitInWord_ExtractsBit()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000")); // probe
        transport.Responses.Enqueue(BuildStxDataResponse("0004")); // bit 2 set

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "a3n", ItemId = "D5:2", Enabled = true, Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.True(values[0].IsGood);
        Assert.Equal(true, values[0].Value);
    }

    [Fact]
    public async Task ReadAsync_ConsecutiveWords_BatchesWr()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000")); // probe
        transport.Responses.Enqueue(BuildStxDataResponse("00010002")); // D10, D11

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "a3n", ItemId = "D10", Mode = TagMode.Source },
                new TagMapping { SourceId = "a3n", ItemId = "D11", Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Equal(2, values.Count);
        Assert.All(values, v => Assert.True(v.IsGood));
        Assert.Equal((short)1, values[0].Value);
        Assert.Equal((short)2, values[1].Value);

        string readAscii = Encoding.ASCII.GetString(transport.Requests[1]);
        Assert.Contains("WR", readAscii, StringComparison.Ordinal);
        Assert.Contains("D0010", readAscii, StringComparison.Ordinal);
        Assert.Contains("0002", readAscii, StringComparison.Ordinal); // word count
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task ConnectAsync_ProbeFailure_ClosesAndThrows()
    {
        var transport = new ScriptedMelsecTransport();
        // No responses → TimeoutException after retries (RetryCount=0 → 1 attempt)

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 0 },
            transport);

        await Assert.ThrowsAsync<TimeoutException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task TryGetTagMetadata_BitAndWord()
    {
        await using var transport = new ScriptedMelsecTransport();
        var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n" },
            transport);

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

    [Fact]
    public async Task ReadAsync_ProtocolError_RetriesThenBad()
    {
        var transport = new ScriptedMelsecTransport();
        transport.Responses.Enqueue(BuildStxDataResponse("0000")); // probe
        // RetryCount=1 → 2 attempts for the read; both NAK
        transport.Responses.Enqueue(new byte[] { Melsec1CFrameCodec.Nak, (byte)'0', (byte)'1', Melsec1CFrameCodec.Cr });
        transport.Responses.Enqueue(new byte[] { Melsec1CFrameCodec.Nak, (byte)'0', (byte)'1', Melsec1CFrameCodec.Cr });

        await using var client = new MelsecA3nClient(
            new MelsecA3nClientOptions { SourceId = "a3n", RetryCount = 1 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "a3n", ItemId = "D100", Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.False(values[0].IsGood);
        Assert.Equal(3, transport.Requests.Count); // probe + 2 read attempts
    }

    /// <summary>
    /// STX + data + ETX + sum(data+ETX) + CR — matches <see cref="Melsec1CFrameCodec.ParseDataResponse"/>.
    /// </summary>
    private static byte[] BuildStxDataResponse(string dataChars)
    {
        string sumPayload = dataChars + ((char)Melsec1CFrameCodec.Etx);
        string sum = Melsec1CFrameCodec.ComputeSumCheck(sumPayload);
        var bytes = new List<byte>(dataChars.Length + 5)
        {
            Melsec1CFrameCodec.Stx
        };
        bytes.AddRange(Encoding.ASCII.GetBytes(dataChars));
        bytes.Add(Melsec1CFrameCodec.Etx);
        bytes.AddRange(Encoding.ASCII.GetBytes(sum));
        bytes.Add(Melsec1CFrameCodec.Cr);
        return bytes.ToArray();
    }
}
