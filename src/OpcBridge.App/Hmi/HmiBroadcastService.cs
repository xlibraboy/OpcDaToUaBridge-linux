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
    private readonly LogicStore logic_store_;
    private readonly LogicStateStore logic_states_;
    private readonly IHubContext<HmiHub> hub_;
    private readonly int flush_ms_;
    private readonly object batch_lock_ = new();
    private readonly Dictionary<string, HmiValueDelta> pending_ = new(StringComparer.OrdinalIgnoreCase);
    private Timer? flush_timer_;
    private int flushing_;
    private int logic_dirty_ = 1;
    private int timer_pending_;
    private string? last_logic_signature_;

    public HmiBroadcastService(
        BridgeState bridgeState,
        MappingStore mappingStore,
        LogicStore logicStore,
        IHubContext<HmiHub> hub,
        IOptions<HmiOptions>? options = null,
        LogicStateStore? logicStates = null)
    {
        bridge_state_ = bridgeState;
        mapping_store_ = mappingStore;
        logic_store_ = logicStore;
        // The host passes the singleton so the API read and this loop report the same state; a
        // caller without one (a unit test) gets its own memory.
        logic_states_ = logicStates ?? new LogicStateStore();
        hub_ = hub;
        flush_ms_ = HmiOptions.ClampBroadcastFlushMs(
            options?.Value.BroadcastFlushMs ?? HmiOptions.DefaultBroadcastFlushMs);
    }

    public int BroadcastFlushMs => flush_ms_;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        bridge_state_.ValueUpdated += OnValueUpdated;
        mapping_store_.Changed += OnMappingsChanged;
        logic_store_.Changed += OnLogicChanged;
        TimeSpan period = TimeSpan.FromMilliseconds(flush_ms_);
        flush_timer_ = new Timer(Flush, null, period, period);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        bridge_state_.ValueUpdated -= OnValueUpdated;
        mapping_store_.Changed -= OnMappingsChanged;
        logic_store_.Changed -= OnLogicChanged;
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
        // Condition labels and value text come from the mappings, so a mapping edit also
        // re-evaluates the logic snapshot (and pushes it when a rendered state changed).
        Interlocked.Exchange(ref logic_dirty_, 1);
        _ = hub_.Clients.All.SendAsync("mappingsChanged", new HmiMappingsChanged { Version = version });
    }

    private void OnLogicChanged(long version)
    {
        Interlocked.Exchange(ref logic_dirty_, 1);
    }

    private void Flush(object? state)
    {
        // A slow evaluation must not overlap the next tick: the timer can re-enter when a
        // flush takes longer than the period on a loaded host.
        if (Interlocked.Exchange(ref flushing_, 1) == 1)
        {
            return;
        }

        try
        {
            HmiValueDelta[]? batch = null;
            lock (batch_lock_)
            {
                if (pending_.Count > 0)
                {
                    batch = pending_.Values.ToArray();
                    pending_.Clear();
                }
            }

            if (batch is not null)
            {
                _ = hub_.Clients.All.SendAsync("values", batch);
            }

            bool logicDirty = Interlocked.Exchange(ref logic_dirty_, 0) == 1;
            // A running timer is the one state that moves with the clock alone: while one is
            // timing, keep evaluating so it can elapse (and report its progress) even though no
            // value arrived.
            bool timerPending = Volatile.Read(ref timer_pending_) == 1;
            if (batch is null && !logicDirty && !timerPending)
            {
                return;
            }

            PushLogicIfChanged();
        }
        finally
        {
            Volatile.Write(ref flushing_, 0);
        }
    }

    /// <summary>
    /// Evaluates every logic block and pushes a <c>logic</c> snapshot whenever the derived
    /// states changed. The signature covers states, reasons, should/actual and a running
    /// hold's whole seconds only — live value text follows the tag stream, so an ordinary
    /// value move does not re-push the logic state. Internal so the dedupe rule is
    /// unit-testable without a hub or timer.
    /// </summary>
    internal void PushLogicIfChanged()
    {
        LogicStateSnapshot snapshot = LogicStateRead.Snapshot(logic_store_, mapping_store_, bridge_state_, DateTime.UtcNow, logic_states_);
        Volatile.Write(ref timer_pending_, HasRunningTimer(snapshot) ? 1 : 0);

        string signature = LogicStateEvaluator.Signature(snapshot);
        if (string.Equals(signature, last_logic_signature_, StringComparison.Ordinal))
        {
            return;
        }

        last_logic_signature_ = signature;
        _ = hub_.Clients.All.SendAsync("logic", snapshot);
    }

    /// <summary>True while any timer is running and has not reached its preset.</summary>
    private static bool HasRunningTimer(LogicStateSnapshot snapshot)
    {
        foreach (LogicBlockStateDto block in snapshot.Blocks)
        {
            foreach (LogicElementStateDto element in block.Elements)
            {
                if (element.PtMs > 0 && element.State != LogicConditionStates.True && element.ElapsedMs > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
