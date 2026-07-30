namespace OpcBridge.Drivers.S7.Protocol;

/// <summary>
/// Pure PPI + S7comm codec (libnodave daveProtoPPI reference) — no I/O.
/// Framing: SD2 length header 0x68 L L 0x68, body [remote,local,0x6C]+PDU, BCC, SYN.
/// Multi-step exchange (E5 ack → request-data) is sequenced by the client.
/// </summary>
public static class PpiFrameCodec
{
    public const byte Sd2 = 0x68;
    public const byte Syn = 0x16;
    public const byte Dle = 0x10;
    public const byte AckE5 = 0xE5;
    public const byte Nak = 0x15;
    public const byte PpiFunction = 0x6C;
    public const byte RequestDataCode = 0x5C;
    public const byte RequestDataCodeAlt = 0x7C;

    public const byte S7ProtocolId = 0x32;
    public const byte S7FuncRead = 0x04;
    public const byte S7FuncWrite = 0x05;
    public const byte S7FuncNegotiate = 0xF0;

    /// <summary>Outgoing S7 PDU offset inside PPI body (libnodave PDUstartO for PPI).</summary>
    public const int PduStartOut = 3;

    /// <summary>Incoming S7 PDU offset inside full SD2 frame (libnodave PDUstartI for PPI).</summary>
    public const int PduStartIn = 7;

    /// <summary>Sum of body bytes, low 8 bits (libnodave _daveSendIt / _daveGetResponsePPI).</summary>
    public static byte ComputeBcc(ReadOnlySpan<byte> body) =>
        (byte)(Sum(body) & 0xFF);

    /// <summary>
    /// Build SD2 length header: 0x68, len, len, 0x68 (libnodave _daveSendLength).
    /// </summary>
    public static byte[] BuildLengthHeader(int bodyLength)
    {
        if (bodyLength is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(bodyLength));

        return [(byte)Sd2, (byte)bodyLength, (byte)bodyLength, (byte)Sd2];
    }

    /// <summary>
    /// Body + BCC + SYN (libnodave _daveSendIt). Does not include the 4-byte length header.
    /// </summary>
    public static byte[] AppendBccAndSyn(ReadOnlySpan<byte> body)
    {
        var result = new byte[body.Length + 2];
        body.CopyTo(result);
        result[body.Length] = ComputeBcc(body);
        result[body.Length + 1] = Syn;
        return result;
    }

    /// <summary>
    /// Full on-wire request: length header + body + BCC + SYN.
    /// Body is [remote, local, 0x6C] + S7 PDU.
    /// </summary>
    public static byte[] BuildPpiRequestFrame(byte remote, byte local, ReadOnlySpan<byte> s7Pdu)
    {
        var body = new byte[PduStartOut + s7Pdu.Length];
        body[0] = remote;
        body[1] = local;
        body[2] = PpiFunction;
        s7Pdu.CopyTo(body.AsSpan(PduStartOut));

        var header = BuildLengthHeader(body.Length);
        var tail = AppendBccAndSyn(body);
        var frame = new byte[header.Length + tail.Length];
        header.CopyTo(frame, 0);
        tail.CopyTo(frame, header.Length);
        return frame;
    }

    /// <summary>
    /// Post-E5 "request data" frame: DLE + [remote, local, 0x5C|0x7C] + BCC + SYN
    /// (libnodave _daveSendRequestData).
    /// </summary>
    public static byte[] BuildRequestDataFrame(byte remote, byte local, bool alternate = false)
    {
        var body = new byte[]
        {
            remote,
            local,
            alternate ? RequestDataCodeAlt : RequestDataCode
        };
        var tail = AppendBccAndSyn(body);
        var frame = new byte[1 + tail.Length];
        frame[0] = Dle;
        tail.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>S7 type-1 negotiate-PDU-length request (libnodave _daveNegPDUlengthRequest).</summary>
    public static byte[] BuildNegotiatePdu(ushort pduNumber = 1)
    {
        // param: F0 00 00 01 00 01 03 C0
        Span<byte> param = stackalloc byte[] { S7FuncNegotiate, 0x00, 0x00, 0x01, 0x00, 0x01, 0x03, 0xC0 };
        return BuildType1Pdu(pduNumber, param, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Full PPI frame for PDU-length negotiate (connect step).</summary>
    public static byte[] BuildNegotiateRequest(byte remote, byte local, ushort pduNumber = 1) =>
        BuildPpiRequestFrame(remote, local, BuildNegotiatePdu(pduNumber));

    /// <summary>
    /// S7 read-bytes var-spec request (libnodave davePrepareReadRequest + daveAddVarToReadRequest).
    /// <paramref name="start"/> is byte offset; converted to bit address on the wire.
    /// </summary>
    public static byte[] BuildReadBytesPdu(
        byte area,
        int dbNumber,
        int start,
        int byteCount,
        ushort pduNumber = 1)
    {
        if (byteCount < 1)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        if (dbNumber is < 0 or > 0xFFFF)
            throw new ArgumentOutOfRangeException(nameof(dbNumber));
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start));

        Span<byte> param = stackalloc byte[2 + 12];
        param[0] = S7FuncRead;
        param[1] = 1; // item count
        WriteVarSpec(param[2..], transportSize: 0x02, count: byteCount, dbNumber, area, startBit: start * 8);
        return BuildType1Pdu(pduNumber, param, ReadOnlySpan<byte>.Empty);
    }

    public static byte[] BuildReadBytesRequest(
        byte remote,
        byte local,
        byte area,
        int dbNumber,
        int start,
        int byteCount,
        ushort pduNumber = 1) =>
        BuildPpiRequestFrame(remote, local, BuildReadBytesPdu(area, dbNumber, start, byteCount, pduNumber));

    /// <summary>
    /// S7 write-bytes request. Data transport size 0x04 with bit-length = byteCount*8
    /// (libnodave daveAddVarToWriteRequest).
    /// </summary>
    public static byte[] BuildWriteBytesPdu(
        byte area,
        int dbNumber,
        int start,
        ReadOnlySpan<byte> data,
        ushort pduNumber = 1)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Write data required.", nameof(data));
        if (dbNumber is < 0 or > 0xFFFF)
            throw new ArgumentOutOfRangeException(nameof(dbNumber));
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start));

        Span<byte> param = stackalloc byte[2 + 12];
        param[0] = S7FuncWrite;
        param[1] = 1;
        WriteVarSpec(param[2..], transportSize: 0x02, count: data.Length, dbNumber, area, startBit: start * 8);

        var bitLen = data.Length * 8;
        var dataBlock = new byte[4 + data.Length];
        dataBlock[0] = 0x00;
        dataBlock[1] = 0x04; // bit transport in data header
        dataBlock[2] = (byte)((bitLen >> 8) & 0xFF);
        dataBlock[3] = (byte)(bitLen & 0xFF);
        data.CopyTo(dataBlock.AsSpan(4));

        return BuildType1Pdu(pduNumber, param, dataBlock);
    }

    public static byte[] BuildWriteBytesRequest(
        byte remote,
        byte local,
        byte area,
        int dbNumber,
        int start,
        ReadOnlySpan<byte> data,
        ushort pduNumber = 1) =>
        BuildPpiRequestFrame(remote, local, BuildWriteBytesPdu(area, dbNumber, start, data, pduNumber));

    /// <summary>
    /// S7 write-bits request. <paramref name="startBit"/> is absolute bit address
    /// (byte*8 + bit). <paramref name="bitCount"/> typically 1; data carries packed bits.
    /// </summary>
    public static byte[] BuildWriteBitsPdu(
        byte area,
        int dbNumber,
        int startBit,
        int bitCount,
        ReadOnlySpan<byte> data,
        ushort pduNumber = 1)
    {
        if (bitCount < 1)
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (data.IsEmpty)
            throw new ArgumentException("Bit data required.", nameof(data));
        if (dbNumber is < 0 or > 0xFFFF)
            throw new ArgumentOutOfRangeException(nameof(dbNumber));
        if (startBit < 0)
            throw new ArgumentOutOfRangeException(nameof(startBit));

        Span<byte> param = stackalloc byte[2 + 12];
        param[0] = S7FuncWrite;
        param[1] = 1;
        WriteVarSpec(param[2..], transportSize: 0x01, count: bitCount, dbNumber, area, startBit);

        var dataBlock = new byte[4 + data.Length];
        dataBlock[0] = 0x00;
        dataBlock[1] = 0x03; // bit
        dataBlock[2] = (byte)((bitCount >> 8) & 0xFF);
        dataBlock[3] = (byte)(bitCount & 0xFF);
        data.CopyTo(dataBlock.AsSpan(4));

        return BuildType1Pdu(pduNumber, param, dataBlock);
    }

    public static byte[] BuildWriteBitsRequest(
        byte remote,
        byte local,
        byte area,
        int dbNumber,
        int startBit,
        int bitCount,
        ReadOnlySpan<byte> data,
        ushort pduNumber = 1) =>
        BuildPpiRequestFrame(
            remote,
            local,
            BuildWriteBitsPdu(area, dbNumber, startBit, bitCount, data, pduNumber));

    /// <summary>
    /// Validate a full SD2 response frame (length + body + BCC + SYN) and return the S7 PDU slice.
    /// </summary>
    public static ReadOnlySpan<byte> UnwrapPpiResponse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 1 && frame[0] == Nak)
            throw new PpiException("PPI NAK received.") { ErrorCode = Nak };

        if (frame.Length < 6)
            throw new PpiException($"PPI response too short ({frame.Length} bytes).");

        if (frame[0] != Sd2 || frame[3] != Sd2 || frame[1] != frame[2])
            throw new PpiException("PPI SD2 length header invalid.");

        int bodyLen = frame[1];
        int expectedTotal = bodyLen + 6; // 4 header + body + BCC + SYN
        if (frame.Length < expectedTotal)
            throw new PpiException($"PPI response truncated: need {expectedTotal}, got {frame.Length}.");

        if (frame[expectedTotal - 1] != Syn)
            throw new PpiException("PPI response missing SYN trailer.");

        var body = frame.Slice(4, bodyLen);
        byte expectedBcc = ComputeBcc(body);
        byte actualBcc = frame[expectedTotal - 2];
        if (actualBcc != expectedBcc)
            throw new PpiException($"PPI BCC mismatch: expected 0x{expectedBcc:X2}, got 0x{actualBcc:X2}.");

        if (bodyLen <= PduStartOut)
            throw new PpiException("PPI response body has no S7 PDU.");

        // PDU begins at absolute offset 7 = 4 (SD2) + 3 (addr header) when body has the 3-byte prefix.
        // Equivalent: body.Slice(3) when bodyLen > 3.
        return body.Slice(PduStartOut);
    }

    /// <summary>
    /// Parse a successful read response PDU; returns user data bytes.
    /// Throws <see cref="PpiException"/> on protocol/item errors.
    /// </summary>
    public static byte[] ParseReadResponsePdu(ReadOnlySpan<byte> pdu)
    {
        if (!TryGetResponseData(pdu, S7FuncRead, out ReadOnlySpan<byte> data, out int err))
            throw new PpiException($"S7 PDU error 0x{err:X4}.") { ErrorCode = err };

        if (data.Length < 1)
            throw new PpiException("Read response has empty data.");

        // Item return: FF = OK, else error code
        byte ret = data[0];
        if (ret != 0xFF)
            throw new PpiException($"S7 read item error 0x{ret:X2}.") { ErrorCode = ret };

        if (data.Length < 4)
            throw new PpiException("Read response data header truncated.");

        byte transport = data[1];
        int rawLen = (data[2] << 8) | data[3];
        int byteLen = transport switch
        {
            0x04 => rawLen >> 3, // length in bits
            0x09 => rawLen,      // length in bytes
            0x03 => rawLen,      // bit results as bytes
            _ => throw new PpiException($"Unsupported read data transport 0x{transport:X2}.")
        };

        if (data.Length < 4 + byteLen)
            throw new PpiException($"Read payload truncated: need {byteLen}, have {data.Length - 4}.");

        return data.Slice(4, byteLen).ToArray();
    }

    /// <summary>Parse read response from a full PPI SD2 frame.</summary>
    public static byte[] ParseReadResponse(ReadOnlySpan<byte> frame) =>
        ParseReadResponsePdu(UnwrapPpiResponse(frame));

    /// <summary>
    /// Ensure write response PDU indicates success (data[0]==0xFF).
    /// </summary>
    public static void EnsureWriteSuccessPdu(ReadOnlySpan<byte> pdu)
    {
        if (!TryGetResponseData(pdu, S7FuncWrite, out ReadOnlySpan<byte> data, out int err))
            throw new PpiException($"S7 PDU error 0x{err:X4}.") { ErrorCode = err };

        if (data.Length < 1 || data[0] != 0xFF)
        {
            int code = data.Length > 0 ? data[0] : -1;
            throw new PpiException($"S7 write failed (0x{code:X2}).") { ErrorCode = code };
        }
    }

    public static void EnsureWriteSuccess(ReadOnlySpan<byte> frame) =>
        EnsureWriteSuccessPdu(UnwrapPpiResponse(frame));

    /// <summary>True if the first byte is the intermediate E5 master-ack.</summary>
    public static bool IsAckE5(ReadOnlySpan<byte> data) =>
        data.Length >= 1 && data[0] == AckE5;

    private static byte[] BuildType1Pdu(ushort pduNumber, ReadOnlySpan<byte> param, ReadOnlySpan<byte> data)
    {
        var pdu = new byte[10 + param.Length + data.Length];
        pdu[0] = S7ProtocolId;
        pdu[1] = 0x01; // request
        pdu[2] = 0x00;
        pdu[3] = 0x00;
        pdu[4] = (byte)((pduNumber >> 8) & 0xFF);
        pdu[5] = (byte)(pduNumber & 0xFF);
        pdu[6] = (byte)((param.Length >> 8) & 0xFF);
        pdu[7] = (byte)(param.Length & 0xFF);
        pdu[8] = (byte)((data.Length >> 8) & 0xFF);
        pdu[9] = (byte)(data.Length & 0xFF);
        param.CopyTo(pdu.AsSpan(10));
        if (!data.IsEmpty)
            data.CopyTo(pdu.AsSpan(10 + param.Length));
        return pdu;
    }

    private static void WriteVarSpec(
        Span<byte> dest12,
        byte transportSize,
        int count,
        int dbNumber,
        byte area,
        int startBit)
    {
        dest12[0] = 0x12;
        dest12[1] = 0x0A;
        dest12[2] = 0x10;
        dest12[3] = transportSize;
        dest12[4] = (byte)((count >> 8) & 0xFF);
        dest12[5] = (byte)(count & 0xFF);
        dest12[6] = (byte)((dbNumber >> 8) & 0xFF);
        dest12[7] = (byte)(dbNumber & 0xFF);
        dest12[8] = area;
        dest12[9] = (byte)((startBit >> 16) & 0xFF);
        dest12[10] = (byte)((startBit >> 8) & 0xFF);
        dest12[11] = (byte)(startBit & 0xFF);
    }

    private static bool TryGetResponseData(
        ReadOnlySpan<byte> pdu,
        byte expectFunc,
        out ReadOnlySpan<byte> data,
        out int error)
    {
        data = default;
        error = 0;

        if (pdu.Length < 10 || pdu[0] != S7ProtocolId)
            throw new PpiException("Invalid S7 PDU header.");

        byte type = pdu[1];
        int hlen = type is 2 or 3 ? 12 : 10;
        if (pdu.Length < hlen)
            throw new PpiException("S7 PDU header truncated.");

        if (hlen == 12)
            error = (pdu[10] << 8) | pdu[11];

        if (error != 0)
            return false;

        int plen = (pdu[6] << 8) | pdu[7];
        int dlen = (pdu[8] << 8) | pdu[9];
        if (pdu.Length < hlen + plen + dlen)
            throw new PpiException("S7 PDU truncated.");

        var param = pdu.Slice(hlen, plen);
        if (param.Length < 1 || param[0] != expectFunc)
            throw new PpiException($"Unexpected S7 function 0x{(param.Length > 0 ? param[0] : 0):X2}, expected 0x{expectFunc:X2}.");

        data = pdu.Slice(hlen + plen, dlen);
        return true;
    }

    private static int Sum(ReadOnlySpan<byte> data)
    {
        int s = 0;
        foreach (var b in data)
            s += b;
        return s;
    }
}
