using System.Buffers.Binary;
using System.Text.Json;

namespace OpcBridge.Client.Workers;

/// <summary>Thrown for malformed, oversized or truncated worker protocol frames.</summary>
public sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message)
        : base(message)
    {
    }

    public WorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Length-prefixed JSON framing: a 4-byte little-endian length followed by that many UTF-8
/// bytes of a serialized <see cref="WorkerFrame"/>. Reads tolerate partial reads and streams
/// that hand data over in small chunks.
/// </summary>
public static class FrameCodec
{
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    private const int LengthPrefixBytes = 4;

    public static void Write(Stream stream, WorkerFrame frame)
    {
        byte[] payload = Serialize(frame);
        Span<byte> prefix = stackalloc byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        stream.Write(prefix);
        stream.Write(payload);
        stream.Flush();
    }

    public static async Task WriteAsync(Stream stream, WorkerFrame frame, CancellationToken cancellationToken = default)
    {
        byte[] payload = Serialize(frame);
        byte[] prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; returns null on a clean end of stream at a frame boundary.</summary>
    public static WorkerFrame? Read(Stream stream)
    {
        byte[] prefix = new byte[LengthPrefixBytes];
        int read = ReadInto(stream, prefix);
        if (read == 0)
        {
            return null;
        }

        if (read < prefix.Length)
        {
            throw new WorkerProtocolException("Truncated frame length prefix.");
        }

        int length = ReadLength(prefix);
        byte[] payload = new byte[length];
        if (ReadInto(stream, payload) != length)
        {
            throw new WorkerProtocolException("Truncated frame payload.");
        }

        return Deserialize(payload);
    }

    /// <summary>Reads one frame; returns null on a clean end of stream at a frame boundary.</summary>
    public static async Task<WorkerFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[LengthPrefixBytes];
        int read = await ReadIntoAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < prefix.Length)
        {
            throw new WorkerProtocolException("Truncated frame length prefix.");
        }

        int length = ReadLength(prefix);
        byte[] payload = new byte[length];
        if (await ReadIntoAsync(stream, payload, cancellationToken).ConfigureAwait(false) != length)
        {
            throw new WorkerProtocolException("Truncated frame payload.");
        }

        return Deserialize(payload);
    }

    private static int ReadLength(byte[] prefix)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new WorkerProtocolException(
                $"Invalid frame length {length} (limit {MaxFrameBytes} bytes).");
        }

        return length;
    }

    private static int ReadInto(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<int> ReadIntoAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static byte[] Serialize(WorkerFrame frame)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame, WorkerProtocol.JsonOptions);
        if (payload.Length == 0 || payload.Length > MaxFrameBytes)
        {
            throw new WorkerProtocolException(
                $"Frame of {payload.Length} bytes exceeds the {MaxFrameBytes}-byte limit.");
        }

        return payload;
    }

    private static WorkerFrame Deserialize(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkerFrame>(payload, WorkerProtocol.JsonOptions)
                ?? throw new WorkerProtocolException("Frame deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new WorkerProtocolException("Malformed frame payload.", ex);
        }
    }
}
