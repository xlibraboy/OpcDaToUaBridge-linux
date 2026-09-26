using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// A poller's period is its configured rate: the delay after a cycle covers what is left of the
/// rate window, so a group whose read takes 400 ms of a 1000 ms window still samples every
/// second. Sleeping the full rate after the work stretched the real period to rate + cycle —
/// which is what aged healthy tags past the dashboard's freshness check and made a busy group
/// look half as fast as it was configured (#20).
/// </summary>
public sealed class PollCadenceTests
{
    [Fact]
    public void IdleGroup_WaitsTheWholeRate()
    {
        Assert.Equal(1000, BridgeWorker.NextPollDelayMs(1000, TimeSpan.Zero));
    }

    [Fact]
    public void BusyGroup_SleepsOnlyTheRemainderOfTheWindow()
    {
        Assert.Equal(800, BridgeWorker.NextPollDelayMs(1000, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(950, BridgeWorker.NextPollDelayMs(1000, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void GroupAtItsBudget_KeepsAGapInsteadOfSpinning()
    {
        Assert.Equal(100, BridgeWorker.NextPollDelayMs(1000, TimeSpan.FromMilliseconds(1000)));
    }

    [Fact]
    public void OverBudgetGroup_StillKeepsTheFloor()
    {
        // A read longer than the rate must not turn the poller into a back-to-back loop: the
        // source's write queue drains between cycles, and the meter reports the saturation.
        Assert.Equal(100, BridgeWorker.NextPollDelayMs(1000, TimeSpan.FromMilliseconds(2500)));
    }

    [Fact]
    public void SubSecondGroup_UsesATenthOfItsRateAsFloor()
    {
        Assert.Equal(60, BridgeWorker.NextPollDelayMs(100, TimeSpan.FromMilliseconds(40)));
        Assert.Equal(10, BridgeWorker.NextPollDelayMs(100, TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void NonPositiveRate_NeverDelays()
    {
        Assert.Equal(0, BridgeWorker.NextPollDelayMs(0, TimeSpan.FromMilliseconds(5)));
    }
}
