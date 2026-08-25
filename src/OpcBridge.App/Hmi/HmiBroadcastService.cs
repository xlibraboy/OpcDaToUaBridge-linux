using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App.Hmi;

public sealed class HmiBroadcastService : IHostedService
{
    private readonly BridgeState bridge_state_;
    private readonly MappingStore mapping_store_;
    private readonly IHubContext<HmiHub> hub_;
    private readonly int flush_ms_;
    private readonly object batch_lock_ = new();
    private readonly Dictionary<string, HmiValueDelta> pending_ = new(StringComparer.OrdinalIgnoreCase);
    private Timer? flush_timer_;

    public HmiBroadcastService(
        BridgeState bridgeState,
        MappingStore mappingStore,
        IHubContext<HmiHub> hub,
        IOptions<HmiOptions>? options = null)
    {
        bridge_state_ = bridgeState;
        mapping_store_ = mappingStore;
        hub_ = hub;
        flush_ms_ = HmiOptions.ClampBroadcastFlushMs(
            options?.Value.BroadcastFlushMs ?? HmiOptions.DefaultBroadcastFlushMs);
    }

    public int BroadcastFlushMs => flush_ms_;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        bridge_state_.ValueUpdated += OnValueUpdated;
        mapping_store_.Changed += OnMappingsChanged;
        TimeSpan period = TimeSpan.FromMilliseconds(flush_ms_);
        flush_timer_ = new Timer(Flush, null, period, period);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        bridge_state_.ValueUpdated -= OnValueUpdated;
        mapping_store_.Changed -= OnMappingsChanged;
        flush_timer_?.Dispose();
        flush_timer_ = null;
        return Task.CompletedTask;
    }

    private void OnValueUpdated(BridgeValue value)
    {
        HmiValueDelta delta = new()
        {
            SourceId = value.SourceId,
            ItemId = value.ItemId,
            Value = value.Value,
            TimestampUtc = value.TimestampUtc,
            DaQuality = value.DaQuality,
            IsGood = value.IsGood
        };
        string key = string.Concat(value.SourceId, "::", value.ItemId);
        lock (batch_lock_)
        {
            pending_[key] = delta;
        }
    }

    private void OnMappingsChanged(long version)
    {
        _ = hub_.Clients.All.SendAsync("mappingsChanged", new HmiMappingsChanged { Version = version });
    }

    private void Flush(object? state)
    {
        HmiValueDelta[] batch;
        lock (batch_lock_)
        {
            if (pending_.Count == 0)
            {
                return;
            }

            batch = pending_.Values.ToArray();
            pending_.Clear();
        }

        _ = hub_.Clients.All.SendAsync("values", batch);
    }
}
