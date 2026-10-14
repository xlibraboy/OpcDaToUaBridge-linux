using System.Collections.ObjectModel;

namespace OpcBridge.Hmi.ViewModels.Logic;

/// <summary>
/// An editor row that carries the per-row order buttons: insert a fresh row above or below it,
/// and move it up or down.
/// </summary>
public interface ILogicOrderRow
{
    /// <summary>Re-evaluates the row's insert / move guards after the list around it changed.</summary>
    void NotifyOrderChanged();
}

/// <summary>
/// One ordered list of editor rows, as the rows' own order buttons see it. The panel owns the list
/// and the factory, so a row built here is created exactly like the ones the panel builds (dirty
/// tracker attached) and a list is never appended to at its end only. A nested list — the network's
/// element rows — inserts and moves in whole <em>groups</em>: a row plus every deeper row under it,
/// so a gate's inputs travel with the gate instead of being left behind on the row above.
/// </summary>
public sealed class LogicRowList<T>
    where T : class, ILogicOrderRow
{
    private readonly ObservableCollection<T> rows_;
    private readonly Func<T?, T>? create_;
    private readonly Action? onChanged_;
    private readonly Func<T, int>? indentOf_;

    /// <param name="rows">The list the rows live in.</param>
    /// <param name="create">Builds a fresh row beside the given one (the panel's own factory).</param>
    /// <param name="onChanged">Runs after an insert or a move (dirty state, editor mode).</param>
    /// <param name="indentOf">The row's nesting level, for lists whose rows nest (the network).</param>
    public LogicRowList(
        ObservableCollection<T> rows,
        Func<T?, T>? create = null,
        Action? onChanged = null,
        Func<T, int>? indentOf = null)
    {
        rows_ = rows;
        create_ = create;
        onChanged_ = onChanged;
        indentOf_ = indentOf;
    }

    /// <summary>True while a fresh row can be inserted beside this one.</summary>
    public bool CanInsert(T row) => create_ is not null && rows_.Contains(row);

    public bool CanMoveUp(T row) => PreviousSiblingStart(row) >= 0;

    public bool CanMoveDown(T row) => NextSiblingStart(row) >= 0;

    /// <summary>A fresh row directly above this one, or below this row's whole group.</summary>
    public void InsertBeside(T row, bool above)
    {
        (int start, int end) = Group(row);
        if (start < 0 || create_ is null)
        {
            return;
        }

        rows_.Insert(above ? start : end, create_(row));
        NotifyChanged();
    }

    /// <summary>Moves this row one place among its siblings — up, or down.</summary>
    public void MoveUp(T row)
    {
        (int start, int end) = Group(row);
        int previous = PreviousSiblingStart(row);
        if (start < 0 || previous < 0)
        {
            return;
        }

        int size = end - start;
        for (int k = 0; k < size; k++)
        {
            rows_.Move(start + k, previous + k);
        }

        NotifyChanged();
    }

    /// <summary>Moves this row one place down, over the sibling directly below it.</summary>
    public void MoveDown(T row)
    {
        (int start, int end) = Group(row);
        int next = NextSiblingStart(row);
        if (start < 0 || next < 0)
        {
            return;
        }

        // Back to front: each moved group row lands one place before the previous one, so the
        // group keeps its own order while it slides past the sibling below.
        int size = end - start;
        int last = EndOfGroup(next) - 1;
        for (int k = size - 1; k >= 0; k--)
        {
            rows_.Move(start + k, last - (size - 1 - k));
        }

        NotifyChanged();
    }

    /// <summary>Re-evaluates every row's order guards and reports the list as changed.</summary>
    public void NotifyChanged()
    {
        foreach (T row in rows_)
        {
            row.NotifyOrderChanged();
        }

        onChanged_?.Invoke();
    }

    /// <summary>The row itself and every deeper row under it — what a move carries along.</summary>
    private (int Start, int End) Group(T row)
    {
        int start = rows_.IndexOf(row);
        return start < 0 ? (-1, -1) : (start, EndOfGroup(start));
    }

    private int EndOfGroup(int start)
    {
        int indent = IndentOf(rows_[start]);
        int end = start + 1;
        while (end < rows_.Count && IndentOf(rows_[end]) > indent)
        {
            end++;
        }

        return end;
    }

    /// <summary>The row above this one's group at the same level, or -1 when it is the first.</summary>
    private int PreviousSiblingStart(T row)
    {
        (int start, _) = Group(row);
        if (start <= 0)
        {
            return -1;
        }

        int index = start - 1;
        while (index >= 0 && IndentOf(rows_[index]) > IndentOf(row))
        {
            index--;
        }

        return index >= 0 && IndentOf(rows_[index]) == IndentOf(row) ? index : -1;
    }

    /// <summary>The row below this one's group at the same level, or -1 when it is the last.</summary>
    private int NextSiblingStart(T row)
    {
        (_, int end) = Group(row);
        return end > 0 && end < rows_.Count && IndentOf(rows_[end]) == IndentOf(row) ? end : -1;
    }

    private int IndentOf(T row) => indentOf_?.Invoke(row) ?? 0;
}
