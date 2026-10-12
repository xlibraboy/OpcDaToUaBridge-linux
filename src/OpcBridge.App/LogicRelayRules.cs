using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// The relay/tag binding rules at runtime: which tag a relay's state is shown on, which relays
/// the logic actually drives, and whether a write to a relay's tag is allowed. Built from the
/// logic store's blocks and bindings on demand — a write or a tag read is not a hot path, so
/// there is no cache to invalidate when the logic changes.
///
/// An M a coil drives is the logic's own output: its tag is read-only unless the binding says
/// otherwise. A free M (no coil anywhere) is the operator's: the tag may be written and the
/// logic reads that value.
/// </summary>
public sealed class LogicRelayRules
{
    private static readonly LogicRelayRules Empty = new(
        new Dictionary<string, LogicRelayDto>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, LogicRelayDto> by_tag_;
    private readonly HashSet<string> driven_;

    private LogicRelayRules(
        Dictionary<string, LogicRelayDto> byTag,
        HashSet<string> driven)
    {
        by_tag_ = byTag;
        driven_ = driven;
    }

    /// <summary>True while any relay is bound to a tag — the fast "is this feature in use" check.</summary>
    public bool HasBindings => by_tag_.Count > 0;

    public static LogicRelayRules Build(
        IEnumerable<LogicRelayDto>? relays,
        IEnumerable<LogicBlockDto>? blocks)
    {
        Dictionary<string, LogicRelayDto> byTag = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicRelayDto relay in relays ?? Array.Empty<LogicRelayDto>())
        {
            if (string.IsNullOrWhiteSpace(relay.Name) || !LogicRelays.IsBound(relay))
            {
                continue;
            }

            byTag[LogicRelays.TagKey(relay.SourceId, relay.ItemId)] = relay;
        }

        HashSet<string> driven = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicBlockDto block in blocks ?? Array.Empty<LogicBlockDto>())
        {
            // A disabled block's coils never drive (the evaluator says the same), so its relays
            // stay free — disabling an interlock hands its M back to the operator.
            if (!block.Enabled)
            {
                continue;
            }

            foreach ((LogicElementDto element, int _) in LogicNetwork.Walk(block.Elements))
            {
                if (LogicElementKinds.IsCoil(element.Kind) && !string.IsNullOrWhiteSpace(element.Relay))
                {
                    driven.Add(element.Relay.Trim());
                }
            }

            foreach (LogicStepDto step in block.Steps)
            {
                foreach ((LogicElementDto element, int _) in LogicNetwork.Walk(step.Elements))
                {
                    if (LogicElementKinds.IsCoil(element.Kind) && !string.IsNullOrWhiteSpace(element.Relay))
                    {
                        driven.Add(element.Relay.Trim());
                    }
                }
            }
        }

        return new LogicRelayRules(byTag, driven);
    }

    public static LogicRelayRules None => Empty;

    /// <summary>The binding of the tag, when that tag is a relay's I/O point.</summary>
    public bool TryGetByTag(string? sourceId, string? itemId, out LogicRelayDto relay) =>
        by_tag_.TryGetValue(LogicRelays.TagKey(sourceId, itemId), out relay!);

    /// <summary>True while an enabled block's coil drives this relay.</summary>
    public bool IsDriven(string? name) => driven_.Contains(name?.Trim() ?? string.Empty);

    /// <summary>
    /// Whether the tag this relay is bound to may be written: the binding's own policy wins,
    /// otherwise a driven M is read-only and a free one is writable.
    /// </summary>
    public bool IsWritable(LogicRelayDto relay)
    {
        if (string.Equals(relay.Write, LogicRelayWrites.Allow, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(relay.Write, LogicRelayWrites.Never, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsDriven(relay.Name);
    }
}
