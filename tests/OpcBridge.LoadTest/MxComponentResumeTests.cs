using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Drivers.MxComponent;
using OpcBridge.Influx;
using OpcBridge.Mqtt;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Issue #5: resuming a paused MX Component source must reconnect. A failed open is transient
/// (the logical station can still be claimed by the session the pause just released), so it is
/// reported as a lost connection and retried with backoff — never parked in Faulted, which used
/// to be terminal because the coordinator only retries SourceConnectionLostException.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MxComponentResumeTests : IDisposable
{
    private static readonly string SourcesFile = Path.Combine(AppContext.BaseDirectory, "sources.json");

    public MxComponentResumeTests() => DeleteSourcesFile();

    public void Dispose() => DeleteSourcesFile();

    [Fact]
    public void Classifier_TreatsAnOpenFailureAsTransient()
    {
        // The resume race: Open() returns non-zero while the previous session still holds the
        // station. Also covers the inner retry timing out.
        Assert.True(MxConnectErrorClassifier.IsTransient(
            new InvalidOperationException("MX Component Open failed with error code 2147483647 (0x7FFFFFFF).")));
        Assert.True(MxConnectErrorClassifier.IsTransient(new TimeoutException("no answer from the PLC")));
    }

    [Fact]
    public void Classifier_KeepsTerminalFailuresTerminal()
    {
        // Nothing here can be fixed by retrying, and retrying forever would keep every source's
        // status churning: these stay Faulted.
        Assert.False(MxConnectErrorClassifier.IsTransient(
            new MxComponentUnavailableException("MX Component 4 is not registered on this machine.")));
        Assert.False(MxConnectErrorClassifier.IsTransient(new PlatformNotSupportedException()));
        Assert.False(MxConnectErrorClassifier.IsTransient(new ObjectDisposedException("client")));
        Assert.False(MxConnectErrorClassifier.IsTransient(new OperationCanceledException()));
    }

    [Fact]
    public async Task ConnectAsync_TransientOpenFailure_IsReportedAsLostConnection()
    {
        var session = new FakeMxSession
        {
            ConnectFailures = 1,
            ConnectFailure = new InvalidOperationException("MX Component Open failed with error code 1 (0x00000001).")
        };

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "wetend", LogicalStationNumber = 0, RetryCount = 0 },
            session);

        SourceConnectionLostException error = await Assert.ThrowsAsync<SourceConnectionLostException>(
            () => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("wetend", error.Message);
        Assert.Contains("logical station 0", error.Message);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task ConnectAsync_NotInstalled_StaysTerminal()
    {
        var session = new FakeMxSession
        {
            ConnectFailures = 1,
            ConnectFailure = new MxComponentUnavailableException("MX Component 4 is not registered on this machine.")
        };

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);

        await Assert.ThrowsAsync<MxComponentUnavailableException>(
            () => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_NonWindows_StaysTerminal()
    {
        var session = new FakeMxSession
        {
            ConnectFailures = 1,
            ConnectFailure = new PlatformNotSupportedException("MX Component 4 (ActUtlType) requires Windows.")
        };

        await using var client = new MxComponentClient(
            new MxComponentClientOptions { SourceId = "mx", RetryCount = 0 },
            session);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PauseResume_TransientConnectFailures_RecoverToConnected()
    {
        ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        BridgeState state = new(Options.Create(new BridgeOptions()));
        DaRuntimeSettings settings = new(Options.Create(new DaClientOptions()));
        settings.UpsertSource(new DaSourceRuntimeSettings(
            "wetend_plc-mhi",
            "WETEND PLC MHI",
            SourceTypes.MxComponent,
            1000,
            true,
            50000,
            OpcDa: null,
            OpcUa: null,
            Melsec: null,
            S7200: null,
            MxComponent: new MxComponentSourceOptions(LogicalStationNumber: 0, TimeoutMs: 3000, RetryCount: 2)));

        MappingStore mappingStore = new(Options.Create(new BridgeOptions()));
        InterlinkStore linkStore = new(Options.Create(new BridgeOptions()));
        UaServerHost uaServer = new(
            Options.Create(new UaServerOptions { EndpointUrl = "opc.tcp://127.0.0.1:4100/OpcBridge" }),
            loggerFactory.CreateLogger<UaServerHost>(),
            loggerFactory);

        var factory = new FlakyMxFactory();
        BridgeWorker worker = new(
            uaServer,
            state,
            mappingStore,
            linkStore,
            settings,
            factory,
            Options.Create(new BridgeOptions()),
            loggerFactory.CreateLogger<BridgeWorker>(),
            new MqttBridge(loggerFactory.CreateLogger<MqttBridge>()),
            new MqttRuntimeSettings(Options.Create(new MqttBrokerOptions())),
            new MqttValueStore(),
            new NoopInfluxWriter(),
            new InfluxRuntimeSettings(Options.Create(new InfluxOptions())));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        await worker.StartAsync(cts.Token);

        try
        {
            Assert.True(
                await WaitForStateAsync(state, "wetend_plc-mhi", "Connected", TimeSpan.FromSeconds(20)),
                "the source must connect once before it can be paused");

            // Pause: the worker must release the upstream connection, not just report Paused.
            settings.SetPaused("wetend_plc-mhi", true);
            Assert.True(
                await WaitForStateAsync(state, "wetend_plc-mhi", "Paused", TimeSpan.FromSeconds(20)),
                "a paused source must settle in the Paused state");
            Assert.True(factory.Disposals >= 1, "pausing must dispose the source's client (release the COM port)");

            // Resume: the next two opens fail the way a resume does when the release has not
            // landed yet. The coordinator must retry them and reach Connected — never Faulted.
            HashSet<string> observed = new(StringComparer.OrdinalIgnoreCase);
            settings.SetPaused("wetend_plc-mhi", false);
            bool connected = await WaitForStateAsync(
                state,
                "wetend_plc-mhi",
                "Connected",
                TimeSpan.FromSeconds(30),
                observed);

            Assert.True(connected, $"resumed source never reconnected; states seen: {string.Join(", ", observed)}");
            Assert.DoesNotContain("Faulted", observed);
            Assert.True(factory.ConnectCalls >= 4, $"expected retries after resume, got {factory.ConnectCalls} connects");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> WaitForStateAsync(
        BridgeState state,
        string sourceId,
        string expected,
        TimeSpan timeout,
        HashSet<string>? observed = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            string? current = state.GetStatus().Sources
                .FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                ?.ConnectionState;

            if (current is not null)
            {
                observed?.Add(current);
                if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            await Task.Delay(50, CancellationToken.None);
        }

        return false;
    }

    private static void DeleteSourcesFile()
    {
        try
        {
            if (File.Exists(SourcesFile))
            {
                File.Delete(SourcesFile);
            }
        }
        catch (IOException)
        {
            // A parallel app host may be holding it; the suite already treats this file as shared.
        }
    }

    private sealed class FlakyMxFactory : SourceClientFactory
    {
        private readonly object sync_ = new();
        private int connectCalls_;
        private int disposals_;

        public int ConnectCalls
        {
            get { lock (sync_) { return connectCalls_; } }
        }

        public int Disposals
        {
            get { lock (sync_) { return disposals_; } }
        }

        public override ISourceClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source) =>
            new FlakyMxClient(this);

        /// <summary>The initial connect succeeds; the first two connects after the pause fail.</summary>
        private bool NextConnectFails()
        {
            lock (sync_)
            {
                connectCalls_++;
                return connectCalls_ is 2 or 3;
            }
        }

        private void NoteDisposal() => Interlocked.Increment(ref disposals_);

        private sealed class FlakyMxClient : ISourceClient
        {
            private readonly FlakyMxFactory owner_;

            public FlakyMxClient(FlakyMxFactory owner) => owner_ = owner;

            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                if (owner_.NextConnectFails())
                {
                    throw new SourceConnectionLostException(
                        "MX Component source 'wetend_plc-mhi' could not open logical station 0: station still claimed.");
                }

                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<BridgeValue>> ReadAsync(
                IReadOnlyList<TagMapping> mappings,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<BridgeValue>>(Array.Empty<BridgeValue>());

            public Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken) =>
                Task.FromResult(false);

            public bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights)
            {
                canonicalDataType = null;
                accessRights = null;
                return false;
            }

            public ValueTask DisposeAsync()
            {
                owner_.NoteDisposal();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class NoopInfluxWriter : IInfluxWriter
    {
        public InfluxConnectionState State { get; set; } = InfluxConnectionState.Disconnected;

        public event Action<InfluxConnectionState>? StateChanged;

        public Task ConnectAsync(InfluxOptions options, CancellationToken ct)
        {
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task WritePointAsync(BridgeValue value, string? displayName, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMxSession : IMxComponentSession
    {
        public int ConnectFailures { get; set; }

        public Exception ConnectFailure { get; set; } = new InvalidOperationException("connect failed");

        public bool IsOpen { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (ConnectFailures > 0)
            {
                ConnectFailures--;
                throw ConnectFailure;
            }

            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadWordsAsync(string device, int count, CancellationToken cancellationToken) =>
            Task.FromResult(new ushort[count]);

        public Task WriteWordsAsync(string device, IReadOnlyList<ushort> words, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task WriteBitAsync(string device, bool value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<(string CpuName, string CpuCode)> GetCpuTypeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(("A3NCPU", "0030"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
