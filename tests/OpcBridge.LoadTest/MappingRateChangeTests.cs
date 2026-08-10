using System.Collections.Concurrent;
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
/// A non-DA source (MX Component, serial drivers, UA sources) must start a poller for a
/// newly introduced per-tag poll rate. Regression: changing a tag's update rate on the
/// Maps faceplate froze its values, because only <see cref="OpcDaClient"/> sources had
/// their pollers rebuilt when mappings changed — the new rate group had no running poller.
/// </summary>
[Collection(nameof(DaLinkApiAppCollection))]
public sealed class MappingRateChangeTests
{
    [Fact]
    public async Task NonDaSource_PerTagRateChange_StartsPollerForNewRate()
    {
        // MappingStore persists to mappings.json in the test bin directory; a stale file
        // from a previous run would silently pre-seed the store (keys already exist, so
        // Add becomes a no-op) and mask the regression. Isolate like other store tests.
        string mappingsPath = Path.Combine(AppContext.BaseDirectory, "mappings.json");
        string linksPath = Path.Combine(AppContext.BaseDirectory, "links.json");
        string sourcesPath = Path.Combine(AppContext.BaseDirectory, "sources.json");
        foreach (string path in new[] { mappingsPath, linksPath, sourcesPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        BridgeState state = new(Options.Create(new BridgeOptions()));
        DaRuntimeSettings settings = new(Options.Create(new DaClientOptions
        {
            Sources =
            [
                new DaSourceOptions { SourceId = "mx1", DisplayName = "MX", ProgId = "Test.Server.1", Host = "localhost" }
            ]
        }));
        MappingStore mappingStore = new(Options.Create(new BridgeOptions()));
        mappingStore.Add(
        [
            new TagMapping { SourceId = "mx1", ItemId = "D0", DisplayName = "D0", PollRateMs = 1000 },
            new TagMapping { SourceId = "mx1", ItemId = "D1", DisplayName = "D1", PollRateMs = 1000 }
        ]);
        DaLinkStore linkStore = new(Options.Create(new BridgeOptions()));
        UaServerHost uaServer = new(
            Options.Create(new UaServerOptions { EndpointUrl = "opc.tcp://127.0.0.1:4101/OpcBridge" }),
            loggerFactory.CreateLogger<UaServerHost>(),
            loggerFactory);
        RecordingSourceClientFactory factory = new();
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

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await worker.StartAsync(cts.Token);

        try
        {
            // The 1 s poller is running: D1 is being read from the source.
            Assert.True(
                await WaitUntilAsync(() => factory.Client.ReadCount("D1") > 0, TimeSpan.FromSeconds(10)),
                "D1 was never read before the rate change");

            // Simulate the Maps faceplate save: move D1 to a 500 ms poll rate.
            Assert.True(mappingStore.TryUpdate(
                new TagMapping { SourceId = "mx1", ItemId = "D1", DisplayName = "D1", PollRateMs = 500 },
                out _));

            // Give the coordinator time to rebuild the cache and let the old 1 s poller drain
            // any in-flight reads. With the bug, D1 is never read again after this settle point
            // (its values freeze). With the fix, a new 500 ms poller keeps reading it.
            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
            int readsAfterSettle = factory.Client.ReadCount("D1");
            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
            int readsAfterWindow = factory.Client.ReadCount("D1");

            Assert.True(
                readsAfterWindow > readsAfterSettle,
                $"D1 stopped being read after its poll rate changed (reads frozen at {readsAfterSettle}) — no poller exists for the new rate");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, CancellationToken.None);
        }

        return condition();
    }

    private sealed class RecordingSourceClientFactory : SourceClientFactory
    {
        public RecordingSourceClient Client { get; } = new();

        public override ISourceClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
            => Client;
    }

    /// <summary>Non-DA source client: records every tag the poller asks it to read.</summary>
    private sealed class RecordingSourceClient : ISourceClient
    {
        private readonly ConcurrentQueue<string> reads_ = new();

        public int ReadCount(string itemId) => reads_.Count(r => string.Equals(r, itemId, StringComparison.Ordinal));

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<BridgeValue>> ReadAsync(
            IReadOnlyList<TagMapping> mappings,
            CancellationToken cancellationToken)
        {
            var values = new List<BridgeValue>(mappings.Count);
            foreach (TagMapping mapping in mappings)
            {
                reads_.Enqueue(mapping.ItemId);
                values.Add(new BridgeValue(mapping.SourceId, mapping.ItemId, 1, DateTime.UtcNow, 192, true));
            }

            return Task.FromResult<IReadOnlyList<BridgeValue>>(values);
        }

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
