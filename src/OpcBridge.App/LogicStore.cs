using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Persists the plant logic blocks authored in the dashboard's Logic tab
/// (<c>logic.json</c> in the data directory): the blocks plus the relay/tag bindings of the
/// internal relays (M coils) they use. Mirrors <see cref="InterlinkStore"/>: one
/// coarse lock, an in-memory version counter, whole-file persistence and a
/// validate-all-or-nothing save. Unlike the interlink store it raises
/// <see cref="Changed"/> so the HMI broadcaster re-evaluates and pushes the new state.
/// </summary>
public sealed class LogicStore
{
    private const int MaxTagsPerBlock = 8;
    private const int MaxTagLength = 24;

    /// <summary>Longest interlock group name a block may carry (the phone's heading).</summary>
    private const int MaxBlockGroupLength = 40;

    /// <summary>Longest OR group label a condition may carry.</summary>
    private const int MaxOrGroupLength = 24;

    /// <summary>Longest hold timer a contact may carry (one hour).</summary>
    private const int MaxHoldMs = 3_600_000;

    /// <summary>Longest counter preset a CTU / CTD may carry.</summary>
    private const int MaxCounterPv = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private List<LogicBlockDto> blocks_;
    private List<LogicRelayDto> relays_;
    private long version_;

    public LogicStore(IOptions<BridgeOptions> options)
    {
        _ = options;
        persist_path_ = DataDirectory.Combine("logic.json");
        (blocks_, relays_) = LoadFromDisk();
    }

    /// <summary>Raised after a change, with the new version (outside the lock).</summary>
    public event Action<long>? Changed;

    public (IReadOnlyList<LogicBlockDto> Blocks, IReadOnlyList<LogicRelayDto> Relays, long Version) GetSnapshot()
    {
        lock (sync_)
        {
            return (blocks_.ToArray(), relays_.ToArray(), version_);
        }
    }

    public long SetAll(IEnumerable<LogicBlockDto> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        lock (sync_)
        {
            blocks_ = NormalizeAllOrThrow(blocks);
            version_++;
            Persist();
        }

        Changed?.Invoke(version_);
        return version_;
    }

    /// <summary>
    /// Insert or replace by id — the dashboard's Save for a new or edited block.
    /// <paramref name="saved"/> is the normalized block as stored (ids generated, text trimmed).
    /// </summary>
    public bool TrySave(LogicBlockDto block, out LogicBlockDto saved, out long version, out string? error)
    {
        long newVersion;
        LogicBlockDto stored;
        lock (sync_)
        {
            if (!TryNormalize(block, out LogicBlockDto normalized, out error))
            {
                saved = block;
                version = version_;
                return false;
            }

            if (HasNameConflict(normalized.Name, normalized.Id))
            {
                saved = block;
                version = version_;
                error = "Block name already exists.";
                return false;
            }

            int index = blocks_.FindIndex(existing => existing.Id == normalized.Id);
            if (index < 0)
            {
                blocks_.Add(normalized);
            }
            else
            {
                blocks_[index] = normalized;
            }

            version_++;
            Persist();
            newVersion = version_;
            stored = normalized;
            error = null;
        }

        Changed?.Invoke(newVersion);
        saved = stored;
        version = newVersion;
        return true;
    }

    public bool TryRemove(Guid id, out long version)
    {
        long newVersion;
        lock (sync_)
        {
            int removed = blocks_.RemoveAll(existing => existing.Id == id);
            if (removed == 0)
            {
                version = version_;
                return false;
            }

            version_++;
            Persist();
            newVersion = version_;
        }

        Changed?.Invoke(newVersion);
        version = newVersion;
        return true;
    }

    /// <summary>
    /// Replaces the whole relay/tag binding table — the Logic editor's Save for the relays its
    /// blocks use. A binding names the tag the relay's state is shown on; relays left unbound
    /// are not stored at all (their value is the bridge's memory only).
    /// </summary>
    public bool TrySetRelays(
        IEnumerable<LogicRelayDto> relays,
        out IReadOnlyList<LogicRelayDto> saved,
        out long version,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(relays);

        long newVersion;
        List<LogicRelayDto> normalized;
        lock (sync_)
        {
            if (!TryNormalizeRelays(relays, out normalized, out error))
            {
                saved = Array.Empty<LogicRelayDto>();
                version = version_;
                return false;
            }

            relays_ = normalized;
            version_++;
            Persist();
            newVersion = version_;
        }

        Changed?.Invoke(newVersion);
        saved = normalized.ToArray();
        version = newVersion;
        error = null;
        return true;
    }

    /// <summary>
    /// Normalizes the binding table: names canonicalized, unbound entries dropped (a relay
    /// without a tag has nothing to store), duplicates refused, the count bounded. Write is
    /// canonicalized; SourceId of a bound tag defaults to the bridge's default source.
    /// </summary>
    private static bool TryNormalizeRelays(
        IEnumerable<LogicRelayDto> relays,
        out List<LogicRelayDto> normalized,
        out string? error)
    {
        normalized = new List<LogicRelayDto>();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (LogicRelayDto relay in relays)
        {
            if (relay is null)
            {
                continue;
            }

            if (!TryNormalizeRelayBinding(relay, out LogicRelayDto? bound, out error))
            {
                return false;
            }

            if (bound is null)
            {
                continue;
            }

            if (!names.Add(bound.Name))
            {
                error = $"Relay {bound.Name} is listed twice.";
                return false;
            }

            if (normalized.Count == LogicLimits.MaxRelayBindings)
            {
                error = $"At most {LogicLimits.MaxRelayBindings} relay tags can be bound.";
                return false;
            }

            normalized.Add(bound);
        }

        error = null;
        return true;
    }

    /// <summary>
    /// One relay binding, or null for an unbound entry (nothing to store). The name must be a
    /// valid relay; a binding needs both halves of the tag pair, and a write policy is optional.
    /// </summary>
    private static bool TryNormalizeRelayBinding(
        LogicRelayDto relay,
        out LogicRelayDto? normalized,
        out string? error)
    {
        if (!TryNormalizeRelay(relay.Name, out string name, out error))
        {
            normalized = null;
            return false;
        }

        if (name.Length == 0)
        {
            normalized = null;
            error = "A relay binding needs a relay name (e.g. M01).";
            return false;
        }

        string itemId = relay.ItemId?.Trim() ?? string.Empty;
        string sourceId = relay.SourceId?.Trim() ?? string.Empty;
        if (itemId.Length == 0)
        {
            // Not bound to a tag: the relay is the bridge's own memory, nothing to store.
            normalized = null;
            error = null;
            return true;
        }

        if (itemId.Length > 256)
        {
            normalized = null;
            error = "A relay tag name is at most 256 characters.";
            return false;
        }

        normalized = new LogicRelayDto
        {
            Name = name,
            SourceId = sourceId.Length == 0 ? DaRuntimeSettings.DefaultSourceId : sourceId,
            ItemId = itemId,
            Write = LogicRelayWrites.Normalize(relay.Write)
        };
        error = null;
        return true;
    }

    private bool HasNameConflict(string name, Guid currentId)
    {
        return blocks_.Any(existing =>
            existing.Id != currentId &&
            string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static List<LogicBlockDto> NormalizeAllOrThrow(IEnumerable<LogicBlockDto> blocks)
    {
        List<LogicBlockDto> normalized = new();
        HashSet<Guid> ids = new();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (LogicBlockDto block in blocks)
        {
            if (!TryNormalize(block, out LogicBlockDto normalizedBlock, out string? error))
            {
                throw new InvalidOperationException(error);
            }

            if (!ids.Add(normalizedBlock.Id))
            {
                throw new InvalidOperationException("Block already exists.");
            }

            if (!names.Add(normalizedBlock.Name))
            {
                throw new InvalidOperationException("Block name already exists.");
            }

            normalized.Add(normalizedBlock);
        }

        return normalized;
    }

    private static bool TryNormalize(LogicBlockDto block, out LogicBlockDto normalized, out string? error)
    {
        ArgumentNullException.ThrowIfNull(block);

        string name = block.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 64)
        {
            normalized = default!;
            error = "Block name is required (max 64 characters).";
            return false;
        }

        if (!LogicBlockKinds.IsValid(block.Kind))
        {
            normalized = default!;
            error = "Block kind must be interlock, permissive or sequence.";
            return false;
        }

        if (!TryNormalizeTags(block.Tags, out List<string> tags, out error))
        {
            normalized = default!;
            return false;
        }

        string blockGroup = block.Group?.Trim() ?? string.Empty;
        if (blockGroup.Length > MaxBlockGroupLength)
        {
            normalized = default!;
            error = "An interlock group can be at most 40 characters.";
            return false;
        }

        string kind = block.Kind.ToLowerInvariant();
        bool isSequence = kind == LogicBlockKinds.Sequence;

        // A block carries its logic either as an IEC network or as the simple conditions form;
        // the two are alternatives, never both, so there is one source of truth to evaluate.
        List<LogicElementDto> elements = new();
        if (!TryNormalizeElements(block.Elements, out elements, out error))
        {
            normalized = default!;
            return false;
        }

        List<LogicConditionDto> conditions = new();
        if (!isSequence)
        {
            if (elements.Count > 0 && block.Conditions.Count > 0)
            {
                normalized = default!;
                error = "Use either conditions or an IEC network, not both.";
                return false;
            }

            if (elements.Count == 0 && block.Conditions.Count == 0)
            {
                normalized = default!;
                error = "A block needs at least one condition or an IEC network.";
                return false;
            }

            foreach (LogicConditionDto condition in block.Conditions)
            {
                if (!TryNormalizeCondition(condition, out LogicConditionDto normalizedCondition, out error))
                {
                    normalized = default!;
                    return false;
                }

                conditions.Add(normalizedCondition);
            }
        }
        else if (elements.Count > 0 || block.Conditions.Count > 0)
        {
            normalized = default!;
            error = "A sequence carries its logic in its steps.";
            return false;
        }

        List<LogicStepDto> steps = new();
        if (isSequence)
        {
            if (block.Steps.Count == 0)
            {
                normalized = default!;
                error = "A sequence needs at least one step.";
                return false;
            }

            HashSet<string> stepNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (LogicStepDto step in block.Steps)
            {
                if (!TryNormalizeStep(step, out LogicStepDto normalizedStep, out error))
                {
                    normalized = default!;
                    return false;
                }

                if (!stepNames.Add(normalizedStep.Name))
                {
                    normalized = default!;
                    error = "Step names must be unique within a sequence.";
                    return false;
                }

                steps.Add(normalizedStep);
            }
        }

        List<LogicActionDto> actions = new();
        foreach (LogicActionDto action in block.Actions)
        {
            if (!TryNormalizeAction(action, out LogicActionDto normalizedAction, out error))
            {
                normalized = default!;
                return false;
            }

            actions.Add(normalizedAction);
        }

        normalized = new LogicBlockDto
        {
            Id = block.Id == Guid.Empty ? Guid.NewGuid() : block.Id,
            Name = name,
            Group = blockGroup,
            Description = block.Description?.Trim() ?? string.Empty,
            Kind = kind,
            Enabled = block.Enabled,
            Order = block.Order,
            Tags = tags,
            Elements = elements,
            Conditions = conditions,
            Steps = steps,
            Actions = actions
        };
        error = null;
        return true;
    }

    private static bool TryNormalizeStep(LogicStepDto step, out LogicStepDto normalized, out string? error)
    {
        string name = step.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 64)
        {
            normalized = default!;
            error = "Step name is required (max 64 characters).";
            return false;
        }

        List<LogicElementDto> stepElements = new();
        if (!TryNormalizeElements(step.Elements, out stepElements, out error))
        {
            normalized = default!;
            return false;
        }

        if (stepElements.Count > 0 && step.Conditions.Count > 0)
        {
            normalized = default!;
            error = "Use either conditions or an IEC network, not both.";
            return false;
        }

        List<LogicConditionDto> conditions = new();
        foreach (LogicConditionDto condition in step.Conditions)
        {
            if (!TryNormalizeCondition(condition, out LogicConditionDto normalizedCondition, out error))
            {
                normalized = default!;
                return false;
            }

            conditions.Add(normalizedCondition);
        }

        List<LogicActionDto> actions = new();
        foreach (LogicActionDto action in step.Actions)
        {
            if (!TryNormalizeAction(action, out LogicActionDto normalizedAction, out error))
            {
                normalized = default!;
                return false;
            }

            actions.Add(normalizedAction);
        }

        string? completionSourceId = NormalizeOptional(step.CompletionSourceId);
        string? completionItemId = NormalizeOptional(step.CompletionItemId);
        if ((completionSourceId is null) != (completionItemId is null))
        {
            normalized = default!;
            error = "A completion handshake needs both a source and a tag.";
            return false;
        }

        if (completionSourceId is not null)
        {
            completionSourceId = NormalizeSourceId(completionSourceId);
        }

        normalized = new LogicStepDto
        {
            Id = step.Id == Guid.Empty ? Guid.NewGuid() : step.Id,
            Name = name,
            Elements = stepElements,
            Conditions = conditions,
            NextStepText = NormalizeOptional(step.NextStepText),
            CompletionSourceId = completionSourceId,
            CompletionItemId = completionItemId,
            Actions = actions
        };
        error = null;
        return true;
    }

    /// <summary>
    /// Normalizes a network: element kinds, arities and constants checked, ids generated, the
    /// contact fields cleared on gates and blocks (and the presets cleared where they do not
    /// apply), depth and element count bounded so a hand-written file cannot build an unbounded
    /// tree.
    /// </summary>
    private static bool TryNormalizeElements(
        IReadOnlyList<LogicElementDto>? elements,
        out List<LogicElementDto> normalized,
        out string? error)
    {
        normalized = new List<LogicElementDto>();
        int budget = LogicLimits.MaxElementsPerBlock;

        foreach (LogicElementDto element in elements ?? Array.Empty<LogicElementDto>())
        {
            if (!TryNormalizeElement(element, 0, ref budget, out LogicElementDto child, out error))
            {
                return false;
            }

            normalized.Add(child);
        }

        error = null;
        return true;
    }

    private static bool TryNormalizeElement(
        LogicElementDto element,
        int depth,
        ref int budget,
        out LogicElementDto normalized,
        out string? error)
    {
        if (depth > LogicLimits.MaxElementDepth)
        {
            normalized = default!;
            error = $"A network can nest at most {LogicLimits.MaxElementDepth} levels.";
            return false;
        }

        if (budget-- <= 0)
        {
            normalized = default!;
            error = $"A block can hold at most {LogicLimits.MaxElementsPerBlock} elements.";
            return false;
        }

        if (!LogicElementKinds.IsValid(element.Kind))
        {
            normalized = default!;
            error = "Unknown element kind.";
            return false;
        }

        string kind = element.Kind.ToLowerInvariant();
        string text = element.Text?.Trim() ?? string.Empty;
        if (text.Length > 200)
        {
            normalized = default!;
            error = "Element text is at most 200 characters.";
            return false;
        }

        if (!LogicConditionSeverities.IsValid(element.Severity))
        {
            normalized = default!;
            error = "Element severity must be block or warn.";
            return false;
        }

        // A relay is read by a contact and driven by a coil; every other kind must not carry one
        // (a hand-edited file with a relay on a gate has it cleared, like any other stray field).
        string relay = string.Empty;
        if (kind == LogicElementKinds.Contact || LogicElementKinds.IsCoil(kind))
        {
            if (!TryNormalizeRelay(element.Relay, out relay, out error))
            {
                normalized = default!;
                return false;
            }
        }

        if (kind == LogicElementKinds.Contact)
        {
            bool relayContact = relay.Length > 0;

            if (text.Length == 0 && !relayContact)
            {
                normalized = default!;
                error = "A contact needs its operator sentence.";
                return false;
            }

            string contactItemId = element.ItemId?.Trim() ?? string.Empty;
            if (!relayContact && contactItemId.Length == 0)
            {
                normalized = default!;
                error = "A contact needs a tag.";
                return false;
            }

            if (!LogicConditionOps.IsValid(element.Op))
            {
                normalized = default!;
                error = "A contact must be on, off, gt, lt or eq.";
                return false;
            }

            if (element.Inputs.Count > 0)
            {
                normalized = default!;
                error = "A contact reads one tag and takes no inputs.";
                return false;
            }

            string contactOp = element.Op.ToLowerInvariant();
            if (relayContact)
            {
                if (LogicConditionOps.RequiresValue(contactOp))
                {
                    normalized = default!;
                    error = "A relay contact must be on or off.";
                    return false;
                }

                normalized = new LogicElementDto
                {
                    Id = element.Id == Guid.Empty ? Guid.NewGuid() : element.Id,
                    Kind = LogicElementKinds.Contact,
                    Text = text,
                    Relay = relay,
                    Op = contactOp,
                    Severity = element.Severity.ToLowerInvariant(),
                    NextStepText = NormalizeOptional(element.NextStepText)
                };
                error = null;
                return true;
            }

            normalized = new LogicElementDto
            {
                Id = element.Id == Guid.Empty ? Guid.NewGuid() : element.Id,
                Kind = LogicElementKinds.Contact,
                Text = text,
                SourceId = NormalizeSourceId(element.SourceId),
                ItemId = contactItemId,
                Op = contactOp,
                Value = LogicConditionOps.RequiresValue(contactOp) ? element.Value : null,
                Severity = element.Severity.ToLowerInvariant(),
                NextStepText = NormalizeOptional(element.NextStepText)
            };
            error = null;
            return true;
        }

        if (LogicElementKinds.IsCoil(kind) && relay.Length == 0)
        {
            normalized = default!;
            error = "A coil needs a relay (e.g. M01).";
            return false;
        }

        (int minInputs, int maxInputs) = LogicElementKinds.InputRange(kind);
        if (element.Inputs.Count < minInputs || element.Inputs.Count > maxInputs)
        {
            normalized = default!;
            error = minInputs == maxInputs
                ? $"{KindName(kind)} takes exactly {minInputs} input(s)."
                : $"{KindName(kind)} takes between {minInputs} and {maxInputs} inputs.";
            return false;
        }

        if (element.PtMs is < 0 or > MaxHoldMs)
        {
            normalized = default!;
            error = "A timer preset must be between 0 and 3,600,000 ms.";
            return false;
        }

        if (element.Pv is < 0 or > MaxCounterPv)
        {
            normalized = default!;
            error = "A counter preset must be between 0 and 1,000,000.";
            return false;
        }

        List<LogicElementDto> inputs = new();
        foreach (LogicElementDto input in element.Inputs)
        {
            if (!TryNormalizeElement(input, depth + 1, ref budget, out LogicElementDto child, out error))
            {
                normalized = default!;
                return false;
            }

            inputs.Add(child);
        }

        normalized = new LogicElementDto
        {
            Id = element.Id == Guid.Empty ? Guid.NewGuid() : element.Id,
            Kind = kind,
            Text = text,
            // A gate or block combines its inputs; only a contact reads a tag, only a coil drives
            // a relay, and only a timer or counter carries a preset. A coil always blocks: it is
            // an output, so "warn only" has no meaning for it.
            Relay = relay,
            PtMs = LogicElementKinds.IsTimer(kind) ? element.PtMs : 0,
            Pv = LogicElementKinds.IsCounter(kind) ? element.Pv : 0,
            Inputs = inputs,
            Severity = LogicElementKinds.IsCoil(kind)
                ? LogicConditionSeverities.Block
                : element.Severity.ToLowerInvariant(),
            NextStepText = NormalizeOptional(element.NextStepText)
        };
        error = null;
        return true;
    }

    private static string KindName(string kind) => kind switch
    {
        LogicElementKinds.Coil => "COIL",
        LogicElementKinds.And => "AND",
        LogicElementKinds.Or => "OR",
        LogicElementKinds.Xor => "XOR",
        LogicElementKinds.Not => "NOT",
        LogicElementKinds.Ton => "TON",
        LogicElementKinds.Tof => "TOF",
        LogicElementKinds.Tp => "TP",
        LogicElementKinds.Ctu => "CTU",
        LogicElementKinds.Ctd => "CTD",
        LogicElementKinds.Sr => "SR",
        LogicElementKinds.Rs => "RS",
        LogicElementKinds.RisingEdge => "R_TRIG",
        LogicElementKinds.FallingEdge => "F_TRIG",
        _ => "The element"
    };

    private static bool TryNormalizeCondition(LogicConditionDto condition, out LogicConditionDto normalized, out string? error)
    {
        string text = condition.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > 200)
        {
            normalized = default!;
            error = "Condition text is required (max 200 characters).";
            return false;
        }

        string itemId = condition.ItemId?.Trim() ?? string.Empty;
        if (itemId.Length == 0)
        {
            normalized = default!;
            error = "Condition tag is required.";
            return false;
        }

        if (!LogicConditionOps.IsValid(condition.Op))
        {
            normalized = default!;
            error = "Condition operator must be on, off, gt, lt or eq.";
            return false;
        }

        string op = condition.Op.ToLowerInvariant();
        if (LogicConditionOps.RequiresValue(op) && condition.Value is null)
        {
            normalized = default!;
            error = "A numeric comparison needs a value.";
            return false;
        }

        if (!LogicConditionSeverities.IsValid(condition.Severity))
        {
            normalized = default!;
            error = "Condition severity must be block or warn.";
            return false;
        }

        if (condition.HoldMs is < 0 or > MaxHoldMs)
        {
            normalized = default!;
            error = "A hold timer must be between 0 and 3,600,000 ms.";
            return false;
        }

        string group = condition.Group?.Trim() ?? string.Empty;
        if (group.Length > MaxOrGroupLength)
        {
            normalized = default!;
            error = "An OR group can be at most 24 characters.";
            return false;
        }

        normalized = new LogicConditionDto
        {
            Id = condition.Id == Guid.Empty ? Guid.NewGuid() : condition.Id,
            Text = text,
            SourceId = NormalizeSourceId(condition.SourceId),
            ItemId = itemId,
            Op = op,
            Value = LogicConditionOps.RequiresValue(op) ? condition.Value : null,
            Group = group,
            HoldMs = condition.HoldMs,
            NextStepText = NormalizeOptional(condition.NextStepText),
            Severity = condition.Severity.ToLowerInvariant()
        };
        error = null;
        return true;
    }

    private static bool TryNormalizeAction(LogicActionDto action, out LogicActionDto normalized, out string? error)
    {
        string label = action.Label?.Trim() ?? string.Empty;
        if (label.Length == 0 || label.Length > 32)
        {
            normalized = default!;
            error = "Action label is required (max 32 characters).";
            return false;
        }

        string itemId = action.ItemId?.Trim() ?? string.Empty;
        if (itemId.Length == 0)
        {
            normalized = default!;
            error = "Action tag is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(action.Value))
        {
            normalized = default!;
            error = "Action value is required.";
            return false;
        }

        normalized = new LogicActionDto
        {
            Label = label,
            SourceId = NormalizeSourceId(action.SourceId),
            ItemId = itemId,
            Value = action.Value.Trim(),
            Confirm = action.Confirm
        };
        error = null;
        return true;
    }

    /// <summary>
    /// Trims the block's tags, drops blanks and case-insensitive duplicates (first spelling
    /// wins) and bounds the set: at most <see cref="MaxTagsPerBlock"/> tags of at most
    /// <see cref="MaxTagLength"/> characters each — a longer list is a save error, not a
    /// silent truncation.
    /// </summary>
    private static bool TryNormalizeTags(IReadOnlyList<string>? tags, out List<string> normalized, out string? error)
    {
        normalized = new List<string>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string? raw in tags ?? Array.Empty<string>())
        {
            string tag = raw?.Trim() ?? string.Empty;
            if (tag.Length == 0 || !seen.Add(tag))
            {
                continue;
            }

            if (tag.Length > MaxTagLength)
            {
                error = "A tag can be at most 24 characters.";
                return false;
            }

            if (normalized.Count == MaxTagsPerBlock)
            {
                error = "A block can carry at most 8 tags.";
                return false;
            }

            normalized.Add(tag);
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Canonical relay name: trimmed and uppercased, so M01 and m01 are one relay. Blank is a
    /// valid "no relay" for a tag contact; anything else must start with a letter and hold only
    /// letters, digits and underscores, at most <see cref="LogicLimits.MaxRelayNameLength"/>
    /// characters.
    /// </summary>
    private static bool TryNormalizeRelay(string? text, out string relay, out string? error)
    {
        relay = text?.Trim().ToUpperInvariant() ?? string.Empty;
        if (relay.Length == 0)
        {
            error = null;
            return true;
        }

        bool valid = relay.Length <= LogicLimits.MaxRelayNameLength && char.IsAsciiLetter(relay[0]);
        for (int i = 1; i < relay.Length && valid; i++)
        {
            valid = char.IsAsciiLetterOrDigit(relay[i]) || relay[i] == '_';
        }

        if (!valid)
        {
            error = $"A relay name must start with a letter (max {LogicLimits.MaxRelayNameLength} characters).";
            return false;
        }

        error = null;
        return true;
    }

    private static string? NormalizeOptional(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string NormalizeSourceId(string? sourceId)
    {
        string value = sourceId?.Trim() ?? string.Empty;
        return value.Length == 0 ? DaRuntimeSettings.DefaultSourceId : value;
    }

    private void Persist()
    {
        try
        {
            string json = JsonSerializer.Serialize(new LogicFileDto { Relays = relays_, Blocks = blocks_ }, JsonOptions);
            File.WriteAllText(persist_path_, json);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Reads <c>logic.json</c>. The current shape is an object holding the relay bindings and the
    /// blocks; a bare array is the pre-relay shape and loads as blocks only, so an older file is
    /// never lost. Blocks (and bindings) that no longer validate are dropped and the file is
    /// rewritten clean, so one bad hand-edit cannot take the whole store down.
    /// </summary>
    private (List<LogicBlockDto> Blocks, List<LogicRelayDto> Relays) LoadFromDisk()
    {
        List<LogicBlockDto> noBlocks = new();
        try
        {
            if (!File.Exists(persist_path_))
            {
                return (noBlocks, new List<LogicRelayDto>());
            }

            string json = File.ReadAllText(persist_path_);
            List<LogicBlockDto>? loadedBlocks;
            List<LogicRelayDto>? loadedRelays;
            if (json.TrimStart().StartsWith('['))
            {
                loadedBlocks = JsonSerializer.Deserialize<List<LogicBlockDto>>(json);
                loadedRelays = new List<LogicRelayDto>();
            }
            else
            {
                LogicFileDto? file = JsonSerializer.Deserialize<LogicFileDto>(json);
                loadedBlocks = file?.Blocks;
                loadedRelays = file?.Relays;
            }

            if (loadedBlocks is null)
            {
                return (noBlocks, new List<LogicRelayDto>());
            }

            List<LogicBlockDto> blocks = new();
            foreach (LogicBlockDto block in loadedBlocks)
            {
                if (TryNormalize(block, out LogicBlockDto normalized, out _))
                {
                    blocks.Add(normalized);
                }
            }

            List<LogicRelayDto> relays = new();
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (LogicRelayDto relay in loadedRelays ?? new List<LogicRelayDto>())
            {
                if (relay is not null
                    && TryNormalizeRelayBinding(relay, out LogicRelayDto? bound, out _)
                    && bound is not null
                    && names.Add(bound.Name))
                {
                    relays.Add(bound);
                }
            }

            if (blocks.Count != loadedBlocks.Count || relays.Count != (loadedRelays?.Count ?? 0))
            {
                File.WriteAllText(persist_path_, JsonSerializer.Serialize(new LogicFileDto { Relays = relays, Blocks = blocks }, JsonOptions));
            }

            return (blocks, relays);
        }
        catch
        {
            return (noBlocks, new List<LogicRelayDto>());
        }
    }

    /// <summary>The persisted shape of <c>logic.json</c>.</summary>
    private sealed class LogicFileDto
    {
        public List<LogicRelayDto> Relays { get; set; } = new();

        public List<LogicBlockDto> Blocks { get; set; } = new();
    }
}
