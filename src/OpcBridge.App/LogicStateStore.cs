namespace OpcBridge.App;

/// <summary>
/// Keeps the little bit of memory an IEC 61131-3 network needs between evaluations: how long a
/// timer's input has been true, a counter's value, a latch's state and an edge trigger's last
/// input. One singleton serves the API read and the hub broadcaster, so both report the same
/// state; the memory lives in the process only, so a bridge restart re-times every timer and
/// reloads every counter — the honest reading for a monitor.
///
/// Thread-safe — a request thread and the flush timer both evaluate. Pruning uses
/// <see cref="BeginPass"/>/<see cref="EndPass"/>: an id a whole pass never saw (its element was
/// deleted or its block changed) loses its memory, and an interleaved pass can at worst drop a
/// live memory, which simply restarts it.
/// </summary>
public sealed class LogicStateStore
{
    private readonly object sync_ = new();
    private readonly Dictionary<Guid, Memory> memories_ = new();
    private HashSet<Guid>? seen_;

    private sealed class Memory
    {
        public DateTime? SinceUtc;
        public bool? LastInput;
        public bool CountLoaded;
        public int Count;
    }

    /// <summary>Starts an evaluation pass over every element of every block.</summary>
    public void BeginPass()
    {
        lock (sync_)
        {
            seen_ = new HashSet<Guid>();
        }
    }

    /// <summary>Ends the pass and forgets memories whose element was not part of it.</summary>
    public void EndPass()
    {
        lock (sync_)
        {
            if (seen_ is null)
            {
                return;
            }

            if (memories_.Count > 0)
            {
                List<Guid> stale = memories_.Keys.Where(id => !seen_.Contains(id)).ToList();
                foreach (Guid id in stale)
                {
                    memories_.Remove(id);
                }
            }

            seen_ = null;
        }
    }

    /// <summary>
    /// On-delay timer (TON): the output rises once the input has held true for
    /// <paramref name="ptMs"/> and falls with the input, which also clears the elapsed time.
    /// </summary>
    public (bool Q, int ElapsedMs) Ton(Guid id, bool input, int ptMs, DateTime nowUtc)
    {
        lock (sync_)
        {
            Memory memory = MemoryFor(id);
            if (!input)
            {
                memory.SinceUtc = null;
                return (false, 0);
            }

            memory.SinceUtc ??= nowUtc;
            double elapsed = Math.Max(0, (nowUtc - memory.SinceUtc.Value).TotalMilliseconds);
            if (elapsed >= ptMs)
            {
                return (true, ptMs);
            }

            return (false, (int)elapsed);
        }
    }

    /// <summary>
    /// Off-delay timer (TOF): the output follows the input up and stays true for
    /// <paramref name="ptMs"/> after the input falls. The elapsed time counts the off-delay.
    /// </summary>
    public (bool Q, int ElapsedMs) Tof(Guid id, bool input, int ptMs, DateTime nowUtc)
    {
        lock (sync_)
        {
            Memory memory = MemoryFor(id);
            if (input)
            {
                memory.SinceUtc = null;
                memory.LastInput = true;
                return (true, 0);
            }

            if (memory.LastInput != true)
            {
                // Was never true: nothing to extend.
                memory.SinceUtc = null;
                return (false, 0);
            }

            memory.SinceUtc ??= nowUtc;
            double elapsed = Math.Max(0, (nowUtc - memory.SinceUtc.Value).TotalMilliseconds);
            if (elapsed >= ptMs)
            {
                memory.LastInput = false;
                return (false, ptMs);
            }

            return (true, (int)elapsed);
        }
    }

    /// <summary>
    /// Pulse timer (TP): a rising input starts one pulse of <paramref name="ptMs"/>, which runs
    /// to its end even if the input falls in between; the input must fall before another pulse.
    /// </summary>
    public (bool Q, int ElapsedMs) Tp(Guid id, bool input, int ptMs, DateTime nowUtc)
    {
        lock (sync_)
        {
            Memory memory = MemoryFor(id);
            if (memory.SinceUtc is null)
            {
                if (!input || memory.LastInput == true)
                {
                    memory.LastInput = input;
                    return (false, 0);
                }

                memory.SinceUtc = nowUtc;
            }

            double elapsed = Math.Max(0, (nowUtc - memory.SinceUtc.Value).TotalMilliseconds);
            memory.LastInput = input;
            if (elapsed >= ptMs)
            {
                memory.SinceUtc = null;
                return (false, ptMs);
            }

            return (true, (int)elapsed);
        }
    }

    /// <summary>
    /// Up counter (CTU): counts the input's rising edges, a true reset clears it, and the output
    /// is true once the count reaches <paramref name="pv"/>.
    /// </summary>
    public (bool Q, int Count) Ctu(Guid id, bool up, bool reset, int pv)
    {
        lock (sync_)
        {
            Memory memory = MemoryFor(id);
            if (reset)
            {
                memory.Count = 0;
                memory.LastInput = false;
                return (pv <= 0, 0);
            }

            if (up && memory.LastInput != true)
            {
                memory.Count++;
            }

            memory.LastInput = up;
            return (memory.Count >= pv, memory.Count);
        }
    }

    /// <summary>
    /// Down counter (CTD): starts at <paramref name="pv"/>, counts the input's rising edges down,
    /// reloads on its second input, and is true once the count reaches zero.
    /// </summary>
    public (bool Q, int Count) Ctd(Guid id, bool down, bool load, int pv)
    {
        lock (sync_)
        {
            Memory memory = MemoryFor(id);
            if (!memory.CountLoaded || load)
            {
                memory.Count = pv;
                memory.CountLoaded = true;
                if (load)
                {
                    memory.LastInput = down;
                    return (pv <= 0, memory.Count);
                }
            }

            if (down && memory.LastInput != true)
            {
                memory.Count = Math.Max(0, memory.Count - 1);
            }

            memory.LastInput = down;
            return (memory.Count <= 0, memory.Count);
        }
    }

    /// <summary>Set-dominant latch (SR): a true set wins, else a true reset clears, else it holds.</summary>
    public bool Sr(Guid id, bool set, bool reset)
    {
        lock (sync_)
        {
            Memory memory = MemoryFor(id);
            if (set)
            {
                memory.LastInput = true;
            }
            else if (reset)
            {
                memory.LastInput = false;
            }

            return memory.LastInput == true;
        }
    }

    /// <summary>Reset-dominant latch (RS): a true reset wins, else a true set holds it in.</summary>
    public bool Rs(Guid id, bool set, bool reset)
    {
        lock (sync_)
        {
            Memory memory = MemoryFor(id);
            if (reset)
            {
                memory.LastInput = false;
            }
            else if (set)
            {
                memory.LastInput = true;
            }

            return memory.LastInput == true;
        }
    }

    /// <summary>
    /// Edge trigger (R_TRIG / F_TRIG): true for the one evaluation that sees the input rise (or
    /// fall) — "one scan" here is one evaluation pass.
    /// </summary>
    public bool Edge(Guid id, bool input, bool rising)
    {
        lock (sync_)
        {
            Memory memory = MemoryFor(id);
            bool? previous = memory.LastInput;
            memory.LastInput = input;
            if (previous is null)
            {
                return false;
            }

            return rising ? previous == false && input : previous == true && !input;
        }
    }

    private Memory MemoryFor(Guid id)
    {
        seen_?.Add(id);
        if (!memories_.TryGetValue(id, out Memory? memory))
        {
            memory = new Memory();
            memories_[id] = memory;
        }

        return memory;
    }
}
