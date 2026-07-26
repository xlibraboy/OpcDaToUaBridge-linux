using System.Globalization;
using System.Text;
using OpcBridge.Drivers.Melsec.Addressing;
using OpcBridge.Drivers.Melsec.Protocol;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class Melsec1CFrameCodecTests
{
    private static string AlgorithmicSum(string payload)
    {
        int sum = 0;
        foreach (char c in payload)
        {
            sum += c;
        }

        return (sum & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ComputeSumCheck_KnownVector()
    {
        // Sum of ASCII of "00FFBRD01000001" → low byte as 2 uppercase hex digits.
        string payload = "00FFBRD01000001";
        Assert.Equal(AlgorithmicSum(payload), Melsec1CFrameCodec.ComputeSumCheck(payload));
    }

    [Fact]
    public void ComputeSumCheck_Empty_Is00()
    {
        Assert.Equal("00", Melsec1CFrameCodec.ComputeSumCheck(string.Empty));
    }

    [Fact]
    public void BuildRequest_WordRead_D100_OneWord()
    {
        string body = Melsec1CCommands.BuildWordReadBody("D0100", 1);
        Assert.Equal("D01000001", body);

        byte[] frame = Melsec1CFrameCodec.BuildRequest("00", "FF", "WR", body);
        Assert.Equal(Melsec1CFrameCodec.Enq, frame[0]);
        Assert.Equal(Melsec1CFrameCodec.Cr, frame[^1]);

        string ascii = Encoding.ASCII.GetString(frame);
        string payload = "00FFWR" + body;
        string expected = "\u0005" + payload + AlgorithmicSum(payload) + "\r";
        Assert.Equal(expected, ascii);
        Assert.StartsWith("\u000500FFWR", ascii, StringComparison.Ordinal);
        Assert.Contains("D0100", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRequest_BitRead_M10_EightBits()
    {
        string head = Melsec1CDeviceCodes.FormatHead(new MelsecAddress(MelsecDeviceKind.M, 10, null, "M10"));
        Assert.Equal("M0010", head);

        string body = Melsec1CCommands.BuildBitReadBody(head, 8);
        Assert.Equal("M00100008", body);

        byte[] frame = Melsec1CFrameCodec.BuildRequest("00", "FF", "BR", body);
        string payload = "00FFBR" + body;
        string expected = "\u0005" + payload + AlgorithmicSum(payload) + "\r";
        Assert.Equal(expected, Encoding.ASCII.GetString(frame));
    }

    [Fact]
    public void BuildRequest_WordWrite_And_BitWrite_Bodies()
    {
        string wwBody = Melsec1CCommands.BuildWordWriteBody("D0100", new ushort[] { 0x1234, 0x00FF });
        Assert.Equal("D01000002123400FF", wwBody);

        string bwBody = Melsec1CCommands.BuildBitWriteBody("M0010", "1011");
        Assert.Equal("M001000041011", bwBody);

        byte[] ww = Melsec1CFrameCodec.BuildRequest("00", "FF", "WW", wwBody);
        string wwPayload = "00FFWW" + wwBody;
        Assert.Equal(
            "\u0005" + wwPayload + AlgorithmicSum(wwPayload) + "\r",
            Encoding.ASCII.GetString(ww));

        byte[] bw = Melsec1CFrameCodec.BuildRequest("00", "FF", "BW", bwBody);
        string bwPayload = "00FFBW" + bwBody;
        Assert.Equal(
            "\u0005" + bwPayload + AlgorithmicSum(bwPayload) + "\r",
            Encoding.ASCII.GetString(bw));
    }

    [Theory]
    [InlineData(MelsecDeviceKind.D, 100, "D0100")]
    [InlineData(MelsecDeviceKind.M, 10, "M0010")]
    [InlineData(MelsecDeviceKind.X, 16, "X020")] // 16 decimal = 020 octal
    [InlineData(MelsecDeviceKind.Y, 15, "Y017")] // 15 decimal = 017 octal
    [InlineData(MelsecDeviceKind.X, 0, "X000")]
    public void FormatHead_AcpuStyle(MelsecDeviceKind kind, int number, string expected)
    {
        var address = new MelsecAddress(kind, number, null, "n/a");
        Assert.Equal(expected, Melsec1CDeviceCodes.FormatHead(address));
    }

    [Fact]
    public void ParseWordReadData_FourHexDigits()
    {
        ushort[] words = Melsec1CCommands.ParseWordReadData("00FF", 1);
        Assert.Equal(new ushort[] { 0x00FF }, words);
    }

    [Fact]
    public void ParseWordReadData_MultipleWords()
    {
        ushort[] words = Melsec1CCommands.ParseWordReadData("1234ABCD00FF", 3);
        Assert.Equal(new ushort[] { 0x1234, 0xABCD, 0x00FF }, words);
    }

    [Fact]
    public void ParseBitReadData_ZeroOneChars()
    {
        bool[] bits = Melsec1CCommands.ParseBitReadData("10110", 5);
        Assert.Equal(new[] { true, false, true, true, false }, bits);
    }

    [Fact]
    public void ParseDataResponse_RejectsNak()
    {
        byte[] nak = [Melsec1CFrameCodec.Nak, (byte)'0', (byte)'1', Melsec1CFrameCodec.Cr];
        var ex = Assert.Throws<MelsecProtocolException>(() => Melsec1CFrameCodec.ParseDataResponse(nak));
        Assert.Contains("NAK", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDataResponse_StxDataEtxSumCr()
    {
        // Data "00FF" between STX/ETX; sum over data+ETX.
        string data = "00FF";
        string sumPayload = data + ((char)Melsec1CFrameCodec.Etx);
        string sum = AlgorithmicSum(sumPayload);
        byte[] response =
        [
            Melsec1CFrameCodec.Stx,
            ..Encoding.ASCII.GetBytes(data),
            Melsec1CFrameCodec.Etx,
            ..Encoding.ASCII.GetBytes(sum),
            Melsec1CFrameCodec.Cr
        ];

        Assert.Equal(data, Melsec1CFrameCodec.ParseDataResponse(response));
    }

    [Fact]
    public void ParseDataResponse_OptionalLeadingAck()
    {
        string data = "ABCD";
        string sum = AlgorithmicSum(data + ((char)Melsec1CFrameCodec.Etx));
        byte[] response =
        [
            Melsec1CFrameCodec.Ack,
            Melsec1CFrameCodec.Stx,
            ..Encoding.ASCII.GetBytes(data),
            Melsec1CFrameCodec.Etx,
            ..Encoding.ASCII.GetBytes(sum),
            Melsec1CFrameCodec.Cr
        ];

        Assert.Equal(data, Melsec1CFrameCodec.ParseDataResponse(response));
    }

    [Fact]
    public void ParseDataResponse_BadSumCheck_Throws()
    {
        byte[] response =
        [
            Melsec1CFrameCodec.Stx,
            (byte)'0', (byte)'0',
            Melsec1CFrameCodec.Etx,
            (byte)'0', (byte)'0', // deliberately wrong
            Melsec1CFrameCodec.Cr
        ];

        var ex = Assert.Throws<MelsecProtocolException>(() => Melsec1CFrameCodec.ParseDataResponse(response));
        Assert.Contains("sum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureAckOrThrow_AcceptsPureAck()
    {
        byte[] ack = [Melsec1CFrameCodec.Ack];
        Melsec1CFrameCodec.EnsureAckOrThrow(ack);

        byte[] ackCr = [Melsec1CFrameCodec.Ack, Melsec1CFrameCodec.Cr];
        Melsec1CFrameCodec.EnsureAckOrThrow(ackCr);
    }

    [Fact]
    public void EnsureAckOrThrow_RejectsNak()
    {
        byte[] nak = [Melsec1CFrameCodec.Nak, (byte)'1', (byte)'2', Melsec1CFrameCodec.Cr];
        var ex = Assert.Throws<MelsecProtocolException>(() => Melsec1CFrameCodec.EnsureAckOrThrow(nak));
        Assert.Contains("NAK", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureAckOrThrow_RejectsMissingAck()
    {
        byte[] junk = [(byte)'X'];
        Assert.Throws<MelsecProtocolException>(() => Melsec1CFrameCodec.EnsureAckOrThrow(junk));
    }
}
