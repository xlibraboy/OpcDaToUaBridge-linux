using OpcBridge.App;
using OpcBridge.Client;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The IEC 61131-3 side of the logic model: how the simple conditions form expands into a
/// network (with stable ids, so a timer keeps its memory), how the tree walks, and how the
/// standard gates and function blocks evaluate.
/// </summary>
public sealed class LogicNetworkTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 6, 0, 0, DateTimeKind.Utc);

    private static Func<string, string, BridgeValueSnapshot?> Values(
        params (string SourceId, string ItemId, object? Value, bool Good)[] entries)
    {
        Dictionary<string, BridgeValueSnapshot> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string sourceId, string itemId, object? value, bool good) in entries)
        {
            map[LogicStateEvaluator.Key(sourceId, itemId)] =
                new BridgeValueSnapshot(sourceId, itemId, value, Now, good ? 192 : 0, good, Now);
        }

        return (sourceId, itemId) =>
            map.TryGetValue(LogicStateEvaluator.Key(sourceId, itemId), out BridgeValueSnapshot snapshot) ? snapshot : null;
    }

    private static LogicElementDto Contact(string itemId, string op = LogicConditionOps.On, double? value = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = LogicElementKinds.Contact,
        Text = itemId + " must hold",
        SourceId = "sim",
        ItemId = itemId,
        Op = op,
        Value = value
    };

    private static LogicElementDto Gate(string kind, params LogicElementDto[] inputs) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Inputs = inputs.ToList()
    };

    private static LogicBlockDto Network(params LogicElementDto[] elements) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Primary Arm Up",
        Kind = LogicBlockKinds.Interlock,
        Elements = elements.ToList()
    };

    private static Func<string, string, TagMapping?> Mappings(params TagMapping[] mappings)
    {
        IReadOnlyDictionary<string, TagMapping> index = LogicStateEvaluator.BuildMappingIndex(mappings);
        return (sourceId, itemId) =>
            index.TryGetValue(LogicStateEvaluator.Key(sourceId, itemId), out TagMapping mapping) ? mapping : null;
    }

    /// <summary>Evaluates a network against the default set of values: PS1/FS1 true, LS2 and PB1 false.</summary>
    private static LogicBlockStateDto Evaluate(LogicBlockDto block, LogicStateStore? states = null, DateTime? now = null) =>
        LogicStateEvaluator.EvaluateBlock(
            block,
            Values(("sim", "PS1", true, true), ("sim", "LS2", false, true), ("sim", "PB1", false, true), ("sim", "FS1", true, true)),
            Mappings(),
            states,
            now ?? Now);

    private static LogicBlockStateDto EvaluateWith(
        LogicBlockDto block,
        params (string SourceId, string ItemId, object? Value, bool Good)[] values) =>
        LogicStateEvaluator.EvaluateBlock(block, Values(values), Mappings());

    private static LogicElementStateDto Element(LogicBlockStateDto state, LogicElementDto element) =>
        state.Elements.Single(candidate => candidate.Id == element.Id);

    [Fact]
    public void Expand_TurnsConditionsGroupsAndHoldsIntoANetwork()
    {
        LogicConditionDto plain = new() { Id = Guid.NewGuid(), Text = "Pressure switch PS1 must read 1", SourceId = "sim", ItemId = "PS1", Op = LogicConditionOps.On };
        LogicConditionDto local = new() { Id = Guid.NewGuid(), Text = "Local start", SourceId = "sim", ItemId = "PB1", Op = LogicConditionOps.On, Group = "Start permissive" };
        LogicConditionDto remote = new() { Id = Guid.NewGuid(), Text = "Remote start", SourceId = "sim", ItemId = "PB2", Op = LogicConditionOps.On, Group = "Start permissive" };
        LogicConditionDto held = new() { Id = Guid.NewGuid(), Text = "Flow must hold", SourceId = "sim", ItemId = "FS1", Op = LogicConditionOps.On, HoldMs = 3000 };

        List<LogicElementDto> roots = LogicNetwork.Expand(new[] { plain, local, remote, held });

        // Roots: the block's AND over the plain contact, the group and the timer.
        LogicElementDto root = Assert.Single(roots);
        Assert.Equal(LogicElementKinds.And, root.Kind);
        Assert.Equal(3, root.Inputs.Count);

        Assert.Equal(LogicElementKinds.Contact, root.Inputs[0].Kind);
        Assert.Equal(plain.Id, root.Inputs[0].Id);

        LogicElementDto or = root.Inputs[1];
        Assert.Equal(LogicElementKinds.Or, or.Kind);
        Assert.Equal("Start permissive", or.Text);
        Assert.Equal(new[] { local.Id, remote.Id }, or.Inputs.Select(input => input.Id));

        LogicElementDto ton = root.Inputs[2];
        Assert.Equal(LogicElementKinds.Ton, ton.Kind);
        Assert.Equal(3000, ton.PtMs);
        Assert.Equal(held.Id, Assert.Single(ton.Inputs).Id);
    }

    [Fact]
    public void Expand_KeepsItsIdsStable_SoATimerKeepsItsMemory()
    {
        LogicConditionDto held = new() { Id = Guid.NewGuid(), Text = "Flow must hold", SourceId = "sim", ItemId = "FS1", Op = LogicConditionOps.On, HoldMs = 3000 };

        Guid first = LogicNetwork.Expand(new[] { held })[0].Id;
        Guid second = LogicNetwork.Expand(new[] { held })[0].Id;

        Assert.Equal(first, second);
        Assert.Equal(LogicElementIds.Derived(held.Id, 1), first);
    }

    [Fact]
    public void Walk_IsDepthFirstInDrawingOrder()
    {
        LogicElementDto and = Gate(LogicElementKinds.And, Contact("PS1"), Gate(LogicElementKinds.Or, Contact("PB1"), Contact("LS2")));

        List<(LogicElementDto Element, int Depth)> walked = LogicNetwork.Walk(new[] { and });

        Assert.Equal(5, walked.Count);
        Assert.Equal(0, walked[0].Depth);
        Assert.Equal(and.Id, walked[0].Element.Id);
        Assert.Equal(1, walked[1].Depth);
        Assert.Equal("PS1", walked[1].Element.ItemId);
        Assert.Equal(1, walked[2].Depth);
        Assert.Equal(LogicElementKinds.Or, walked[2].Element.Kind);
        Assert.Equal(2, walked[3].Depth);
        Assert.Equal("PB1", walked[3].Element.ItemId);
        Assert.Equal("LS2", walked[4].Element.ItemId);
    }

    [Fact]
    public void Gates_FollowKleeneLogic()
    {
        LogicElementDto ps1 = Contact("PS1");
        LogicElementDto ls2 = Contact("LS2");
        LogicElementDto missing = Contact("NOPE");

        LogicBlockStateDto and = Evaluate(Network(Gate(LogicElementKinds.And, ps1, ls2)));
        Assert.Equal(LogicBlockStates.Blocked, and.State);
        Assert.Equal(LogicConditionStates.False, and.Elements[0].State);

        LogicBlockStateDto or = Evaluate(Network(Gate(LogicElementKinds.Or, ps1, ls2)));
        Assert.Equal(LogicBlockStates.Ready, or.State);

        // XOR over two: true only when exactly one input is true.
        LogicElementDto ps1b = Contact("PS1");
        LogicBlockStateDto xor = Evaluate(Network(Gate(LogicElementKinds.Xor, ps1b, Contact("LS2"))));
        Assert.Equal(LogicBlockStates.Ready, xor.State);
        LogicBlockStateDto bothFalse = EvaluateWith(
            Network(Gate(LogicElementKinds.Xor, Contact("PS1"), Contact("PS1"))),
            ("sim", "PS1", false, true));
        Assert.Equal(LogicBlockStates.Blocked, bothFalse.State);

        // NOT inverts; an unknown input stays unknown.
        LogicBlockStateDto not = Evaluate(Network(Gate(LogicElementKinds.Not, Contact("LS2"))));
        Assert.Equal(LogicBlockStates.Ready, not.State);
        LogicBlockStateDto notMissing = Evaluate(Network(Gate(LogicElementKinds.Not, missing)));
        Assert.Equal(LogicBlockStates.Unknown, notMissing.State);
        Assert.Equal("no data for NOPE", notMissing.Reason);
    }

    [Fact]
    public void Timer_And_Counter_ReportTheirProgress()
    {
        LogicStateStore states = new();
        LogicElementDto flow = Contact("FS1");
        LogicBlockDto block = Network(new LogicElementDto
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.Ton,
            Text = "Flow must hold 3 s",
            PtMs = 3000,
            Inputs = { flow }
        });

        LogicBlockStateDto start = Evaluate(block, states, Now);
        LogicElementStateDto timer = start.Elements[0];
        Assert.Equal(LogicElementKinds.Ton, timer.Kind);
        Assert.Equal(LogicConditionStates.False, timer.State);
        Assert.Equal(0, timer.ElapsedMs);
        Assert.Equal("Flow must hold 3 s", start.Reason);

        LogicBlockStateDto mid = Evaluate(block, states, Now.AddSeconds(1));
        Assert.Equal(1000, mid.Elements[0].ElapsedMs);
        Assert.Equal(LogicBlockStates.Blocked, mid.State);

        LogicBlockStateDto done = Evaluate(block, states, Now.AddSeconds(3));
        Assert.Equal(LogicBlockStates.Ready, done.State);
        Assert.Null(done.Reason);

        // CTU counts rising edges and is true at its preset.
        LogicStateStore counters = new();
        LogicElementDto pulse = Contact("PS1");
        LogicBlockDto counterBlock = Network(new LogicElementDto
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.Ctu,
            Text = "Two starts",
            Pv = 2,
            Inputs = { pulse }
        });
        LogicStateEvaluator.EvaluateBlock(
            counterBlock, Values(("sim", "PS1", false, true)), Mappings(), counters, Now);
        LogicBlockStateDto one = LogicStateEvaluator.EvaluateBlock(
            counterBlock, Values(("sim", "PS1", true, true)), Mappings(), counters, Now.AddSeconds(1));
        Assert.Equal(1, one.Elements[0].Count);
        Assert.Equal(LogicBlockStates.Blocked, one.State);

        // A falling edge arms the next count; the second rise reaches the preset.
        LogicStateEvaluator.EvaluateBlock(
            counterBlock, Values(("sim", "PS1", false, true)), Mappings(), counters, Now.AddSeconds(2));
        LogicBlockStateDto two = LogicStateEvaluator.EvaluateBlock(
            counterBlock, Values(("sim", "PS1", true, true)), Mappings(), counters, Now.AddSeconds(3));
        Assert.Equal(2, two.Elements[0].Count);
        Assert.Equal(LogicBlockStates.Ready, two.State);
    }

    [Fact]
    public void Latch_HoldsAndEdgeTriggersFireOnce()
    {
        LogicStateStore states = new();
        LogicElementDto set = Contact("PS1");
        LogicElementDto reset = Contact("LS2");
        LogicBlockDto latch = Network(new LogicElementDto
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.Sr,
            Inputs = { set, reset }
        });

        Assert.Equal(LogicBlockStates.Ready, Evaluate(latch, states, Now).State);

        // The set input drops and the reset stays false: the latch holds what it had.
        LogicBlockStateDto held = LogicStateEvaluator.EvaluateBlock(
            latch,
            Values(("sim", "PS1", false, true), ("sim", "LS2", false, true)),
            Mappings(),
            states,
            Now.AddSeconds(1));
        Assert.Equal(LogicBlockStates.Ready, held.State);

        // The reset wins: the latch drops.
        LogicBlockStateDto reset2 = LogicStateEvaluator.EvaluateBlock(
            latch,
            Values(("sim", "PS1", false, true), ("sim", "LS2", true, true)),
            Mappings(),
            states,
            Now.AddSeconds(2));
        Assert.Equal(LogicBlockStates.Blocked, reset2.State);

        // R_TRIG fires on the rising edge only.
        LogicStateStore edges = new();
        LogicElementDto edgeInput = Contact("PS1");
        LogicBlockDto edge = Network(new LogicElementDto
        {
            Id = Guid.NewGuid(),
            Kind = LogicElementKinds.RisingEdge,
            Inputs = { edgeInput }
        });

        // Low, then rising: the second evaluation sees the edge, the third does not.
        LogicBlockStateDto low = LogicStateEvaluator.EvaluateBlock(
            edge, Values(("sim", "PS1", false, true)), Mappings(), edges, Now);
        Assert.Equal(LogicBlockStates.Blocked, low.State);

        LogicBlockStateDto rising = LogicStateEvaluator.EvaluateBlock(
            edge, Values(("sim", "PS1", true, true)), Mappings(), edges, Now.AddSeconds(1));
        Assert.Equal(LogicBlockStates.Ready, rising.State);

        LogicBlockStateDto steady = LogicStateEvaluator.EvaluateBlock(
            edge, Values(("sim", "PS1", true, true)), Mappings(), edges, Now.AddSeconds(2));
        Assert.Equal(LogicBlockStates.Blocked, steady.State);
    }

    [Fact]
    public void KindHelpers_DescribeTheStandardSet()
    {
        Assert.True(LogicElementKinds.IsValid(LogicElementKinds.Ton));
        Assert.False(LogicElementKinds.IsValid("widget"));
        Assert.True(LogicElementKinds.IsGate(LogicElementKinds.Xor));
        Assert.True(LogicElementKinds.IsFunctionBlock(LogicElementKinds.Ctu));
        Assert.Equal((1, 1), LogicElementKinds.InputRange(LogicElementKinds.Not));
        Assert.Equal((2, 2), LogicElementKinds.InputRange(LogicElementKinds.Sr));
        Assert.Equal((1, 2), LogicElementKinds.InputRange(LogicElementKinds.Ctu));
        Assert.Equal((1, 1), LogicElementKinds.InputRange(LogicElementKinds.Ton));
    }
}
