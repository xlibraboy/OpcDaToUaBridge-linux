using OpcBridge.Client;
using OpcBridge.Hmi.ViewModels.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The network's auto format: the levels a flat list of rows is given by its kinds alone — one
/// click instead of an indent click on every row.
/// </summary>
public sealed class LogicNetworkLayoutTests
{
    private const string C = LogicElementKinds.Contact;

    [Fact]
    public void Contacts_StayRoots() =>
        Assert.Equal(new[] { 0, 0, 0 }, LogicNetworkLayout.Levels(new[] { C, C, C }));

    [Fact]
    public void NothingToLayOut_IsNoLevels() =>
        Assert.Empty(LogicNetworkLayout.Levels(Array.Empty<string>()));

    [Fact]
    public void EveryKind_IsGivenTheInputsItReads()
    {
        // The Primary Arm Down example as it is authored — a contact, a NOT, an XOR and a CTU one
        // after another, each with its operand written behind it — lays out as the block is stored:
        // the contact a root, the NOT's contact under it, the two XOR contacts under the XOR, the
        // CTU's contact under the CTU.
        int[] levels = LogicNetworkLayout.Levels(new[]
        {
            C,
            LogicElementKinds.Not, C,
            LogicElementKinds.Xor, C, C,
            LogicElementKinds.Ctu, C
        });

        Assert.Equal(new[] { 0, 0, 1, 0, 1, 1, 0, 1 }, levels);
    }

    [Fact]
    public void AGate_TakesTheContactsBehindIt()
    {
        // An OR reads any number: the two it takes to be a gate, then the contacts that follow —
        // they belong to the gate, and standing beside it as roots they would be ANDed in instead.
        Assert.Equal(
            new[] { 0, 1, 1, 1 },
            LogicNetworkLayout.Levels(new[] { LogicElementKinds.Or, C, C, C }));
    }

    [Fact]
    public void AGate_TakesAGateThatNeedsInputsAsOneInput()
    {
        // OR(C1, AND(C2, C3)): the gate written behind the OR is one input of it, and its own
        // inputs nest under it in turn.
        Assert.Equal(
            new[] { 0, 1, 1, 2, 2 },
            LogicNetworkLayout.Levels(new[] { LogicElementKinds.Or, C, LogicElementKinds.And, C, C }));
    }

    [Fact]
    public void AGate_StopsAtARowThatReadsInputsOfItsOwn_OnceItHasItsTwo()
    {
        // OR(C1, C2) ∧ TON(C3): the timer is not a contact, so the gate does not take it — it stays
        // a root beside the gate, which is what an author means by writing it there (a gate that
        // took it in would read OR(C1, C2, TON(C3)), which is not the same thing for an OR or an
        // XOR).
        Assert.Equal(
            new[] { 0, 1, 1, 0, 1 },
            LogicNetworkLayout.Levels(new[] { LogicElementKinds.Or, C, C, LogicElementKinds.Ton, C }));
    }

    [Fact]
    public void AnAnd_TakesEverythingBehindIt()
    {
        // AND(C1, OR(C2, C3), TON(C4)) — an AND written first takes the whole chain behind it.
        // It may do so safely: an AND is associative, so a row taken in as its input is the same
        // conjunction as the row standing beside it as a root, and the block ANDs its roots anyway.
        Assert.Equal(
            new[] { 0, 1, 1, 2, 2, 1, 2 },
            LogicNetworkLayout.Levels(new[]
            {
                LogicElementKinds.And, C, LogicElementKinds.Or, C, C, LogicElementKinds.Ton, C
            }));
    }

    [Fact]
    public void ALatchAndACounter_KeepTheirSecondInputForTheAuthor()
    {
        // A counter's reset and a latch's second input are deliberate: the layout gives the row the
        // one input it reads and leaves the row behind it where it stands.
        Assert.Equal(new[] { 0, 1, 0 }, LogicNetworkLayout.Levels(new[] { LogicElementKinds.Ctu, C, C }));
        Assert.Equal(new[] { 0, 1, 0 }, LogicNetworkLayout.Levels(new[] { LogicElementKinds.Sr, C, C }));
    }

    [Fact]
    public void RowsNobodyTakes_StayRoots()
    {
        // A row that needs inputs but has none behind it is left unwired — the editor's tolerance
        // for a half-built network, reported as (not wired yet), never as satisfied.
        Assert.Equal(new[] { 0, 0 }, LogicNetworkLayout.Levels(new[] { C, LogicElementKinds.Xor }));
        Assert.Equal(new[] { 0, 1 }, LogicNetworkLayout.Levels(new[] { LogicElementKinds.Xor, C }));
    }

    [Fact]
    public void DeeperThanTheSharedLimit_StopsAtTheLimit()
    {
        // A chain of NOTs nests one level per row; the deepest levels clamp at the depth the store
        // accepts, so a saved network is never refused for being too deep.
        string[] kinds = Enumerable.Repeat(LogicElementKinds.Not, 20).Append(C).ToArray();

        int[] levels = LogicNetworkLayout.Levels(kinds);

        Assert.Equal(kinds.Length, levels.Length);
        Assert.Equal(LogicLimits.MaxElementDepth, levels[LogicLimits.MaxElementDepth]);
        Assert.All(levels, level => Assert.InRange(level, 0, LogicLimits.MaxElementDepth));
        Assert.Equal(
            Enumerable.Range(0, LogicLimits.MaxElementDepth + 1)
                .Concat(Enumerable.Repeat(LogicLimits.MaxElementDepth, 4)),
            levels);
    }

    [Fact]
    public void TheBridgesOwnExpansion_IsAlreadyLaidOut()
    {
        // A network the bridge itself builds (the flat conditions expanded: a wrapper AND, an OR
        // per group, a TON per hold) must come back from the layout exactly as it went in — the
        // button has to be safe to press on a block that was converted and saved.
        List<LogicElementDto> network = LogicNetwork.Expand(new List<LogicConditionDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Text = "Line 01 start permit must be given",
                SourceId = "sim",
                ItemId = "Permit",
                Op = LogicConditionOps.On
            },
            new()
            {
                Id = Guid.NewGuid(),
                Text = "Local start pressed",
                SourceId = "sim",
                ItemId = "Local",
                Op = LogicConditionOps.On,
                Group = "Start permissive"
            },
            new()
            {
                Id = Guid.NewGuid(),
                Text = "Remote start pressed",
                SourceId = "sim",
                ItemId = "Remote",
                Op = LogicConditionOps.On,
                Group = "Start permissive"
            },
            new()
            {
                Id = Guid.NewGuid(),
                Text = "Level above 50 %",
                SourceId = "sim",
                ItemId = "Level",
                Op = LogicConditionOps.GreaterThan,
                Value = 50,
                HoldMs = 3000
            }
        });

        List<(LogicElementDto Element, int Depth)> walked = LogicNetwork.Walk(network);
        int[] levels = LogicNetworkLayout.Levels(walked.Select(node => node.Element.Kind).ToArray());

        Assert.Equal(7, walked.Count);                                    // AND + contact + OR + 2 + TON + contact
        Assert.Equal(walked.Select(node => node.Depth), levels);
    }
}
