using OpcBridge.Client;
using OpcBridge.Core;
using OpcBridge.Ua;

namespace OpcBridge.App;

/// <summary>
/// Writes a logic relay's value out to the tag it is bound to, through the same write path a
/// source poll or an operator write uses. Implemented by <see cref="BridgeWorker"/>, so the
/// publisher needs no dependency on the worker itself.
/// </summary>
public interface ILogicRelayWriteSink
{
    Task<bool> WriteRelayValueAsync(string sourceId, string itemId, object? value, CancellationToken ct);
}

/// <summary>
/// Publishes internal relays onto the tags they are bound to. Called with the coil's value
/// during an evaluation pass (in scan order), it keeps the bound tag's live value in step with
/// the M — the value cache, the OPC UA node, and for a device-backed tag the source itself,
/// written on change only. A tag the bridge holds (Mode Internal) and a tag that is read-only
/// stay local: the state is shown, nothing is sent anywhere.
/// </summary>
public sealed class LogicRelayPublisher
{
    private readonly MappingStore mapping_store_;
    private readonly BridgeState bridge_state_;
    private readonly UaServerHost? ua_server_;
    private readonly ILogicRelayWriteSink? write_sink_;
    private readonly object sync_ = new();
    private readonly Dictionary<string, bool> last_published_ = new(StringComparer.OrdinalIgnoreCase);

    public LogicRelayPublisher(
        MappingStore mappingStore,
        BridgeState bridgeState,
        UaServerHost? uaServer = null,
        ILogicRelayWriteSink? writeSink = null)
    {
        mapping_store_ = mappingStore;
        bridge_state_ = bridgeState;
        ua_server_ = uaServer;
        write_sink_ = writeSink;
    }

    /// <summary>
    /// Publishes one coil's value onto its relay's tag. Idempotent per logic value: only a
    /// change of the coil's own value is published, so a device bit that answers back something
    /// else cannot make the bridge write the same value pass after pass.
    /// </summary>
    public void Publish(LogicRelayDto relay, bool value)
    {
        if (!LogicRelays.IsBound(relay) || string.IsNullOrWhiteSpace(relay.Name))
        {
            return;
        }

        lock (sync_)
        {
            if (last_published_.TryGetValue(relay.Name, out bool previous) && previous == value)
            {
                return;
            }

            last_published_[relay.Name] = value;
        }

        (IReadOnlyList<TagMapping> mappings, _) = mapping_store_.GetSnapshot();
        TagMapping? mapping = FindMapping(mappings, relay.SourceId, relay.ItemId);
        if (mapping is null || !mapping.Enabled)
        {
            // No tag behind the binding (yet): nothing to show the state on.
            return;
        }

        BridgeValue published = new(
            relay.SourceId,
            relay.ItemId,
            TagDigital.ToTagValue(mapping.DataType, value),
            DateTime.UtcNow,
            192,
            true);
        bridge_state_.SetValue(published);
        ua_server_?.UpdateValue(published);
        WriteOut(mapping, published.Value);
    }

    /// <summary>Forgets the published values (a restart's first pass publishes everything again).</summary>
    public void Clear()
    {
        lock (sync_)
        {
            last_published_.Clear();
        }
    }

    private void WriteOut(TagMapping mapping, object? value)
    {
        if (write_sink_ is null || !mapping.Writeable)
        {
            return;
        }

        if (string.Equals(mapping.Mode, TagMode.Internal, StringComparison.OrdinalIgnoreCase))
        {
            // The bridge holds this tag's value; there is no device to send it to.
            return;
        }

        ILogicRelayWriteSink sink = write_sink_;
        _ = Task.Run(async () =>
        {
            try
            {
                await sink.WriteRelayValueAsync(mapping.SourceId, mapping.ItemId, value, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A failed write-out is the source's problem, reported by its own status; the
                // relay's state is still shown from the bridge.
            }
        });
    }

    private static TagMapping? FindMapping(
        IReadOnlyList<TagMapping> mappings,
        string sourceId,
        string itemId)
    {
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            if (string.Equals(mapping.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mapping.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
            {
                return mapping;
            }
        }

        return null;
    }
}
