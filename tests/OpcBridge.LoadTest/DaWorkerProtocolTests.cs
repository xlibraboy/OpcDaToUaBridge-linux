using System.Text;
using System.Text.Json;
using OpcBridge.Client.Workers;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaWorkerProtocolTests
{
    private sealed class ChunkedReadStream : MemoryStream
    {
        private readonly int _chunk;

        public ChunkedReadStream(byte[] data, int chunk)
            : base(data)
        {
            _chunk = chunk;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, _chunk));

        public override int Read(Span<byte> buffer)
            => base.Read(buffer[..Math.Min(buffer.Length, _chunk)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunk)], cancellationToken);
    }

    [Fact]
    public void Frame_RoundTripsThroughTheCodec()
    {
        WorkerFrame frame = WorkerFrame.Create(
            WorkerFrameTypes.ReadResult,
            42,
            new WorkerReadResult(new[] { Value("tag1", 1.5d) }, null));
        using var stream = new MemoryStream();

        FrameCodec.Write(stream, frame);
        stream.Position = 0;
        WorkerFrame? read = FrameCodec.Read(stream);

        Assert.NotNull(read);
        Assert.Equal(WorkerFrameTypes.ReadResult, read!.Type);
        Assert.Equal(42, read.Seq);
        WorkerReadResult? payload = read.PayloadAs<WorkerReadResult>();
        Assert.NotNull(payload);
        Assert.Null(payload!.Error);
        WireValue single = Assert.Single(payload.Values);
        Assert.Equal("tag1", single.ItemId);
        Assert.Equal(1.5d, WireValueCodec.Decode(single.Type, single.Value));
    }

    [Fact]
    public void Frame_RoundTripsThroughChunkedReads()
    {
        WorkerFrame frame = WorkerFrame.Create(WorkerFrameTypes.Heartbeat, 7, new WorkerHeartbeat(2, 1234, 99));
        using var chunked = new ChunkedReadStream(WriteToArray(frame), chunk: 3);

        WorkerFrame? read = FrameCodec.Read(chunked);

        Assert.NotNull(read);
        Assert.Equal(7, read!.Seq);
        WorkerHeartbeat? payload = read.PayloadAs<WorkerHeartbeat>();
        Assert.NotNull(payload);
        Assert.Equal(1234, payload!.UptimeMs);
    }

    [Fact]
    public async Task Frame_RoundTripsThroughAsyncChunkedReads()
    {
        WorkerFrame frame = WorkerFrame.Create(WorkerFrameTypes.Ready, 1, new WorkerReady("1", 123, ".\\mesadm1"));
        using var chunked = new ChunkedReadStream(WriteToArray(frame), chunk: 1);

        WorkerFrame? read = await FrameCodec.ReadAsync(chunked);

        Assert.NotNull(read);
        Assert.Equal(WorkerFrameTypes.Ready, read!.Type);
        Assert.Equal(".\\mesadm1", read.PayloadAs<WorkerReady>()!.Account);
    }

    [Fact]
    public void Read_AtStreamEnd_ReturnsNull()
    {
        using var stream = new MemoryStream();

        Assert.Null(FrameCodec.Read(stream));
    }

    [Fact]
    public async Task ReadAsync_AtStreamEnd_ReturnsNull()
    {
        using var stream = new MemoryStream();

        Assert.Null(await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public void Read_TruncatedPrefix_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2 });

        Assert.Throws<WorkerProtocolException>(() => FrameCodec.Read(stream));
    }

    [Fact]
    public void Read_OversizedFrameLength_Throws()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(FrameCodec.MaxFrameBytes + 1));

        WorkerProtocolException ex = Assert.Throws<WorkerProtocolException>(() => FrameCodec.Read(stream));
        Assert.Contains("length", ex.Message);
    }

    [Fact]
    public void Read_TruncatedPayload_Throws()
    {
        byte[] bytes = BitConverter.GetBytes(64).Concat(new byte[10]).ToArray();
        using var stream = new MemoryStream(bytes);

        Assert.Throws<WorkerProtocolException>(() => FrameCodec.Read(stream));
    }

    [Fact]
    public void Read_MalformedJson_Throws()
    {
        byte[] payload = Encoding.UTF8.GetBytes("this is not json");
        using var stream = new MemoryStream(BitConverter.GetBytes(payload.Length).Concat(payload).ToArray());

        Assert.Throws<WorkerProtocolException>(() => FrameCodec.Read(stream));
    }

    [Fact]
    public void Encode_FrameBeyondTheLimit_Throws()
    {
        WorkerFrame frame = WorkerFrame.Create(
            WorkerFrameTypes.Notice,
            1,
            new WorkerNotice("warning", new string('x', FrameCodec.MaxFrameBytes)));
        using var stream = new MemoryStream();

        Assert.Throws<WorkerProtocolException>(() => FrameCodec.Write(stream, frame));
    }

    public static TheoryData<object?> ScalarValues => new()
    {
        null,
        true,
        (sbyte)-5,
        (byte)250,
        (short)-1000,
        (ushort)60000,
        -123456,
        4000000000u,
        -9000000000L,
        18000000000000000000UL,
        1.5f,
        3.14159265358979d,
        "text",
        new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        new byte[] { 1, 2, 3, 250 }
    };

    [Theory]
    [MemberData(nameof(ScalarValues))]
    public void WireValueCodec_PreservesTheExactClrType(object? value)
    {
        (string type, JsonElement json) = WireValueCodec.Encode(value);
        object? decoded = WireValueCodec.Decode(type, json);

        if (value is null)
        {
            Assert.Null(decoded);
            return;
        }

        Assert.Equal(value.GetType(), decoded!.GetType());
        if (value is byte[] bytes)
        {
            Assert.Equal(bytes, (byte[])decoded);
        }
        else
        {
            Assert.Equal(value, decoded);
        }
    }

    [Fact]
    public void WireValueCodec_DateTime_KeepsUtcKind()
    {
        var instant = new DateTime(2026, 10, 1, 12, 30, 45, DateTimeKind.Utc);

        (string type, JsonElement json) = WireValueCodec.Encode(instant);

        var decoded = (DateTime)WireValueCodec.Decode(type, json)!;
        Assert.Equal(instant, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.Kind);
    }

    [Fact]
    public void WireValueCodec_UnsupportedType_Throws()
    {
        Assert.Throws<NotSupportedException>(() => WireValueCodec.Encode(1.5m));
    }

    [Fact]
    public void WireValueCodec_UnknownTag_Throws()
    {
        Assert.Throws<WorkerProtocolException>(() => WireValueCodec.Decode("nope", default));
    }

    [Fact]
    public void PayloadAs_ReturnsNullWhenThePayloadIsAbsent()
    {
        WorkerFrame frame = WorkerFrame.Create<object?>(WorkerFrameTypes.Ping, 1, null);

        Assert.Null(frame.PayloadAs<WorkerPong>());
    }

    private static WireValue Value(string itemId, object? value)
    {
        (string type, JsonElement json) = WireValueCodec.Encode(value);
        return new WireValue(itemId, type, json, DateTime.UtcNow, 192, true);
    }

    private static byte[] WriteToArray(WorkerFrame frame)
    {
        using var buffer = new MemoryStream();
        FrameCodec.Write(buffer, frame);
        return buffer.ToArray();
    }
}
