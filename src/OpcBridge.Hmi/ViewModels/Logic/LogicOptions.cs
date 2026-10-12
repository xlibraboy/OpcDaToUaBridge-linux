using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>One entry of an editor dropdown: the stored value plus the label the operator reads.</summary>
public sealed record LogicSelectOption(string Value, string Label)
{
    public static LogicSelectOption? Find(IReadOnlyList<LogicSelectOption> options, string? value) =>
        options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The editor's fixed vocabulary, worded the way the phone and the web app worded it.</summary>
public static class LogicOptions
{
    public static IReadOnlyList<LogicSelectOption> BlockKinds { get; } = new[]
    {
        new LogicSelectOption(LogicBlockKinds.Interlock, "Interlock"),
        new LogicSelectOption(LogicBlockKinds.Permissive, "Permissive"),
        new LogicSelectOption(LogicBlockKinds.Sequence, "Sequence")
    };

    public static IReadOnlyList<LogicSelectOption> Ops { get; } = new[]
    {
        new LogicSelectOption(LogicConditionOps.On, "ON (NO contact)"),
        new LogicSelectOption(LogicConditionOps.Off, "OFF (NC contact)"),
        new LogicSelectOption(LogicConditionOps.GreaterThan, ">"),
        new LogicSelectOption(LogicConditionOps.LessThan, "<"),
        new LogicSelectOption(LogicConditionOps.Equal, "=")
    };

    public static IReadOnlyList<LogicSelectOption> Severities { get; } = new[]
    {
        new LogicSelectOption(LogicConditionSeverities.Block, "blocks"),
        new LogicSelectOption(LogicConditionSeverities.Warn, "warn only")
    };

    public static IReadOnlyList<LogicSelectOption> ElementKinds { get; } = new[]
    {
        new LogicSelectOption(LogicElementKinds.Contact, "Contact — NO / NC / compare / relay"),
        new LogicSelectOption(LogicElementKinds.Coil, "Coil — drive internal relay (IN, M)"),
        new LogicSelectOption(LogicElementKinds.And, "AND"),
        new LogicSelectOption(LogicElementKinds.Or, "OR"),
        new LogicSelectOption(LogicElementKinds.Xor, "XOR"),
        new LogicSelectOption(LogicElementKinds.Not, "NOT"),
        new LogicSelectOption(LogicElementKinds.Ton, "TON — on-delay (IN, PT)"),
        new LogicSelectOption(LogicElementKinds.Tof, "TOF — off-delay (IN, PT)"),
        new LogicSelectOption(LogicElementKinds.Tp, "TP — pulse (IN, PT)"),
        new LogicSelectOption(LogicElementKinds.Ctu, "CTU — up counter (CU, R, PV)"),
        new LogicSelectOption(LogicElementKinds.Ctd, "CTD — down counter (CD, LD, PV)"),
        new LogicSelectOption(LogicElementKinds.Sr, "SR — set-dominant latch (S1, R)"),
        new LogicSelectOption(LogicElementKinds.Rs, "RS — reset-dominant latch (S, R1)"),
        new LogicSelectOption(LogicElementKinds.RisingEdge, "R_TRIG — rising edge (IN)"),
        new LogicSelectOption(LogicElementKinds.FallingEdge, "F_TRIG — falling edge (IN)")
    };
}
