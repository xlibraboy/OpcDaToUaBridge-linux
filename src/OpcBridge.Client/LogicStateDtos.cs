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

    /// <summary>Why the block is blocked / unknown: the first failing condition's text, or a no-data note.</summary>
    public string? Reason { get; set; }

    /// <summary>State of every condition in the block, including each step's conditions.</summary>
    public List<LogicConditionStateDto> Conditions { get; set; } = new();

    /// <summary>Per-step state for sequence blocks; empty for interlocks / permissives.</summary>
    public List<LogicStepStateDto> Steps { get; set; } = new();
}

/// <summary>Evaluated state of one condition, joined to the definition by <see cref="Id"/>.</summary>
public sealed class LogicConditionStateDto
{
    public Guid Id { get; set; }

    /// <summary>One of <see cref="LogicConditionStates"/>.</summary>
    public string State { get; set; } = LogicConditionStates.Unknown;

    /// <summary>Live value rendered the way the plant reads it (on/off text, or number + unit).</summary>
    public string ValueText { get; set; } = "—";

    /// <summary>Timestamp of the value the state was derived from; null when no value has arrived.</summary>
    public DateTime? TimestampUtc { get; set; }
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
