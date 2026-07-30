using OpcBridge.Drivers.S7.Protocol;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Golden fixtures derived from libnodave nodave.c PPI path
/// (_daveExchangePPI, _daveSendLength, _daveSendIt, _daveSendRequestData,
/// davePrepareReadRequest / daveAddVarToReadRequest / daveAddVarToWriteRequest /
/// daveAddBitVarToWriteRequest, _daveNegPDUlengthRequest, _daveGetResponsePPI).
/// Defaults match testPPI: remote=2, local=0. PDU number fixed to 1 for determinism.
/// </summary>
public sealed class S7PpiCodecTests
{
    private const byte Remote = 2;
    private const byte Local = 0;
    private const ushort PduNumber = 1;

    // --- Area constants (nodave.h daveInputs/Outputs/Flags/DB) ---

    [Fact]
    public void PpiAreas_MatchLibnodave()
    {
        Assert.Equal(0x81, PpiAreas.Inputs);
        Assert.Equal(0x82, PpiAreas.Outputs);
        Assert.Equal(0x83, PpiAreas.Flags);
        Assert.Equal(0x84, PpiAreas.DB);
    }

    // --- BCC / framing helpers ---

    [Fact]
    public void ComputeBcc_MatchesSumLowByte()
    {
        // Body of READ VB0 request without header/trailer (see fixture below).
        byte[] body =
        [
            0x02, 0x00, 0x6C,
            0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0E, 0x00, 0x00,
            0x04, 0x01, 0x12, 0x0A, 0x10, 0x02, 0x00, 0x02, 0x00, 0x01, 0x84, 0x00, 0x00, 0x00
        ];
        Assert.Equal(0x6A, PpiFrameCodec.ComputeBcc(body));
    }

    [Fact]
    public void BuildLengthHeader_Sd2RepeatedLen()
    {
        Assert.Equal(new byte[] { 0x68, 0x1B, 0x1B, 0x68 }, PpiFrameCodec.BuildLengthHeader(0x1B));
    }

    [Fact]
    public void BuildRequestDataFrame_AfterE5()
    {
        // libnodave _daveSendRequestData(alt=0): DLE + remote + local + 0x5C + BCC + SYN
        // body sum 0x02+0x00+0x5C = 0x5E
        byte[] expected = [0x10, 0x02, 0x00, 0x5C, 0x5E, 0x16];
        Assert.Equal(expected, PpiFrameCodec.BuildRequestDataFrame(Remote, Local));
    }

    [Fact]
    public void BuildRequestDataFrame_AlternateCode()
    {
        // alt=1 uses 0x7C; BCC = 0x02+0x00+0x7C = 0x7E
        byte[] expected = [0x10, 0x02, 0x00, 0x7C, 0x7E, 0x16];
        Assert.Equal(expected, PpiFrameCodec.BuildRequestDataFrame(Remote, Local, alternate: true));
    }

    [Fact]
    public void IsAckE5()
    {
        Assert.True(PpiFrameCodec.IsAckE5([0xE5]));
        Assert.False(PpiFrameCodec.IsAckE5([0x68]));
        Assert.False(PpiFrameCodec.IsAckE5([]));
    }

    // --- Connect / negotiate ---

    [Fact]
    public void BuildNegotiateRequest_Fixture()
    {
        // S7 PDU: 32 01 00 00 00 01 00 08 00 00 F0 00 00 01 00 01 03 C0
        // Full: 68 15 15 68 | 02 00 6C | PDU | BCC 5F | 16
        byte[] expected =
        [
            0x68, 0x15, 0x15, 0x68,
            0x02, 0x00, 0x6C,
            0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00,
            0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x03, 0xC0,
            0x5F, 0x16
        ];
        Assert.Equal(expected, PpiFrameCodec.BuildNegotiateRequest(Remote, Local, PduNumber));
    }

    // --- Read ---

    [Fact]
    public void BuildReadBytesRequest_VB0_TwoBytes_Fixture()
    {
        // Read 2 bytes from V memory (DB1, area 0x84, start 0).
        // PDU: 32 01 00 00 00 01 00 0E 00 00 04 01 12 0A 10 02 00 02 00 01 84 00 00 00
        // Full wire:
        // 68 1B 1B 68 02 00 6C 32 01 00 00 00 01 00 0E 00 00
        // 04 01 12 0A 10 02 00 02 00 01 84 00 00 00 6A 16
        byte[] expected =
        [
            0x68, 0x1B, 0x1B, 0x68,
            0x02, 0x00, 0x6C,
            0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0E, 0x00, 0x00,
            0x04, 0x01,
            0x12, 0x0A, 0x10, 0x02, 0x00, 0x02, 0x00, 0x01, 0x84, 0x00, 0x00, 0x00,
            0x6A, 0x16
        ];
        Assert.Equal(
            expected,
            PpiFrameCodec.BuildReadBytesRequest(Remote, Local, PpiAreas.DB, dbNumber: 1, start: 0, byteCount: 2, PduNumber));
    }

    [Fact]
    public void BuildReadBytesRequest_Flags_MB0_TwoBytes_Fixture()
    {
        // Flags area 0x83, DB 0, start 0, 2 bytes.
        byte[] expected =
        [
            0x68, 0x1B, 0x1B, 0x68,
            0x02, 0x00, 0x6C,
            0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0E, 0x00, 0x00,
            0x04, 0x01,
            0x12, 0x0A, 0x10, 0x02, 0x00, 0x02, 0x00, 0x00, 0x83, 0x00, 0x00, 0x00,
            0x68, 0x16
        ];
        Assert.Equal(
            expected,
            PpiFrameCodec.BuildReadBytesRequest(Remote, Local, PpiAreas.Flags, dbNumber: 0, start: 0, byteCount: 2, PduNumber));
    }

    [Fact]
    public void ParseReadResponse_TwoBytes_Fixture()
    {
        // Synthetic successful response (PDUstartI=7): data 0x12 0x34
        // 68 17 17 68 00 02 08 | type2 PDU | BCC A5 | 16
        byte[] frame =
        [
            0x68, 0x17, 0x17, 0x68,
            0x00, 0x02, 0x08,
            0x32, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x06, 0x00, 0x00,
            0x04, 0x01,
            0xFF, 0x04, 0x00, 0x10, 0x12, 0x34,
            0xA5, 0x16
        ];
        byte[] data = PpiFrameCodec.ParseReadResponse(frame);
        Assert.Equal(new byte[] { 0x12, 0x34 }, data);
    }

    [Fact]
    public void ParseReadResponse_ItemError_Throws()
    {
        // data[0]=0x0A (not FF)
        byte[] frame =
        [
            0x68, 0x12, 0x12, 0x68,
            0x00, 0x02, 0x08,
            0x32, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00,
            0x04, 0x01,
            0x0A,
            0x51, 0x16
        ];
        var ex = Assert.Throws<PpiException>(() => PpiFrameCodec.ParseReadResponse(frame));
        Assert.Equal(0x0A, ex.ErrorCode);
    }

    // --- Write ---

    [Fact]
    public void BuildWriteBitsRequest_Q0_0_Fixture()
    {
        // Write 1 bit to Q0.0 (outputs 0x82, startBit 0, data 0x01).
        // PDU ends with data header 00 03 00 01 + 01
        byte[] expected =
        [
            0x68, 0x20, 0x20, 0x68,
            0x02, 0x00, 0x6C,
            0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0E, 0x00, 0x05,
            0x05, 0x01,
            0x12, 0x0A, 0x10, 0x01, 0x00, 0x01, 0x00, 0x00, 0x82, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x00, 0x01, 0x01,
            0x70, 0x16
        ];
        Assert.Equal(
            expected,
            PpiFrameCodec.BuildWriteBitsRequest(
                Remote, Local, PpiAreas.Outputs, dbNumber: 0, startBit: 0, bitCount: 1, data: [0x01], PduNumber));
    }

    [Fact]
    public void BuildWriteBitsRequest_M0_0_Fixture()
    {
        byte[] expected =
        [
            0x68, 0x20, 0x20, 0x68,
            0x02, 0x00, 0x6C,
            0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0E, 0x00, 0x05,
            0x05, 0x01,
            0x12, 0x0A, 0x10, 0x01, 0x00, 0x01, 0x00, 0x00, 0x83, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x00, 0x01, 0x01,
            0x71, 0x16
        ];
        Assert.Equal(
            expected,
            PpiFrameCodec.BuildWriteBitsRequest(
                Remote, Local, PpiAreas.Flags, dbNumber: 0, startBit: 0, bitCount: 1, data: [0x01], PduNumber));
    }

    [Fact]
    public void BuildWriteBytesRequest_VB0_TwoBytes_Fixture()
    {
        // Write AA 55 to VB0 (DB1).
        byte[] expected =
        [
            0x68, 0x21, 0x21, 0x68,
            0x02, 0x00, 0x6C,
            0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0E, 0x00, 0x06,
            0x05, 0x01,
            0x12, 0x0A, 0x10, 0x02, 0x00, 0x02, 0x00, 0x01, 0x84, 0x00, 0x00, 0x00,
            0x00, 0x04, 0x00, 0x10, 0xAA, 0x55,
            0x84, 0x16
        ];
        Assert.Equal(
            expected,
            PpiFrameCodec.BuildWriteBytesRequest(
                Remote, Local, PpiAreas.DB, dbNumber: 1, start: 0, data: [0xAA, 0x55], PduNumber));
    }

    [Fact]
    public void EnsureWriteSuccess_OkFixture()
    {
        byte[] frame =
        [
            0x68, 0x12, 0x12, 0x68,
            0x00, 0x02, 0x08,
            0x32, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x01,
            0xFF,
            0x47, 0x16
        ];
        PpiFrameCodec.EnsureWriteSuccess(frame); // no throw
    }

    // --- NAK / bad BCC ---

    [Fact]
    public void UnwrapPpiResponse_Nak_Throws()
    {
        var ex = Assert.Throws<PpiException>(() => PpiFrameCodec.UnwrapPpiResponse([PpiFrameCodec.Nak]));
        Assert.Equal(PpiFrameCodec.Nak, ex.ErrorCode);
    }

    [Fact]
    public void UnwrapPpiResponse_BadBcc_Throws()
    {
        // Same as good read response but BCC flipped 0xA5 -> 0x5A
        byte[] frame =
        [
            0x68, 0x17, 0x17, 0x68,
            0x00, 0x02, 0x08,
            0x32, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x06, 0x00, 0x00,
            0x04, 0x01,
            0xFF, 0x04, 0x00, 0x10, 0x12, 0x34,
            0x5A, 0x16
        ];
        var ex = Assert.Throws<PpiException>(() => PpiFrameCodec.UnwrapPpiResponse(frame));
        Assert.Contains("BCC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnwrapPpiResponse_MissingSyn_Throws()
    {
        byte[] frame =
        [
            0x68, 0x17, 0x17, 0x68,
            0x00, 0x02, 0x08,
            0x32, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x06, 0x00, 0x00,
            0x04, 0x01,
            0xFF, 0x04, 0x00, 0x10, 0x12, 0x34,
            0xA5, 0x00
        ];
        Assert.Throws<PpiException>(() => PpiFrameCodec.UnwrapPpiResponse(frame));
    }
}
