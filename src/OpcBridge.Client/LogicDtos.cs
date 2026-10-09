namespace OpcBridge.Client;

/// <summary>
/// A block of plant logic the operator can read at a glance: an interlock or permissive
/// (a flat list of conditions, all of which must be true) or a sequence (ordered steps,
/// each gated by its own conditions). Authored in the dashboard's Logic tab, served to the
/// mobile app together with the evaluated state.
/// </summary>
public sealed class LogicBlockDto
{
    public Guid Id { get; set; }

    /// <summary>Operator-facing block name (e.g. "Line 01 Start").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional operator note rendered under the name.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>One of <see cref="LogicBlockKinds"/>.</summary>
    public string Kind { get; set; } = LogicBlockKinds.Interlock;

    public bool Enabled { get; set; } = true;

    /// <summary>Display order among blocks (ascending).</summary>
    public int Order { get; set; }

    /// <summary>
    /// Free-form labels that group blocks (e.g. "Line 1", "Safety"), shown as chips on the
    /// phone. Up to 8, each max 24 characters; the store trims and deduplicates them.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Conditions for interlock / permissive blocks. Empty for sequences.</summary>
    public List<LogicConditionDto> Conditions { get; set; } = new();

    /// <summary>Ordered steps for sequence blocks. Empty for interlocks / permissives.</summary>
    public List<LogicStepDto> Steps { get; set; } = new();

    /// <summary>Block-level action buttons shown on the phone.</summary>
    public List<LogicActionDto> Actions { get; set; } = new();
}

/// <summary>
/// One condition of a block or step: a single tag comparison with the operator sentence
/// shown on the phone and the hint for what to do when it is not true.
/// </summary>
public sealed class LogicConditionDto
{
    public Guid Id { get; set; }

    /// <summary>Operator sentence (e.g. "Line 01 start permit must be given").</summary>
    public string Text { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;

    /// <summary>One of <see cref="LogicConditionOps"/>.</summary>
    public string Op { get; set; } = LogicConditionOps.On;

    /// <summary>Comparison value; required for gt / lt / eq, ignored for on / off.</summary>
    public double? Value { get; set; }

    /// <summary>"What to do next" text shown while the condition is not true.</summary>
    public string? NextStepText { get; set; }

    /// <summary>One of <see cref="LogicConditionSeverities"/> — warn conditions never block.</summary>
    public string Severity { get; set; } = LogicConditionSeverities.Block;
}

/// <summary>One ordered step of a sequence block.</summary>
public sealed class LogicStepDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<LogicConditionDto> Conditions { get; set; } = new();

    /// <summary>"What to do next" text shown while this is the current step.</summary>
    public string? NextStepText { get; set; }

    /// <summary>
    /// Optional handshake tag: when set, the step counts as done only once this tag is true
    /// (in addition to its conditions). Null = done as soon as its conditions are true.
    /// </summary>
    public string? CompletionSourceId { get; set; }

    public string? CompletionItemId { get; set; }

    public List<LogicActionDto> Actions { get; set; } = new();
}

/// <summary>A button on the phone that writes one tag value through the existing write path.</summary>
public sealed class LogicActionDto
{
    public string Label { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Literal to write (parsed by the bridge against the target tag's data type).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Ask the operator to confirm before writing.</summary>
    public bool Confirm { get; set; } = true;
}

/// <summary>An operator note (or acknowledgement) attached to a block, optionally to one condition.</summary>
public sealed class LogicNoteDto
{
    public Guid Id { get; set; }
    public Guid BlockId { get; set; }
    public Guid? ConditionId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Request body for <c>POST /api/logic/notes</c>.</summary>
public sealed class LogicNoteAddRequest
{
    public Guid BlockId { get; set; }
    public Guid? ConditionId { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>Block kinds authored in the dashboard and rendered by the mobile app.</summary>
public static class LogicBlockKinds
{
    public const string Interlock = "interlock";
    public const string Permissive = "permissive";
    public const string Sequence = "sequence";

    public static readonly string[] All = { Interlock, Permissive, Sequence };

    public static bool IsValid(string? kind) =>
        kind is not null && Array.Exists(All, k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Condition comparison operators. on/off use the bridge's digital coercion.</summary>
public static class LogicConditionOps
{
    public const string On = "on";
    public const string Off = "off";
    public const string GreaterThan = "gt";
    public const string LessThan = "lt";
    public const string Equal = "eq";

    public static readonly string[] All = { On, Off, GreaterThan, LessThan, Equal };

    public static bool IsValid(string? op) =>
        op is not null && Array.Exists(All, o => string.Equals(o, op, StringComparison.OrdinalIgnoreCase));

    /// <summary>Numeric operators need a comparison value; on/off do not.</summary>
    public static bool RequiresValue(string? op) =>
        string.Equals(op, GreaterThan, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(op, LessThan, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(op, Equal, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Condition severities: a false block condition blocks the block, warn only flags.</summary>
public static class LogicConditionSeverities
{
    public const string Block = "block";
    public const string Warn = "warn";

    public static readonly string[] All = { Block, Warn };

    public static bool IsValid(string? severity) =>
        severity is not null && Array.Exists(All, s => string.Equals(s, severity, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Evaluated block states, as rendered on the phone and dashboard.</summary>
public static class LogicBlockStates
{
    public const string Ready = "ready";
    public const string Blocked = "blocked";
    public const string Unknown = "unknown";
    public const string Disabled = "disabled";
}

/// <summary>Evaluated condition states.</summary>
public static class LogicConditionStates
{
    public const string True = "true";
    public const string False = "false";
    public const string Unknown = "unknown";
}

/// <summary>Evaluated sequence step states.</summary>
public static class LogicStepStates
{
    public const string Done = "done";
    public const string Current = "current";
    public const string Pending = "pending";
    public const string Unknown = "unknown";
}
