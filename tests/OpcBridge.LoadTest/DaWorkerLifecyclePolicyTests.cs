using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaWorkerLifecyclePolicyTests
{
    [Fact]
    public void Backoff_GrowsToACap()
    {
        Assert.Equal(TimeSpan.Zero, WorkerLifecyclePolicy.BackoffForAttempt(0));
        Assert.Equal(TimeSpan.FromSeconds(1), WorkerLifecyclePolicy.BackoffForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(2), WorkerLifecyclePolicy.BackoffForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(5), WorkerLifecyclePolicy.BackoffForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(10), WorkerLifecyclePolicy.BackoffForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(30), WorkerLifecyclePolicy.BackoffForAttempt(5));
        Assert.Equal(TimeSpan.FromSeconds(30), WorkerLifecyclePolicy.BackoffForAttempt(50));
    }

    [Fact]
    public void Quarantine_AfterFiveCrashesInsideTheWindow()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        List<DateTime> crashes = Enumerable.Range(0, 5).Select(i => now.AddMinutes(-i)).ToList();

        Assert.True(WorkerLifecyclePolicy.ShouldQuarantine(crashes, now));
    }

    [Fact]
    public void Quarantine_NotBeforeFiveRecentCrashes()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        List<DateTime> four = Enumerable.Range(0, 4).Select(i => now.AddMinutes(-i)).ToList();
        List<DateTime> stale = new()
        {
            now.AddMinutes(-11),
            now.AddMinutes(-12),
            now.AddMinutes(-13),
            now.AddMinutes(-14),
            now.AddMinutes(-15)
        };

        Assert.False(WorkerLifecyclePolicy.ShouldQuarantine(four, now));
        Assert.False(WorkerLifecyclePolicy.ShouldQuarantine(stale, now));
    }

    [Theory]
    [InlineData(0, "clean-exit")]
    [InlineData(70, "parent-gone")]
    [InlineData(71, "bootstrap-error")]
    [InlineData(72, "protocol-error")]
    [InlineData(-1073741819, "crashed")]
    public void ClassifyExit_NamesTheOutcomes(int exitCode, string expected)
    {
        Assert.Equal(expected, WorkerLifecyclePolicy.ClassifyExit(exitCode));
    }
}
