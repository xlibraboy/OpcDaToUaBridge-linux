using OpcBridge.Core;
using OpcBridge.Drivers.S7;
using OpcBridge.Drivers.S7.Protocol;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class S7ClientTests
{
    private const byte Remote = 0x02;
    private const byte Local = 0x00;

    [Fact]
    public async Task ConnectAsync_SendsNegotiateExchange()
    {
        var transport = new ScriptedS7Transport();
        ScriptExchange(transport, BuildOkNegotiateResponse());

        await using var client = new S7200Client(
            new S7200ClientOptions { SourceId = "s7", RetryCount = 0 },
            transport);

        await client.ConnectAsync(CancellationToken.None);

        Assert.True(transport.IsOpen);
        // write request + write request-data
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(0x68, transport.Writes[0][0]);
        Assert.Equal(PpiFrameCodec.Dle, transport.Writes[1][0]);
    }

    [Fact]
    public async Task ReadAsync_VW0_ReturnsUInt16()
    {
        var transport = new ScriptedS7Transport();
        ScriptExchange(transport, BuildOkNegotiateResponse());
        ScriptExchange(transport, BuildOkReadResponse(new byte[] { 0x12, 0x34 }));

        await using var client = new S7200Client(
            new S7200ClientOptions { SourceId = "s7", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping
                {
                    SourceId = "s7",
                    ItemId = "VW0",
                    Enabled = true,
                    Mode = TagMode.Source
                }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.True(values[0].IsGood);
        Assert.Equal(0xC0, values[0].DaQuality);
        Assert.Equal("VW0", values[0].ItemId);
        Assert.Equal(0x1234, Convert.ToInt32(values[0].Value));
    }

    [Fact]
    public async Task ReadAsync_Bit_ReturnsBool()
    {
        var transport = new ScriptedS7Transport();
        ScriptExchange(transport, BuildOkNegotiateResponse());
        // Q0.1 → read QB0, bit1 set → 0x02
        ScriptExchange(transport, BuildOkReadResponse(new byte[] { 0x02 }));

        await using var client = new S7200Client(
            new S7200ClientOptions { SourceId = "s7", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "s7", ItemId = "Q0.1", Enabled = true, Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.True(values[0].IsGood);
        Assert.Equal(true, values[0].Value);
    }

    [Fact]
    public async Task ReadAsync_InvalidAddress_ReturnsBad()
    {
        var transport = new ScriptedS7Transport();
        ScriptExchange(transport, BuildOkNegotiateResponse());

        await using var client = new S7200Client(
            new S7200ClientOptions { SourceId = "s7", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[]
            {
                new TagMapping { SourceId = "s7", ItemId = "T0", Enabled = true, Mode = TagMode.Source }
            },
            CancellationToken.None);

        Assert.Single(values);
        Assert.False(values[0].IsGood);
        // no extra exchange beyond connect
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task WriteAsync_Word_Succeeds()
    {
        var transport = new ScriptedS7Transport();
        ScriptExchange(transport, BuildOkNegotiateResponse());
        ScriptExchange(transport, BuildOkWriteResponse());

        await using var client = new S7200Client(
            new S7200ClientOptions { SourceId = "s7", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        bool ok = await client.WriteAsync("VW100", 0x55AA, CancellationToken.None);
        Assert.True(ok);
        Assert.True(transport.Writes.Count >= 4);
    }

    [Fact]
    public async Task WriteAsync_InvalidAddress_ReturnsFalse()
    {
        var transport = new ScriptedS7Transport();
        ScriptExchange(transport, BuildOkNegotiateResponse());

        await using var client = new S7200Client(
            new S7200ClientOptions { SourceId = "s7", RetryCount = 0 },
            transport);
        await client.ConnectAsync(CancellationToken.None);

        bool ok = await client.WriteAsync("not-an-address", 1, CancellationToken.None);
        Assert.False(ok);
    }

    [Fact]
    public async Task TryGetTagMetadata_Bit_IsBool()
    {
        await using var client = new S7200Client(
            new S7200ClientOptions { SourceId = "s7" },
            new ScriptedS7Transport());

        Assert.True(client.TryGetTagMetadata("I0.0", out short? dt, out int? access));
        Assert.Equal((short)11, dt);
        Assert.Equal(1, access); // inputs read-oriented
    }
    [Fact]
    public async Task TryGetTagMetadata_VW_IsI2()
    {
        await using var client = new S7200Client(
            new S7200ClientOptions { SourceId = "s7" },
            new ScriptedS7Transport());

        Assert.True(client.TryGetTagMetadata("VW10", out short? dt, out int? access));
        Assert.Equal((short)2, dt);
        Assert.Equal(3, access);
    }

    /// <summary>Script one full PPI exchange: E5 then SD2 response frame.</summary>
    private static void ScriptExchange(ScriptedS7Transport transport, byte[] sd2Response)
    {
        transport.EnqueueRead(new byte[] { PpiFrameCodec.AckE5 });
        transport.EnqueueRead(sd2Response);
    }

    private static byte[] BuildOkNegotiateResponse()
    {
        // Minimal type-2 negotiate ack with empty useful data (header only + param F0…).
        // Body: local, remote, 0x08 + type2 PDU
        // PDU: 32 02 00 00 00 01 00 08 00 00 00 00 F0 00 00 01 00 01 00 F0
        byte[] body =
        [
            Local, Remote, 0x08,
            0x32, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00,
            0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xF0
        ];
        return WrapSd2(body);
    }

    private static byte[] BuildOkReadResponse(byte[] userData)
    {
        // type2 read response: param 04 01, data FF 04 lenBits + payload
        int bitLen = userData.Length * 8;
        var data = new byte[4 + userData.Length];
        data[0] = 0xFF;
        data[1] = 0x04;
        data[2] = (byte)((bitLen >> 8) & 0xFF);
        data[3] = (byte)(bitLen & 0xFF);
        userData.CopyTo(data, 4);

        byte[] param = [0x04, 0x01];
        var pdu = new byte[12 + param.Length + data.Length];
        pdu[0] = 0x32;
        pdu[1] = 0x02;
        pdu[2] = 0x00;
        pdu[3] = 0x00;
        pdu[4] = 0x00;
        pdu[5] = 0x01;
        pdu[6] = 0x00;
        pdu[7] = (byte)param.Length;
        pdu[8] = (byte)((data.Length >> 8) & 0xFF);
        pdu[9] = (byte)(data.Length & 0xFF);
        pdu[10] = 0x00;
        pdu[11] = 0x00;
        param.CopyTo(pdu.AsSpan(12));
        data.CopyTo(pdu.AsSpan(12 + param.Length));

        var body = new byte[3 + pdu.Length];
        body[0] = Local;
        body[1] = Remote;
        body[2] = 0x08;
        pdu.CopyTo(body.AsSpan(3));
        return WrapSd2(body);
    }

    private static byte[] BuildOkWriteResponse()
    {
        byte[] param = [0x05, 0x01];
        byte[] data = [0xFF];
        var pdu = new byte[12 + param.Length + data.Length];
        pdu[0] = 0x32;
        pdu[1] = 0x02;
        pdu[2] = 0x00;
        pdu[3] = 0x00;
        pdu[4] = 0x00;
        pdu[5] = 0x01;
        pdu[6] = 0x00;
        pdu[7] = (byte)param.Length;
        pdu[8] = 0x00;
        pdu[9] = (byte)data.Length;
        pdu[10] = 0x00;
        pdu[11] = 0x00;
        param.CopyTo(pdu.AsSpan(12));
        data.CopyTo(pdu.AsSpan(12 + param.Length));

        var body = new byte[3 + pdu.Length];
        body[0] = Local;
        body[1] = Remote;
        body[2] = 0x08;
        pdu.CopyTo(body.AsSpan(3));
        return WrapSd2(body);
    }

    private static byte[] WrapSd2(byte[] body)
    {
        byte bcc = PpiFrameCodec.ComputeBcc(body);
        var frame = new byte[4 + body.Length + 2];
        frame[0] = 0x68;
        frame[1] = (byte)body.Length;
        frame[2] = (byte)body.Length;
        frame[3] = 0x68;
        body.CopyTo(frame.AsSpan(4));
        frame[^2] = bcc;
        frame[^1] = 0x16;
        return frame;
    }
}
