namespace OpcBridge.Client;

/// <summary>
/// One evaluated pass over every logic block, keyed by definition ids. The mobile app loads
/// the definitions once (<c>GET /api/logic</c>) and joins them with this state; the bridge
/// pushes a new snapshot over the <c>logic</c> hub message whenever it changes.
/// </summary>
public sealed class LogicStateSnapshot
{
    /// <summary>Definition version this state was evaluated against, so clients can refetch definitions.</summary>
    public long Version { get; set; }

    /// <summary>Last value timestamp considered; blocks with no reference tags leave this as evaluated time.</summary>
    public DateTime EvaluatedUtc { get; set; }

    public List<LogicBlockStateDto> Blocks { get; set; } = new();
}

/// <summary>Evaluated state of one block.</summary>
public sealed class LogicBlockStateDto
{
    public Guid Id { get; set; }

    /// <summary>One of <see cref="LogicBlockStates"/>.</summary>
    public string State { get; set; } = LogicBlockStates.Unknown;

    /// <summary>Why the block is blocked / unknown: the first failing element's sentence, or a no-data note.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Every element of the block's network, depth-first in drawing order — contacts, gates and
    /// function blocks both, each with its own state. The simple conditions form appears here
    /// as its expansion, so a client only ever renders this list.
    /// </summary>
    public List<LogicElementStateDto> Elements { get; set; } = new();

    /// <summary>Per-step state for sequence blocks; empty for interlocks / permissives.</summary>
    public List<LogicStepStateDto> Steps { get; set; } = new();
}

/// <summary>
/// Evaluated state of one network element, joined to the definition by <see cref="Id"/>.
/// <see cref="State"/> is the element's output Q; for a leaf, <see cref="Should"/> and
/// <see cref="Matches"/> expose the reading it needs against the live one, which is what the
/// phone renders as "should 1 / actual 0".
/// </summary>
public sealed class LogicElementStateDto
{
    public Guid Id { get; set; }

    /// <summary>One of <see cref="LogicElementKinds"/>.</summary>
    public string Kind { get; set; } = LogicElementKinds.Contact;

    /// <summary>Depth in the network (0 = a root) — the phone indents by it.</summary>
    public int Depth { get; set; }

    /// <summary>One of <see cref="LogicConditionStates"/>: the element's output, true / false / unknown.</summary>
    public string State { get; set; } = LogicConditionStates.Unknown;

    /// <summary>
    /// What a contact must read for the interlock to pass: "1" for a normally open contact
    /// (<c>on</c>), "0" for a normally closed one (<c>off</c>), or the comparison ("&gt; 50").
    /// Empty for gates and function blocks.
    /// </summary>
    public string Should { get; set; } = string.Empty;

    /// <summary>
    /// True when the live value satisfies the contact's own requirement, false when it does not,
    /// null when unknown. Ignores a timer — the phone pairs this with <see cref="Should"/> to
    /// show "should 1 / actual 0" while <see cref="State"/> carries the timed verdict.
    /// </summary>
    public bool? Matches { get; set; }

    /// <summary>Live value rendered the way the plant reads it (on/off text, or number + unit).</summary>
    public string ValueText { get; set; } = "—";

    /// <summary>Timestamp of the value the state was derived from; null when no value has arrived.</summary>
    public DateTime? TimestampUtc { get; set; }

    /// <summary>Timer preset (PT) in milliseconds for TON / TOF / TP; 0 for every other kind.</summary>
    public int PtMs { get; set; }

    /// <summary>Milliseconds a timer has been running (its ET); 0 for every other kind.</summary>
    public int ElapsedMs { get; set; }

    /// <summary>Counter value (CV) for CTU / CTD; 0 for every other kind.</summary>
    public int Count { get; set; }
}

/// <summary>Evaluated state of one sequence step, joined to the definition by <see cref="Id"/>.</summary>
public sealed class LogicStepStateDto
{
    public Guid Id { get; set; }

    /// <summary>One of <see cref="LogicStepStates"/>.</summary>
    public string State { get; set; } = LogicStepStates.Pending;

    /// <summary>Why the step is not done (failing condition text, or a missing completion handshake).</summary>
    public string? Reason { get; set; }
}
