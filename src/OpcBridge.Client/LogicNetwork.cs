using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OpcBridge.Client;

/// <summary>
/// The IEC 61131-3 network of a block or a sequence step, and the bridge from the simple
/// conditions form. A block authored with plain conditions — optionally OR groups and hold
/// timers — is expanded into the equivalent network: an AND over its roots, an OR element per
/// group, a TON around a held contact. The evaluator and the phone therefore only ever walk
/// elements, whatever form the block was authored in.
/// </summary>
public static class LogicNetwork
{
    /// <summary>The block's network: its authored elements, else its simple conditions expanded.</summary>
    public static IReadOnlyList<LogicElementDto> For(LogicBlockDto block) =>
        block.Elements.Count > 0 ? block.Elements : Expand(block.Conditions);

    /// <summary>The step's network: its authored elements, else its simple conditions expanded.</summary>
    public static IReadOnlyList<LogicElementDto> For(LogicStepDto step) =>
        step.Elements.Count > 0 ? step.Elements : Expand(step.Conditions);

    /// <summary>
    /// Expands the simple form: one contact per condition (normally open for <c>on</c>,
    /// normally closed for <c>off</c>), an OR element per group holding its members in authored
    /// order, a TON wrapping a contact that carries a hold time, and — when more than one root
    /// remains — an AND element over them, which is the block's own AND.
    /// </summary>
    public static List<LogicElementDto> Expand(IReadOnlyList<LogicConditionDto>? conditions)
    {
        List<LogicElementDto> roots = new();
        Dictionary<string, LogicElementDto> groups = new(StringComparer.OrdinalIgnoreCase);

        foreach (LogicConditionDto condition in conditions ?? Array.Empty<LogicConditionDto>())
        {
            LogicElementDto element = Contact(condition);
            if (condition.HoldMs > 0)
            {
                element = new LogicElementDto
                {
                    Id = LogicElementIds.Derived(condition.Id, 1),
                    Kind = LogicElementKinds.Ton,
                    Text = condition.Text,
                    PtMs = condition.HoldMs,
                    Severity = condition.Severity,
                    NextStepText = condition.NextStepText,
                    Inputs = { element }
                };
            }

            string group = condition.Group?.Trim() ?? string.Empty;
            if (group.Length == 0)
            {
                roots.Add(element);
                continue;
            }

            if (!groups.TryGetValue(group, out LogicElementDto? or))
            {
                or = new LogicElementDto
                {
                    Id = LogicElementIds.Derived(condition.Id, 2),
                    Kind = LogicElementKinds.Or,
                    Text = group,
                    Severity = LogicConditionSeverities.Block
                };
                groups[group] = or;
                roots.Add(or);
            }

            or.Inputs.Add(element);
        }

        if (roots.Count <= 1)
        {
            return roots;
        }

        return new List<LogicElementDto>
        {
            new()
            {
                Id = LogicElementIds.Derived(roots[0].Id, 3),
                Kind = LogicElementKinds.And,
                Inputs = roots
            }
        };
    }

    /// <summary>One condition as a contact element, keeping its id (states and notes key on it).</summary>
    public static LogicElementDto Contact(LogicConditionDto condition) => new()
    {
        Id = condition.Id,
        Kind = LogicElementKinds.Contact,
        Text = condition.Text,
        SourceId = condition.SourceId,
        ItemId = condition.ItemId,
        Op = condition.Op,
        Value = condition.Value,
        Severity = condition.Severity,
        NextStepText = condition.NextStepText
    };

    /// <summary>
    /// Depth-first walk in the order the network is drawn: every element with its depth
    /// (0 = a root). This is the order the phone lists rows in, and the evaluator reports in.
    /// </summary>
    public static List<(LogicElementDto Element, int Depth)> Walk(IEnumerable<LogicElementDto> roots)
    {
        List<(LogicElementDto Element, int Depth)> walked = new();
        Stack<(LogicElementDto Element, int Depth)> pending = new();

        // Push in reverse so the first root is walked first.
        List<LogicElementDto> ordered = roots.ToList();
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            pending.Push((ordered[i], 0));
        }

        while (pending.Count > 0)
        {
            (LogicElementDto element, int depth) = pending.Pop();
            walked.Add((element, depth));

            for (int i = element.Inputs.Count - 1; i >= 0; i--)
            {
                pending.Push((element.Inputs[i], depth + 1));
            }
        }

        return walked;
    }
}

/// <summary>
/// Stable ids for the elements the simple form expands into. They must not change between
/// evaluations: the state store keys a timer's or a latch's memory on the element id, so a
/// fresh id every pass would restart every hold.
/// </summary>
public static class LogicElementIds
{
    /// <summary>A deterministic id derived from a source id and a role number.</summary>
    public static Guid Derived(Guid source, int role)
    {
        Span<byte> buffer = stackalloc byte[20];
        source.TryWriteBytes(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[16..], role);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return new Guid(hash[..16]);
    }
}
