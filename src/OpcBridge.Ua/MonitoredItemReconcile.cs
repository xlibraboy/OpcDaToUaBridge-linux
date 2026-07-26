namespace OpcBridge.Ua;

/// <summary>
/// Pure set-diff for MonitoredItem reconcile (desired vs currently active NodeId strings).
/// </summary>
public static class MonitoredItemReconcile
{
    public static (IReadOnlyList<string> ToAdd, IReadOnlyList<string> ToRemove) Diff(
        IReadOnlyCollection<string> desiredNodeIds,
        IReadOnlyCollection<string> activeNodeIds)
    {
        HashSet<string> desired = ToSet(desiredNodeIds);
        HashSet<string> active = ToSet(activeNodeIds);

        List<string> toAdd = new();
        foreach (string id in desired)
        {
            if (!active.Contains(id))
            {
                toAdd.Add(id);
            }
        }

        List<string> toRemove = new();
        foreach (string id in active)
        {
            if (!desired.Contains(id))
            {
                toRemove.Add(id);
            }
        }

        // Stable order for deterministic tests and batching.
        toAdd.Sort(StringComparer.Ordinal);
        toRemove.Sort(StringComparer.Ordinal);
        return (toAdd, toRemove);
    }

    private static HashSet<string> ToSet(IReadOnlyCollection<string> ids)
    {
        HashSet<string> set = new(StringComparer.Ordinal);
        if (ids is null)
        {
            return set;
        }

        foreach (string? id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            set.Add(id.Trim());
        }

        return set;
    }
}
