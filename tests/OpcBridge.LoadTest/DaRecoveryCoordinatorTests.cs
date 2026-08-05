using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Influx;
using OpcBridge.Mqtt;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Tests the coordinator's reconnect-with-backoff and subscription-watchdog behavior.
/// </summary>
[Collection(nameof(DaLinkApiAppCollection))]
public sealed class DaRecoveryCoordinatorTests
{
    [Fact]
    public async Task ConnectFailure_RetriesWithBackoff_UntilConnected()
    {
        ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        BridgeState state = new(Options.Create(new BridgeOptions()));
        DaRuntimeSettings settings = new(Options.Create(new DaClientOptions
        {
            Sources =
            [
                new DaSourceOptions { SourceId = "recover", DisplayName = "Recover", ProgId = "Test.Server.1", Host = "localhost" }
            ]
        }));
        MappingStore mappingStore = new(Options.Create(new BridgeOptions()));
        DaLinkStore linkStore = new(Options.Create(new BridgeOptions()));
        UaServerHost uaServer = new(
            Options.Create(new UaServerOptions { EndpointUrl = "opc.tcp://127.0.0.1:4859/OpcBridge" }),
            loggerFactory.CreateLogger<UaServerHost>(),
            loggerFactory);

        var factory = new RecoveringSourceClientFactory(failingConnectAttempts: 2);
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

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(25));
        await worker.StartAsync(cts.Token);

        try
        {
            // First two connect attempts throw SourceConnectionLostException; the coordinator
            // must NOT mark the source Faulted — it retries with backoff and eventually connects.
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            string? lastState = null;
            int lastConnectCalls = 0;
            HashSet<string> seenStates = new(StringComparer.Ordinal);
            while (DateTime.UtcNow < deadline)
            {
                lastConnectCalls = factory.Client.ConnectCalls;
                lastState = state.GetStatus().Sources
                    .FirstOrDefault(s => string.Equals(s.SourceId, "recover", StringComparison.OrdinalIgnoreCase))
                    ?.ConnectionState;
                if (lastState is not null)
                {
                    seenStates.Add(lastState);
                }

                if (string.Equals(lastState, "Connected", StringComparison.OrdinalIgnoreCase) && lastConnectCalls >= 3)
                {
                    break;
                }

                await Task.Delay(100, CancellationToken.None);
            }

            Assert.True(
                lastConnectCalls >= 3,
                $"expected retries (>=3 connect attempts), got {lastConnectCalls}; last state {lastState}");
            Assert.Equal("Connected", lastState);
            // Retryable failures must surface as Reconnecting — never as the terminal Faulted.
            Assert.DoesNotContain("Faulted", seenStates);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ScanWatchdog_StaleSubscribedSource_Enqueues()
    {
        BridgeWorker worker = CreateWorker(seedSource: true);
        SetActivity(worker, "recover", DateTime.UtcNow.AddSeconds(-120));

        ConcurrentQueue<string> queue = RunScan(worker);

        Assert.True(queue.TryDequeue(out string? id), "stale subscribed source should be enqueued");
        Assert.Equal("recover", id);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void ScanWatchdog_FreshActivity_NotEnqueued()
    {
        BridgeWorker worker = CreateWorker(seedSource: true);
        SetActivity(worker, "recover", DateTime.UtcNow);

        ConcurrentQueue<string> queue = RunScan(worker);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void ScanWatchdog_NoActivity_NotEnqueued()
    {
        // A subscription source that never delivered a callback (e.g. static tags)
        // must not be flagged — quiet-but-healthy.
        BridgeWorker worker = CreateWorker(seedSource: true);
        ConcurrentQueue<string> queue = RunScan(worker);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void ScanWatchdog_DisabledTimeout_NotEnqueued()
    {
        BridgeWorker worker = CreateWorker(seedSource: true);
        SetActivity(worker, "recover", DateTime.UtcNow.AddSeconds(-120));
        ConcurrentQueue<string> queue = RunScan(worker, watchdogTimeoutMs: 0);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void ScanWatchdog_PollingSource_NotEnqueued()
    {
        // Client without an active subscription: device reads detect death, watchdog stays out.
        BridgeWorker worker = CreateWorker(seedSource: true);
        SetActivity(worker, "recover", DateTime.UtcNow.AddSeconds(-120));
        ConcurrentQueue<string> queue = RunScan(worker, subscriptionActive: false);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public async Task UaConnectFailure_IsReportedAsRetryable()
    {
        // A real OpcUaSourceClient pointed at an unreachable endpoint must surface the
        // failure as SourceConnectionLostException so the coordinator retries with backoff
        // instead of marking the source Faulted forever.
        OpcUaSourceClient client = new(new OpcUaSourceClientOptions
        {
            SourceId = "ua-retry",
            DisplayName = "UA Retry",
            EndpointUrl = "opc.tcp://127.0.0.1:1/opcuasim/",
            SecurityMode = "None",
            SecurityPolicy = "None",
            ReconnectDelayMs = 1000
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<SourceConnectionLostException>(
            () => client.ConnectAsync(cts.Token));
    }

    // --- harness ---
    private static BridgeWorker CreateWorker(
        bool seedSource = false,
        bool subscriptionActive = true)
    {
        ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        BridgeState state = new(Options.Create(new BridgeOptions()));
        DaClientOptions clientOptions = new();
        if (seedSource)
        {
            clientOptions.Sources =
            [
                new DaSourceOptions
                {
                    SourceId = "recover",
                    DisplayName = "Recover",
                    ProgId = "Test.Server.1",
                    Host = "localhost"
                }
            ];
        }
        DaRuntimeSettings settings = new(Options.Create(clientOptions));
        MappingStore mappingStore = new(Options.Create(new BridgeOptions()));
        DaLinkStore linkStore = new(Options.Create(new BridgeOptions()));
        UaServerHost uaServer = new(
            Options.Create(new UaServerOptions { EndpointUrl = "opc.tcp://127.0.0.1:4859/OpcBridge" }),
            loggerFactory.CreateLogger<UaServerHost>(),
            loggerFactory);
        SourceClientFactory factory = new SubscriptionSourceClientFactory(new SubscriptionSourceClient(subscriptionActive));
        return new BridgeWorker(
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
    }

    private static void SetActivity(BridgeWorker worker, string sourceId, DateTime timestamp)
    {
        FieldInfo? field = typeof(BridgeWorker).GetField("watchdog_activity_", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var activity = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        activity[sourceId] = timestamp;
        field!.SetValue(worker, activity);
    }

    private static ConcurrentQueue<string> RunScan(BridgeWorker worker, int watchdogTimeoutMs = 60000, bool subscriptionActive = true)
    {
        var source = new DaSourceRuntimeSettings(
            "recover",
            "Recover",
            SourceTypes.OpcUa,
            1000,
            true,
            50000,
            null,
            new OpcUaSourceOptions(
                "opc.tcp://127.0.0.1:1/opcuasim/",
                "None",
                "None",
                null,
                null,
                60000,
                5000,
                watchdogTimeoutMs),
            null,
            null);
        var sessions = new Dictionary<string, BridgeWorker.SourceSession>(StringComparer.OrdinalIgnoreCase)
        {
            ["recover"] = new BridgeWorker.SourceSession(source, new SubscriptionSourceClient(subscriptionActive))
        };
        var queue = new ConcurrentQueue<string>();
        MethodInfo? method = typeof(BridgeWorker).GetMethod("ScanWatchdog", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(worker, new object[] { sessions, queue });
        return queue;
    }

    private sealed class RecoveringSourceClientFactory : SourceClientFactory
    {
        public RecoveringSourceClient Client { get; }
        public RecoveringSourceClientFactory(int failingConnectAttempts)
        {
            Client = new RecoveringSourceClient(failingConnectAttempts);
        }
        public override ISourceClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
            => Client;
    }

    private sealed class RecoveringSourceClient : ISourceClient, ISubscribableSourceClient, ISubscriptionActiveSource
    {
        private readonly int failingConnectAttempts_;
        private int connectCalls_;
        public int ConnectCalls => Volatile.Read(ref connectCalls_);
        public bool IsSubscriptionActive => false;
        public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived
        {
            add { }
            remove { }
        }
        public RecoveringSourceClient(int failingConnectAttempts)
        {
            failingConnectAttempts_ = failingConnectAttempts;
        }
        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref connectCalls_);
            if (attempt <= failingConnectAttempts_)
            {
                throw new SourceConnectionLostException($"Simulated connect failure (attempt {attempt}).");
            }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<BridgeValue>> ReadAsync(
            IReadOnlyList<TagMapping> mappings,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BridgeValue>>(Array.Empty<BridgeValue>());
        public Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken)
            => Task.FromResult(false);
        public bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights)
        {
            canonicalDataType = null;
            accessRights = null;
            return false;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SubscriptionSourceClientFactory : SourceClientFactory
    {
        private readonly ISourceClient client_;
        public SubscriptionSourceClientFactory(ISourceClient client)
        {
            client_ = client;
        }
        public override ISourceClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
            => client_;
    }

    private sealed class SubscriptionSourceClient : ISourceClient, ISubscribableSourceClient, ISubscriptionActiveSource
    {
        private readonly bool subscriptionActive_;
        public SubscriptionSourceClient(bool subscriptionActive)
        {
            subscriptionActive_ = subscriptionActive;
        }
        public bool IsSubscriptionActive => subscriptionActive_;
        public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived
        {
            add { }
            remove { }
        }
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<BridgeValue>> ReadAsync(
            IReadOnlyList<TagMapping> mappings,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BridgeValue>>(Array.Empty<BridgeValue>());
        public Task<bool> WriteAsync(string itemId, object? value, CancellationToken cancellationToken)
            => Task.FromResult(false);
        public bool TryGetTagMetadata(string itemId, out short? canonicalDataType, out int? accessRights)
        {
            canonicalDataType = null;
            accessRights = null;
            return false;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
}
