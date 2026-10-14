using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Persists the plant logic blocks authored in the HMI's Logic page (<c>logic.json</c> in the
/// data directory) and the per-area tags they reference by name (<c>logic-tags.json</c>).
/// Mirrors <see cref="InterlinkStore"/>: one coarse lock, an in-memory version counter,
/// whole-file persistence and a validate-all-or-nothing save. Unlike the interlink store it
/// raises <see cref="Changed"/> so the HMI broadcaster re-evaluates and pushes the new state;
/// blocks and area tags share the one version, so a binding change makes every client refetch
/// its definitions.
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
    private readonly string area_tags_path_;
    private List<LogicBlockDto> blocks_;
    private List<LogicAreaTagsDto> area_tags_;
    private long version_;

    public LogicStore(IOptions<BridgeOptions> options)
    {
        _ = options;
        persist_path_ = DataDirectory.Combine("logic.json");
        area_tags_path_ = DataDirectory.Combine("logic-tags.json");
        // Area tags first: block normalization canonicalizes references against them.
        area_tags_ = LoadAreaTagsFromDisk() ?? new List<LogicAreaTagsDto>();
        blocks_ = LoadFromDisk() ?? new List<LogicBlockDto>();
    }

    /// <summary>Raised after a change, with the new version (outside the lock).</summary>
    public event Action<long>? Changed;

    public (IReadOnlyList<LogicBlockDto> Blocks, long Version) GetSnapshot()
    {
        lock (sync_)
        {
            return (blocks_.ToArray(), version_);
        }
    }

    /// <summary>The areas' tags, as stored (normalized).</summary>
    public IReadOnlyList<LogicAreaTagsDto> GetAreaTags()
    {
        lock (sync_)
        {
            return area_tags_.ToArray();
        }
    }

    /// <summary>How many blocks read an area's tags (the area name, else the stored name or group it falls back to).</summary>
    public int CountBlocksUsing(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string wanted = name.Trim();

        lock (sync_)
        {
            return blocks_.Count(block => UsesAreaTags(block, wanted));
        }
    }

    /// <summary>
    /// Insert or replace an area's tags by name (case-insensitive) — the Tags screen's Save.
    /// <paramref name="saved"/> is the normalized list as stored.
    /// </summary>
    public bool TrySaveAreaTags(
        LogicAreaTagsDto areaTags,
        out LogicAreaTagsDto saved,
        out long version,
        out string? error)
    {
        long newVersion;
        LogicAreaTagsDto stored;
        lock (sync_)
        {
            if (!TryNormalizeAreaTags(areaTags, out LogicAreaTagsDto normalized, out error))
            {
                saved = areaTags;
                version = version_;
                return false;
            }

            int index = area_tags_.FindIndex(existing =>
                string.Equals(existing.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                area_tags_.Add(normalized);
            }
            else
            {
                area_tags_[index] = normalized;
            }

            version_++;
            PersistAreaTags();
            newVersion = version_;
            stored = normalized;
            error = null;
        }

        Changed?.Invoke(newVersion);
        saved = stored;
        version = newVersion;
        return true;
    }

    /// <summary>
    /// Removes an area's tags. Refused while any block reads them — deleting them would silently
    /// unassign every tag those blocks read; the error names how many.
    /// </summary>
    public bool TryRemoveAreaTags(string name, out long version, out string? error)
    {
        ArgumentNullException.ThrowIfNull(name);
        long newVersion;
        lock (sync_)
        {
            string wanted = name.Trim();
            int index = area_tags_.FindIndex(existing =>
                string.Equals(existing.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                version = version_;
                error = "Area tags not found.";
                return false;
            }

            int used = blocks_.Count(block => UsesAreaTags(block, wanted));
            if (used > 0)
            {
                version = version_;
                error = used == 1
                    ? "1 block uses these area tags."
                    : $"{used} blocks use these area tags.";
                return false;
            }

            area_tags_.RemoveAt(index);
            version_++;
            PersistAreaTags();
            newVersion = version_;
            error = null;
        }

        Changed?.Invoke(newVersion);
        version = newVersion;
        return true;
    }

    /// <summary>A block reads an area's tags by its area, else by the stored name or group it falls back to.</summary>
    private static bool UsesAreaTags(LogicBlockDto block, string name)
    {
        string blockArea = LogicAreaTags.AreaNameFor(block);
        return blockArea.Length > 0
            && string.Equals(blockArea, name, StringComparison.OrdinalIgnoreCase);
    }

    public long SetAll(IEnumerable<LogicBlockDto> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        lock (sync_)
        {
            NormalizeAllOrThrow(blocks);
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

    private bool HasNameConflict(string name, Guid currentId)
    {
        return blocks_.Any(existing =>
            existing.Id != currentId &&
            string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void NormalizeAllOrThrow(IEnumerable<LogicBlockDto> blocks)
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

        blocks_ = normalized;
    }

    private bool TryNormalize(LogicBlockDto block, out LogicBlockDto normalized, out string? error)
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

        string blockArea = block.Area?.Trim() ?? string.Empty;

        // The area whose named tags this block reads: its area, else the name it stores, else
        // the group it is filed under (legacy). The store keeps the resolved name on the block
        // so the evaluator, the phone and older clients resolve it without knowing about areas.
        string resolvedName = LogicAreaTags.AreaNameFor(block);
        if (resolvedName.Length > LogicLimits.MaxAreaNameLength)
        {
            normalized = default!;
            error = $"An area can be at most {LogicLimits.MaxAreaNameLength} characters.";
            return false;
        }

        LogicAreaTagsDto? areaTags = LogicAreaTags.Find(area_tags_, resolvedName);

        string kind = block.Kind.ToLowerInvariant();
        bool isSequence = kind == LogicBlockKinds.Sequence;

        // A block carries its logic either as an IEC network or as the simple conditions form;
        // the two are alternatives, never both, so there is one source of truth to evaluate.
        List<LogicElementDto> elements = new();
        if (!TryNormalizeElements(block.Elements, areaTags, out elements, out error))
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
                if (!TryNormalizeCondition(condition, areaTags, out LogicConditionDto normalizedCondition, out error))
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
                if (!TryNormalizeStep(step, areaTags, out LogicStepDto normalizedStep, out error))
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
            if (!TryNormalizeAction(action, areaTags, out LogicActionDto normalizedAction, out error))
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
            Area = blockArea,
            TagSource = resolvedName,
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

    private static bool TryNormalizeStep(
        LogicStepDto step,
        LogicAreaTagsDto? areaTags,
        out LogicStepDto normalized,
        out string? error)
    {
        string name = step.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 64)
        {
            normalized = default!;
            error = "Step name is required (max 64 characters).";
            return false;
        }

        List<LogicElementDto> stepElements = new();
        if (!TryNormalizeElements(step.Elements, areaTags, out stepElements, out error))
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
            if (!TryNormalizeCondition(condition, areaTags, out LogicConditionDto normalizedCondition, out error))
            {
                normalized = default!;
                return false;
            }

            conditions.Add(normalizedCondition);
        }

        List<LogicActionDto> actions = new();
        foreach (LogicActionDto action in step.Actions)
        {
            if (!TryNormalizeAction(action, areaTags, out LogicActionDto normalizedAction, out error))
            {
                normalized = default!;
                return false;
            }

            actions.Add(normalizedAction);
        }

        // The handshake is either one of the area's named tags or a direct source/tag pair.
        string? completionTag = LogicAreaTags.CanonicalName(areaTags, step.CompletionTag);
        if (completionTag is { Length: 0 })
        {
            completionTag = null;
        }

        string? completionSourceId = null;
        string? completionItemId = null;
        if (completionTag is null)
        {
            completionSourceId = NormalizeOptional(step.CompletionSourceId);
            completionItemId = NormalizeOptional(step.CompletionItemId);
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
            CompletionTag = completionTag,
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
        LogicAreaTagsDto? areaTags,
        out List<LogicElementDto> normalized,
        out string? error)
    {
        normalized = new List<LogicElementDto>();
        int budget = LogicLimits.MaxElementsPerBlock;

        foreach (LogicElementDto element in elements ?? Array.Empty<LogicElementDto>())
        {
            if (!TryNormalizeElement(element, 0, ref budget, areaTags, out LogicElementDto child, out error))
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
        LogicAreaTagsDto? areaTags,
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
            string contactTag = relayContact
                ? string.Empty
                : LogicAreaTags.CanonicalName(areaTags, element.Tag);

            if (text.Length == 0 && !relayContact)
            {
                normalized = default!;
                error = "A contact needs its operator sentence.";
                return false;
            }

            // A contact reads one of three things: an internal relay, one of the area's named
            // tags (the pair lives on the entry), or a direct tag.
            string contactItemId = element.ItemId?.Trim() ?? string.Empty;
            if (!relayContact && contactTag.Length == 0 && contactItemId.Length == 0)
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

            bool named = contactTag.Length > 0;
            normalized = new LogicElementDto
            {
                Id = element.Id == Guid.Empty ? Guid.NewGuid() : element.Id,
                Kind = LogicElementKinds.Contact,
                Text = text,
                // A named tag's pair lives on the area's tags entry, never on the contact.
                SourceId = named ? string.Empty : NormalizeSourceId(element.SourceId),
                ItemId = named ? string.Empty : contactItemId,
                Tag = contactTag,
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
            if (!TryNormalizeElement(input, depth + 1, ref budget, areaTags, out LogicElementDto child, out error))
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

    private static bool TryNormalizeCondition(
        LogicConditionDto condition,
        LogicAreaTagsDto? areaTags,
        out LogicConditionDto normalized,
        out string? error)
    {
        string text = condition.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > 200)
        {
            normalized = default!;
            error = "Condition text is required (max 200 characters).";
            return false;
        }

        string tag = LogicAreaTags.CanonicalName(areaTags, condition.Tag);
        string itemId = condition.ItemId?.Trim() ?? string.Empty;
        if (tag.Length == 0 && itemId.Length == 0)
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
            // A named tag's pair lives on the area's tags entry, never on the condition.
            SourceId = tag.Length > 0 ? string.Empty : NormalizeSourceId(condition.SourceId),
            ItemId = tag.Length > 0 ? string.Empty : itemId,
            Tag = tag,
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

    private static bool TryNormalizeAction(
        LogicActionDto action,
        LogicAreaTagsDto? areaTags,
        out LogicActionDto normalized,
        out string? error)
    {
        string label = action.Label?.Trim() ?? string.Empty;
        if (label.Length == 0 || label.Length > 32)
        {
            normalized = default!;
            error = "Action label is required (max 32 characters).";
            return false;
        }

        string tag = LogicAreaTags.CanonicalName(areaTags, action.Tag);
        string itemId = action.ItemId?.Trim() ?? string.Empty;
        if (tag.Length == 0 && itemId.Length == 0)
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
            SourceId = tag.Length > 0 ? string.Empty : NormalizeSourceId(action.SourceId),
            ItemId = tag.Length > 0 ? string.Empty : itemId,
            Tag = tag,
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

    /// <summary>
    /// Normalizes an area's tags: the name is trimmed and required, entry names are trimmed,
    /// blanks dropped and case-insensitive duplicates collapsed (first spelling wins), and a
    /// binding is either a real source/item pair or blank — an item without a source lands on
    /// the default source, and a missing item leaves the entry created but unassigned, so its
    /// readers evaluate as unknown until the tag is assigned.
    /// </summary>
    private static bool TryNormalizeAreaTags(
        LogicAreaTagsDto areaTags,
        out LogicAreaTagsDto normalized,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(areaTags);

        string name = areaTags.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > LogicLimits.MaxAreaNameLength)
        {
            normalized = default!;
            error = $"An area name is required (max {LogicLimits.MaxAreaNameLength} characters).";
            return false;
        }

        List<LogicTagDto> tags = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicTagDto tag in areaTags.Tags)
        {
            string tagName = tag.Name?.Trim() ?? string.Empty;
            if (tagName.Length == 0 || !seen.Add(tagName))
            {
                continue;
            }

            if (tagName.Length > LogicLimits.MaxTagNameLength)
            {
                normalized = default!;
                error = $"A tag name can be at most {LogicLimits.MaxTagNameLength} characters.";
                return false;
            }

            if (tags.Count == LogicLimits.MaxTagsPerArea)
            {
                normalized = default!;
                error = $"An area can hold at most {LogicLimits.MaxTagsPerArea} tags.";
                return false;
            }

            string itemId = tag.ItemId?.Trim() ?? string.Empty;
            tags.Add(new LogicTagDto
            {
                Name = tagName,
                SourceId = itemId.Length == 0 ? string.Empty : NormalizeSourceId(tag.SourceId),
                ItemId = itemId
            });
        }

        normalized = new LogicAreaTagsDto
        {
            Name = name,
            Tags = tags
        };
        error = null;
        return true;
    }

    private void Persist()
    {
        try
        {
            string json = JsonSerializer.Serialize(blocks_, JsonOptions);
            File.WriteAllText(persist_path_, json);
        }
        catch
        {
        }
    }

    private void PersistAreaTags()
    {
        try
        {
            string json = JsonSerializer.Serialize(area_tags_, JsonOptions);
            File.WriteAllText(area_tags_path_, json);
        }
        catch
        {
        }
    }

    private List<LogicAreaTagsDto>? LoadAreaTagsFromDisk()
    {
        try
        {
            if (!File.Exists(area_tags_path_))
            {
                return null;
            }

            List<LogicAreaTagsDto>? loaded =
                JsonSerializer.Deserialize<List<LogicAreaTagsDto>>(File.ReadAllText(area_tags_path_));
            if (loaded is null)
            {
                return null;
            }

            // Drop lists that no longer validate so one bad hand-edit cannot take the whole
            // store down, then rewrite the file clean.
            List<LogicAreaTagsDto> sanitized = new();
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (LogicAreaTagsDto list in loaded)
            {
                if (TryNormalizeAreaTags(list, out LogicAreaTagsDto normalized, out _)
                    && names.Add(normalized.Name))
                {
                    sanitized.Add(normalized);
                }
            }

            if (sanitized.Count != loaded.Count)
            {
                try
                {
                    File.WriteAllText(area_tags_path_, JsonSerializer.Serialize(sanitized, JsonOptions));
                }
                catch
                {
                }
            }

            return sanitized;
        }
        catch
        {
            return null;
        }
    }

    private List<LogicBlockDto>? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_))
            {
                return null;
            }

            string json = File.ReadAllText(persist_path_);
            List<LogicBlockDto>? loaded = JsonSerializer.Deserialize<List<LogicBlockDto>>(json);
            if (loaded is null)
            {
                return null;
            }

            // Drop blocks that no longer validate so one bad hand-edit cannot take the
            // whole store down, then rewrite the file clean.
            List<LogicBlockDto> sanitized = new();
            foreach (LogicBlockDto block in loaded)
            {
                if (TryNormalize(block, out LogicBlockDto normalized, out _))
                {
                    sanitized.Add(normalized);
                }
            }

            if (sanitized.Count != loaded.Count)
            {
                try
                {
                    File.WriteAllText(persist_path_, JsonSerializer.Serialize(sanitized, JsonOptions));
                }
                catch
                {
                }
            }

            return sanitized;
        }
        catch
        {
            return null;
        }
    }
}
