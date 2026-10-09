namespace OpcBridge.Client;

/// <summary>
/// A block of plant logic the operator can read at a glance: an interlock or permissive
/// (a flat list of conditions, all of which must be true) or a sequence (ordered steps,
/// each gated by its own conditions). Authored in the dashboard's Logic tab, served to the
/// mobile app together with the evaluated state.
///
/// The gate vocabulary is expressed with these fields rather than gate nodes:
/// <b>AND</b> is the block itself (every condition and every OR group must be satisfied),
/// <b>OR</b> is <see cref="LogicConditionDto.Group"/> (members of one group are combined with
/// any-of), <b>normally open</b> is <c>on</c> and <b>normally closed</b> is <c>off</c>
/// (<see cref="LogicConditionOps"/>), and a <b>timer</b> is
/// <see cref="LogicConditionDto.HoldMs"/> (the contact must hold true that long).
/// </summary>
public sealed class LogicBlockDto
{
    public Guid Id { get; set; }

    /// <summary>Operator-facing block name (e.g. "Line 01 Start").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional interlock group (e.g. "Primary Arm"). The phone collects blocks carrying the
    /// same group under one collapsible heading, so a machine's up/down interlocks read
    /// together instead of as a flat list. Blank = ungrouped.
    /// </summary>
    public string Group { get; set; } = string.Empty;

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

    /// <summary>
    /// The block's IEC 61131-3 network: contacts, gates (AND / OR / XOR / NOT) and standard
    /// function blocks (TON, TOF, TP, CTU, CTD, SR, RS, R_TRIG, F_TRIG), nested through each
    /// element's inputs. Empty for a block authored in the simple conditions form — the two
    /// are alternatives; the network is what "any logic flow" needs.
    /// </summary>
    public List<LogicElementDto> Elements { get; set; } = new();

    /// <summary>Conditions for interlock / permissive blocks. Empty for sequences.</summary>
    public List<LogicConditionDto> Conditions { get; set; } = new();

    /// <summary>Ordered steps for sequence blocks. Empty for interlocks / permissives.</summary>
    public List<LogicStepDto> Steps { get; set; } = new();

    /// <summary>Block-level action buttons shown on the phone.</summary>
    public List<LogicActionDto> Actions { get; set; } = new();
}

/// <summary>
/// One element of an IEC 61131-3 logic network. A contact (or a numeric comparison, which is
/// the same element with a gt/lt/eq operator) is a leaf that reads one tag; a gate or a
/// function block combines its <see cref="Inputs"/> — itself elements, so a network nests to
/// any depth. Contacts are normally open (<c>on</c>) or normally closed (<c>off</c>).
/// </summary>
public sealed class LogicElementDto
{
    public Guid Id { get; set; }

    /// <summary>One of <see cref="LogicElementKinds"/>.</summary>
    public string Kind { get; set; } = LogicElementKinds.Contact;

    /// <summary>Operator sentence for a contact, or the element's label for a gate / block.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Tag a contact reads; empty for gates and function blocks.</summary>
    public string SourceId { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    /// <summary>Contact form: on (normally open) / off (normally closed) / gt / lt / eq.</summary>
    public string Op { get; set; } = LogicConditionOps.On;

    /// <summary>Comparison value; required for gt / lt / eq, ignored for on / off.</summary>
    public double? Value { get; set; }

    /// <summary>Timer preset (PT) in milliseconds for TON / TOF / TP.</summary>
    public int PtMs { get; set; }

    /// <summary>Counter preset (PV) for CTU / CTD.</summary>
    public int Pv { get; set; }

    /// <summary>
    /// Inputs, in the order the element reads them: IN for TON / TOF / TP / R_TRIG / F_TRIG,
    /// CU (and R) for CTU, CD (and LD) for CTD, S1/R for SR, S/R1 for RS, and any number for
    /// AND / OR / XOR.
    /// </summary>
    public List<LogicElementDto> Inputs { get; set; } = new();

    /// <summary>Block or warn — a warn element is shown and tracked but never blocks.</summary>
    public string Severity { get; set; } = LogicConditionSeverities.Block;

    /// <summary>"What to do next" text shown while the element is not satisfied.</summary>
    public string? NextStepText { get; set; }
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

    /// <summary>
    /// Optional OR group within the block. Conditions sharing the same group text (matched
    /// case-insensitively, trimmed) are combined with any-of: the group is satisfied as soon
    /// as one member is true, and the block still requires every group and every ungrouped
    /// condition — the AND of ORs a real interlock is drawn from. Warn-only members are shown
    /// but never count towards the group.
    /// </summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Hold timer (0 = none): the contact counts as satisfied only after its input has held
    /// true continuously this long. Evaluated on the bridge's clock, so a bridge restart
    /// re-times every running hold. 0 – 3,600,000 ms.
    /// </summary>
    public int HoldMs { get; set; }

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

    /// <summary>The step's IEC network; empty when the step is authored with plain conditions.</summary>
    public List<LogicElementDto> Elements { get; set; } = new();

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

/// <summary>
/// IEC 61131-3 element kinds: the contacts, the boolean gates and the standard function
/// blocks the bridge evaluates. Names follow the standard (TON / TOF / TP, CTU / CTD,
/// SR / RS, R_TRIG / F_TRIG).
/// </summary>
public static class LogicElementKinds
{
    /// <summary>A contact (or comparison) reading one tag — the leaf of a network.</summary>
    public const string Contact = "contact";

    public const string And = "and";
    public const string Or = "or";
    public const string Xor = "xor";
    public const string Not = "not";

    /// <summary>On-delay timer: Q follows IN after IN has been true for PT.</summary>
    public const string Ton = "ton";

    /// <summary>Off-delay timer: Q stays true for PT after IN falls.</summary>
    public const string Tof = "tof";

    /// <summary>Pulse timer: Q is true for PT once IN rises.</summary>
    public const string Tp = "tp";

    /// <summary>Up counter: CV counts CU's rising edges, Q is true at CV >= PV, R resets.</summary>
    public const string Ctu = "ctu";

    /// <summary>Down counter: CV counts CD's rising edges down from PV, Q is true at CV &lt;= 0.</summary>
    public const string Ctd = "ctd";

    /// <summary>Set-dominant latch (S1 sets, R resets).</summary>
    public const string Sr = "sr";

    /// <summary>Reset-dominant latch (S sets, R1 resets).</summary>
    public const string Rs = "rs";

    /// <summary>Q is true for one evaluation when IN rises.</summary>
    public const string RisingEdge = "r_trig";

    /// <summary>Q is true for one evaluation when IN falls.</summary>
    public const string FallingEdge = "f_trig";

    public static readonly string[] All =
    {
        Contact, And, Or, Xor, Not, Ton, Tof, Tp, Ctu, Ctd, Sr, Rs, RisingEdge, FallingEdge
    };

    public static bool IsValid(string? kind) =>
        kind is not null && Array.Exists(All, k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>AND / OR / XOR / NOT — the boolean gates.</summary>
    public static bool IsGate(string? kind) =>
        string.Equals(kind, And, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Or, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Xor, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Not, StringComparison.OrdinalIgnoreCase);

    /// <summary>TON / TOF / TP / CTU / CTD / SR / RS / R_TRIG / F_TRIG.</summary>
    public static bool IsFunctionBlock(string? kind) =>
        string.Equals(kind, Ton, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Tof, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Tp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Ctu, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Ctd, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Sr, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Rs, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, RisingEdge, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, FallingEdge, StringComparison.OrdinalIgnoreCase);

    public static bool IsTimer(string? kind) =>
        string.Equals(kind, Ton, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Tof, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Tp, StringComparison.OrdinalIgnoreCase);

    public static bool IsCounter(string? kind) =>
        string.Equals(kind, Ctu, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Ctd, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How many inputs the kind reads: a fixed count where the standard fixes it (NOT takes
    /// one, the timers and edge triggers one, SR / RS two), a range where it varies (CTU takes
    /// CU and an optional R, CTD takes CD and an optional LD).
    /// </summary>
    public static (int Min, int Max) InputRange(string? kind)
    {
        if (string.Equals(kind, Not, StringComparison.OrdinalIgnoreCase) ||
            IsTimer(kind) ||
            string.Equals(kind, RisingEdge, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, FallingEdge, StringComparison.OrdinalIgnoreCase))
        {
            return (1, 1);
        }

        if (string.Equals(kind, Sr, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, Rs, StringComparison.OrdinalIgnoreCase))
        {
            return (2, 2);
        }

        if (IsCounter(kind))
        {
            return (1, 2);
        }

        if (IsGate(kind))
        {
            // A gate may also stand alone (a single input passes through), which keeps the
            // editor tolerant while a row is being built.
            return (1, 32);
        }

        return (0, 0);
    }
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
