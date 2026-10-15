using OpcBridge.Client;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// Lays a flat list of element rows out by kind: the levels the network needs, without an indent
/// click per row. A network is authored top to bottom — the gate, then its inputs — so reading the
/// kinds in order is enough to nest them: every element takes the rows its kind reads (one for a
/// NOT, a timer, an edge, a latch or a coil, two for a gate), a gate also takes the rows that
/// follow it once its two are in (its input count is the one that varies), and every row nobody
/// takes stays a root — which is exactly what the block itself is, the AND of its roots.
/// </summary>
public static class LogicNetworkLayout
{
    /// <summary>
    /// The level of every row, in order, for the kinds listed in the order they are shown.
    /// </summary>
    public static int[] Levels(IReadOnlyList<string?> kinds)
    {
        var levels = new int[kinds.Count];
        int position = 0;
        while (position < kinds.Count)
        {
            position = Place(kinds, levels, position, 0);
        }

        return levels;
    }

    /// <summary>
    /// Places one row at the level given, then everything it takes as its inputs; returns the
    /// position after the last row it took.
    /// </summary>
    private static int Place(IReadOnlyList<string?> kinds, int[] levels, int position, int level)
    {
        levels[position] = Math.Min(level, LogicLimits.MaxElementDepth);

        string? kind = kinds[position];
        (_, int max) = LogicElementKinds.InputRange(kind);
        int taken = 0;
        int next = position + 1;
        while (taken < Wanted(kind, max) && next < kinds.Count)
        {
            next = Place(kinds, levels, next, level + 1);
            taken++;
        }

        // A gate's input count is the one that varies, so it keeps taking the rows that follow.
        // An AND takes everything: it is associative, so a row taken in as an input is the same
        // conjunction as the row standing beside it as a root. An OR or an XOR stops at a row that
        // reads inputs of its own — OR(C1, AND(C2, C3)) is not OR(C1) ∧ AND(C2, C3), so the row
        // that needs inputs is left to the author and stays a root beside the gate, while the
        // contacts after the gate are its inputs (as roots they would be ANDed in, which is not
        // what a gate with an OR or an XOR in it means).
        while (LogicElementKinds.IsGate(kind) && taken < max && next < kinds.Count
               && (LogicElementKinds.IsAnd(kind)
                   || string.Equals(kinds[next], LogicElementKinds.Contact, StringComparison.OrdinalIgnoreCase)))
        {
            next = Place(kinds, levels, next, level + 1);
            taken++;
        }

        return next;
    }

    /// <summary>
    /// How many rows the layout gives the kind. The model's minimum is zero for every kind — an
    /// element is saved the moment its row is added and wired afterwards — but a layout has to be
    /// useful, so a row that reads an input is given the one it reads and a gate the two it takes
    /// to be a gate. The second input of a latch or a counter (its reset, its load) is a deliberate
    /// act, so it is left to the author.
    /// </summary>
    private static int Wanted(string? kind, int max) => max switch
    {
        0 => 0,
        _ when LogicElementKinds.IsGate(kind) && max > 1 => 2,
        _ => 1
    };
}
