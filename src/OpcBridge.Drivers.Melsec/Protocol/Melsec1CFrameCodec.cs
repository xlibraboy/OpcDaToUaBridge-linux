using System.Globalization;
using System.Text;

namespace OpcBridge.Drivers.Melsec.Protocol;

/// <summary>
/// Pure MELSEC A-compatible 1C Frame (Dedicated Protocol Format 1) codec — no I/O.
/// Request: ENQ + Station(2) + PC(2) + Command(2) + body + SumCheck(2) + CR.
/// </summary>
public static class Melsec1CFrameCodec
{
    public const byte Enq = 0x05;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Cr = 0x0D;

    /// <summary>
    /// Sum of ASCII values of <paramref name="payloadWithoutEnqAndSumAndCr"/>,
    /// lower 8 bits as 2 uppercase hex digits.
    /// </summary>
    public static string ComputeSumCheck(ReadOnlySpan<char> payloadWithoutEnqAndSumAndCr)
    {
        int sum = 0;
        for (int i = 0; i < payloadWithoutEnqAndSumAndCr.Length; i++)
        {
            sum += payloadWithoutEnqAndSumAndCr[i];
        }

        return (sum & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Build request frame:
    /// ENQ + station + pc + command + body + sum(station+pc+command+body) + CR.
    /// </summary>
    public static byte[] BuildRequest(string stationNo, string pcNo, string command, string body)
    {
        ArgumentNullException.ThrowIfNull(stationNo);
        ArgumentNullException.ThrowIfNull(pcNo);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(body);

        if (stationNo.Length != 2)
        {
            throw new ArgumentException("Station number must be exactly 2 characters.", nameof(stationNo));
        }

        if (pcNo.Length != 2)
        {
            throw new ArgumentException("PC number must be exactly 2 characters.", nameof(pcNo));
        }

        if (command.Length != 2)
        {
            throw new ArgumentException("Command must be exactly 2 characters (e.g. BR, BW, WR, WW).", nameof(command));
        }

        string payload = stationNo + pcNo + command + body;
        string sum = ComputeSumCheck(payload);

        // ENQ + payload + sum(2) + CR
        int len = 1 + payload.Length + 2 + 1;
        byte[] frame = new byte[len];
        frame[0] = Enq;
        Encoding.ASCII.GetBytes(payload, 0, payload.Length, frame, 1);
        Encoding.ASCII.GetBytes(sum, 0, 2, frame, 1 + payload.Length);
        frame[^1] = Cr;
        return frame;
    }

    /// <summary>
    /// Parse a data (read) response: optional leading ACK, then STX…ETX + sum + CR.
    /// Returns characters between STX and ETX. NAK or bad sum throws <see cref="MelsecProtocolException"/>.
    /// </summary>
    public static string ParseDataResponse(ReadOnlySpan<byte> response)
    {
        if (response.IsEmpty)
        {
            throw new MelsecProtocolException("Empty MELSEC 1C response.");
        }

        int offset = 0;
        if (response[0] == Nak)
        {
            throw CreateNakException(response);
        }

        if (response[0] == Ack)
        {
            offset = 1;
            if (offset >= response.Length)
            {
                throw new MelsecProtocolException("Response is ACK only; expected STX data block for a read.");
            }
        }

        if (response[offset] != Stx)
        {
            throw new MelsecProtocolException(
                $"Expected STX (0x02) at response offset {offset}, got 0x{response[offset]:X2}.");
        }

        int stxIndex = offset;
        int etxIndex = -1;
        for (int i = stxIndex + 1; i < response.Length; i++)
        {
            if (response[i] == Etx)
            {
                etxIndex = i;
                break;
            }
        }

        if (etxIndex < 0)
        {
            throw new MelsecProtocolException("MELSEC 1C data response missing ETX.");
        }

        // After ETX: exactly 2 sum digits, optionally followed by a single CR; nothing else.
        int afterEtx = etxIndex + 1;
        if (afterEtx + 2 > response.Length)
        {
            throw new MelsecProtocolException("MELSEC 1C data response missing sum-check digits after ETX.");
        }

        int remaining = response.Length - afterEtx;
        if (remaining == 2)
        {
            // sum only — OK
        }
        else if (remaining == 3 && response[afterEtx + 2] == Cr)
        {
            // sum + CR — OK
        }
        else
        {
            throw new MelsecProtocolException(
                "MELSEC 1C data response must end with sum-check (2 hex) or sum-check + CR; trailing bytes rejected.");
        }

        // Sum check covers characters after STX through ETX inclusive (data + ETX).
        int dataLen = etxIndex - stxIndex - 1;
        int sumLen = dataLen + 1;
        const int StackThreshold = 256;
        Span<char> sumSpan = sumLen <= StackThreshold
            ? stackalloc char[sumLen]
            : new char[sumLen];
        for (int i = 0; i < dataLen; i++)
        {
            sumSpan[i] = (char)response[stxIndex + 1 + i];
        }

        sumSpan[dataLen] = (char)Etx;
        string expectedSum = ComputeSumCheck(sumSpan);

        char sum0 = (char)response[afterEtx];
        char sum1 = (char)response[afterEtx + 1];
        string actualSum = string.Concat(sum0, sum1);
        if (!string.Equals(expectedSum, actualSum, StringComparison.OrdinalIgnoreCase))
        {
            throw new MelsecProtocolException(
                $"MELSEC 1C sum-check mismatch: expected {expectedSum}, got {actualSum}.");
        }

        return Encoding.ASCII.GetString(response.Slice(stxIndex + 1, dataLen));
    }

    /// <summary>
    /// Ensures a write-style success response: pure ACK (optional CR). NAK throws.
    /// </summary>
    public static void EnsureAckOrThrow(ReadOnlySpan<byte> response)
    {
        if (response.IsEmpty)
        {
            throw new MelsecProtocolException("Empty MELSEC 1C response; expected ACK.");
        }

        if (response[0] == Nak)
        {
            throw CreateNakException(response);
        }

        if (response[0] != Ack)
        {
            throw new MelsecProtocolException(
                $"Expected ACK (0x06) for write success, got 0x{response[0]:X2}.");
        }

        // ACK alone, or ACK + CR, or ACK + optional trailing noise we ignore if only CR.
        for (int i = 1; i < response.Length; i++)
        {
            byte b = response[i];
            if (b == Cr)
            {
                continue;
            }

            // Some units may send ACK then nothing meaningful; reject non-CR trailers.
            throw new MelsecProtocolException(
                $"Unexpected byte 0x{b:X2} after ACK in write response.");
        }
    }

    private static MelsecProtocolException CreateNakException(ReadOnlySpan<byte> response)
    {
        // NAK + optional 2-char error code + CR
        string detail = string.Empty;
        if (response.Length >= 3)
        {
            detail = Encoding.ASCII.GetString(response.Slice(1, Math.Min(2, response.Length - 1)));
            detail = detail.TrimEnd('\r', '\n');
        }

        if (string.IsNullOrEmpty(detail))
        {
            return new MelsecProtocolException("MELSEC 1C NAK response (no error code).");
        }

        return new MelsecProtocolException($"MELSEC 1C NAK response, error code '{detail}'.");
    }
}
