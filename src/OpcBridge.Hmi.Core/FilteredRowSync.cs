using System.Collections.ObjectModel;

namespace OpcBridge.Hmi.Core;

/// <summary>
/// Keeps a filtered view of an ordered row list in step with its source, one row at a time.
/// </summary>
/// <remarks>
/// Clearing a bound list (or swapping the list instance) makes a list control rebuild every
/// container it holds, which drops the hover, the selection and the scroll position the operator
/// is on. Live values arrive far faster than an operator can react, so the visible rows are
/// synced individually instead of the list being rebuilt.
/// </remarks>
public static class FilteredRowSync
{
    /// <summary>
    /// Adds, removes and re-adds rows until <paramref name="target"/> holds exactly the rows of
    /// <paramref name="source"/> that <paramref name="isVisible"/> accepts, in source order. Rows
    /// that are already in place are left untouched, so they keep their containers.
    /// </summary>
    public static void Apply<T>(
        IReadOnlyList<T> source,
        ObservableCollection<T> target,
        Func<T, bool> isVisible)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(isVisible);

        int index = 0;
        foreach (T row in source)
        {
            if (index < target.Count && ReferenceEquals(target[index], row))
            {
                if (isVisible(row))
                {
                    index++;
                }
                else
                {
                    target.RemoveAt(index);
                }

                continue;
            }

            if (isVisible(row))
            {
                target.Insert(index, row);
                index++;
            }
        }

        // Rows that left the source are the only ones the walk cannot visit.
        while (target.Count > index)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
