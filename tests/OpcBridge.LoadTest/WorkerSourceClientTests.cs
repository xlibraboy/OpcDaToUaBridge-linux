using System.IO.Pipelines;
using System.Text.Json;
using OpcBridge.App;
using OpcBridge.Client.Workers;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class WorkerSourceClientTests
{
    [Fact]
    public async Task Connect_Ack_CarriesTheSubscriptionFlag()
    {
        (WorkerSourceClient client, _, _) = CreateClient(
            _ => (WorkerFrameTypes.Ack, (object)new WorkerAck(true, SubscriptionActive: true)));

        await client.ConnectAsync(CancellationToken.None);

        Assert.True(client.IsSubscriptionActive);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Connect_TransientAck_ThrowsSourceConnectionLost()
    {
        (WorkerSourceClient client, _, _) = CreateClient(
            _ => (WorkerFrameTypes.Ack, (object)new WorkerAck(false, "worker busy", Transient: true)));

        await Assert.ThrowsAsync<SourceConnectionLostException>(() => client.ConnectAsync(CancellationToken.None));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Read_MapsWireValuesBackToBridgeValues()
    {
        (WorkerSourceClient client, FakeWorker worker, _) = CreateClient(frame => frame.Type switch
        {
            WorkerFrameTypes.Connect => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)),
            WorkerFrameTypes.Read => (WorkerFrameTypes.ReadResult, (object)new WorkerReadResult(new[]
            {
                Wire("Tag1", 12.5d)
            })),
            _ => null
        });

        await client.ConnectAsync(CancellationToken.None);
        IReadOnlyList<BridgeValue> values = await client.ReadAsync(
            new[] { new TagMapping { ItemId = "Tag1", PollRateMs = 500, DataType = "Boolean" } },
            CancellationToken.None);

        BridgeValue value = Assert.Single(values);
        Assert.Equal("pmd", value.SourceId);
        Assert.Equal("Tag1", value.ItemId);
        Assert.Equal(12.5d, value.Value);
        Assert.True(value.IsGood);

        // The request carries the tag's poll rate (worker rate groups) and configured type:
        // without the type the worker rebuilds the mapping as "Double" and asks the DA server
        // to convert every item to VT_R8, so booleans arrive as doubles.
        WorkerFrame read = Assert.Single(worker.Received, frame => frame.Type == WorkerFrameTypes.Read);
        WorkerReadRequest request = read.PayloadAs<WorkerReadRequest>()!;
        WorkerTagRef tag = Assert.Single(request.Tags);
        Assert.Equal(500, tag.PollRateMs);
        Assert.Equal("Boolean", tag.DataType);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Read_TransientError_ThrowsSourceConnectionLost()
    {
        (WorkerSourceClient client, _, _) = CreateClient(frame => frame.Type switch
        {
            WorkerFrameTypes.Connect => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)),
            WorkerFrameTypes.Read => (WorkerFrameTypes.ReadResult, (object)new WorkerReadResult(
                Array.Empty<WireValue>(), "rpc dead", Transient: true)),
            _ => null
        });

        await client.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<SourceConnectionLostException>(() => client.ReadAsync(
            new[] { new TagMapping { ItemId = "Tag1" } },
            CancellationToken.None));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Write_SendsTypedValues_AndReturnsTheWorkerResult()
    {
        (WorkerSourceClient client, FakeWorker worker, _) = CreateClient(frame => frame.Type switch
        {
            WorkerFrameTypes.Connect => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)),
            WorkerFrameTypes.Write => (WorkerFrameTypes.WriteResult, (object)new WorkerWriteResult(true)),
            _ => null
        });

        await client.ConnectAsync(CancellationToken.None);
        bool ok = await client.WriteAsync("Tag1", 5, CancellationToken.None);

        Assert.True(ok);
        WorkerFrame write = Assert.Single(worker.Received, frame => frame.Type == WorkerFrameTypes.Write);
        WorkerWriteRequest request = write.PayloadAs<WorkerWriteRequest>()!;
        Assert.Equal("Tag1", request.ItemId);
        Assert.Equal(WireValueTypes.Int32, request.Type);
        Assert.Equal(5, request.Value.GetInt32());
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Metadata_IsCachedAfterTheFirstLookup()
    {
        int metadataRequests = 0;
        (WorkerSourceClient client, FakeWorker worker, _) = CreateClient(frame => frame.Type switch
        {
            WorkerFrameTypes.Connect => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)),
            WorkerFrameTypes.Metadata => (WorkerFrameTypes.MetadataResult,
                (object)new WorkerMetadataResult(true, 5, 3)),
            _ => null
        });
        // Count metadata requests without disturbing the responder.
        worker.OnFrame = frame =>
        {
            if (frame.Type == WorkerFrameTypes.Metadata)
            {
                Interlocked.Increment(ref metadataRequests);
            }
        };

        await client.ConnectAsync(CancellationToken.None);

        Assert.True(client.TryGetTagMetadata("Tag1", out short? dataType, out int? accessRights));
        Assert.Equal((short)5, dataType);
        Assert.Equal(3, accessRights);
        Assert.True(client.TryGetTagMetadata("Tag1", out _, out _));
        Assert.Equal(1, metadataRequests);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ValuesPush_ForThisSource_RaisesValuesReceived()
    {
        (WorkerSourceClient client, FakeWorker worker, _) = CreateClient(
            _ => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)));

        await client.ConnectAsync(CancellationToken.None);
        var received = new TaskCompletionSource<IReadOnlyList<BridgeValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ValuesReceived += values => received.TrySetResult(values);

        await worker.SendAsync(WorkerFrame.Create(
            WorkerFrameTypes.Values,
            99,
            new WorkerValues("pmd", new[] { Wire("Tag1", 7L) })));

        IReadOnlyList<BridgeValue> values = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        BridgeValue value = Assert.Single(values);
        Assert.Equal(7L, value.Value);
        Assert.Equal(typeof(long), value.Value!.GetType());
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ValuesPush_ForAnotherSource_IsIgnored()
    {
        (WorkerSourceClient client, FakeWorker worker, _) = CreateClient(
            _ => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)));

        await client.ConnectAsync(CancellationToken.None);
        var received = new TaskCompletionSource<IReadOnlyList<BridgeValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ValuesReceived += values => received.TrySetResult(values);

        await worker.SendAsync(WorkerFrame.Create(
            WorkerFrameTypes.Values,
            98,
            new WorkerValues("other-source", new[] { Wire("Other.Tag", 1d) })));
        await worker.SendAsync(WorkerFrame.Create(
            WorkerFrameTypes.Values,
            99,
            new WorkerValues("pmd", new[] { Wire("Tag1", 2d) })));

        IReadOnlyList<BridgeValue> values = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Tag1", Assert.Single(values).ItemId);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DisconnectsAndReleasesTheWorker()
    {
        (WorkerSourceClient client, FakeWorker worker, TestWorkerHost host) = CreateClient(frame => frame.Type switch
        {
            WorkerFrameTypes.Connect => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)),
            WorkerFrameTypes.Disconnect => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)),
            _ => null
        });

        await client.ConnectAsync(CancellationToken.None);
        await client.DisposeAsync();

        await WaitUntilAsync(
            () => worker.Received.Any(frame => frame.Type == WorkerFrameTypes.Disconnect),
            "disconnect frame");
        Assert.Equal(1, host.Released);
    }

    [Fact]
    public async Task ChannelClosed_WhileConnected_RaisesConnectionLost()
    {
        (WorkerSourceClient client, FakeWorker worker, _) = CreateClient(
            _ => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)));
        await client.ConnectAsync(CancellationToken.None);

        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionLost += error => lost.TrySetResult(error);

        worker.Close();

        Exception error = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<SourceConnectionLostException>(error);
        Assert.Contains("pmd", error.Message, StringComparison.Ordinal);
        Assert.False(client.IsSubscriptionActive);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DoesNotRaiseConnectionLost()
    {
        (WorkerSourceClient client, _, _) = CreateClient(frame => frame.Type switch
        {
            WorkerFrameTypes.Connect => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)),
            WorkerFrameTypes.Disconnect => (WorkerFrameTypes.Ack, (object)new WorkerAck(true)),
            _ => null
        });
        await client.ConnectAsync(CancellationToken.None);

        bool raised = false;
        client.ConnectionLost += _ => raised = true;

        await client.DisposeAsync();
        await Task.Delay(100);

        Assert.False(raised);
    }

    private static (WorkerSourceClient Client, FakeWorker Worker, TestWorkerHost Host) CreateClient(
        Func<WorkerFrame, (string Type, object Payload)?> responder)
    {
        (Stream parent, Stream workerSide) = CreateDuplexPair();
        var connection = new WorkerConnection(parent);
        var fakeWorker = new FakeWorker(workerSide)
        {
            // Disconnect is always acked so disposal never waits out its timeout; the tests
            // that care only inspect the received frame.
            Responder = frame => frame.Type == WorkerFrameTypes.Disconnect
                ? (WorkerFrameTypes.Ack, (object)new WorkerAck(true))
                : responder(frame)
        };
        var host = new TestWorkerHost(connection);
        return (new WorkerSourceClient(host, "own:pmd", "pmd"), fakeWorker, host);
    }

    private static WireValue Wire(string itemId, object? value, DateTime? timestamp = null)
    {
        (string type, JsonElement json) = WireValueCodec.Encode(value);
        return new WireValue(itemId, type, json, timestamp ?? DateTime.UtcNow, 192, true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static (Stream Parent, Stream Worker) CreateDuplexPair()
    {
        var parentToWorker = new Pipe();
        var workerToParent = new Pipe();
        return (
            new DuplexStream(parentToWorker.Writer.AsStream(), workerToParent.Reader.AsStream()),
            new DuplexStream(workerToParent.Writer.AsStream(), parentToWorker.Reader.AsStream()));
    }

    private sealed class DuplexStream : Stream
    {
        private readonly Stream _write;
        private readonly Stream _read;

        public DuplexStream(Stream write, Stream read)
        {
            _write = write;
            _read = read;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _read.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _write.WriteAsync(buffer, cancellationToken);

        public override void Flush() => _write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _write.Dispose();
                _read.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeWorker
    {
        private readonly Stream _stream;
        private readonly List<WorkerFrame> _received = new();
        private readonly object _gate = new();

        public FakeWorker(Stream stream)
        {
            _stream = stream;
            _ = Task.Run(LoopAsync);
        }

        public Func<WorkerFrame, (string Type, object Payload)?>? Responder { get; set; }

        public Action<WorkerFrame>? OnFrame { get; set; }

        public IReadOnlyList<WorkerFrame> Received
        {
            get
            {
                lock (_gate)
                {
                    return _received.ToList();
                }
            }
        }

        public Task SendAsync(WorkerFrame frame) => FrameCodec.WriteAsync(_stream, frame);

        /// <summary>Simulates the worker process dying: the pipe closes under the parent.</summary>
        public void Close() => _stream.Dispose();

        private async Task LoopAsync()
        {
            try
            {
                while (true)
                {
                    WorkerFrame? frame = await FrameCodec.ReadAsync(_stream).ConfigureAwait(false);
                    if (frame is null)
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        _received.Add(frame);
                    }

                    OnFrame?.Invoke(frame);

                    if (Responder?.Invoke(frame) is { } response)
                    {
                        await FrameCodec.WriteAsync(
                            _stream,
                            WorkerFrame.Create(response.Type, frame.Seq, response.Payload)).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // The test finished with this pipe.
            }
        }
    }
}
