using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The post-reconnect snapshot refresh must not be one swallowed attempt: it retries on its
/// schedule and reports honestly whether the values are fresh again.
/// </summary>
public sealed class SnapshotRefreshTests
{
    [Fact]
    public async Task SucceedsOnFirstAttempt_WithoutRetrying()
    {
        int attempts = 0;

        bool ok = await SnapshotRefresh.TryAsync(
            _ =>
            {
                attempts++;
                return Task.CompletedTask;
            },
            [TimeSpan.FromSeconds(30)],
            CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetriesUntilAnAttemptSucceeds()
    {
        int attempts = 0;

        bool ok = await SnapshotRefresh.TryAsync(
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException(new InvalidOperationException("boom"))
                    : Task.CompletedTask;
            },
            [TimeSpan.Zero, TimeSpan.Zero],
            CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task GivesUpAfterTheSchedule_AndReportsFailure()
    {
        int attempts = 0;

        bool ok = await SnapshotRefresh.TryAsync(
            _ =>
            {
                attempts++;
                return Task.FromException(new InvalidOperationException("boom"));
            },
            [TimeSpan.Zero, TimeSpan.Zero],
            CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SnapshotRefresh.TryAsync(_ => Task.CompletedTask, [TimeSpan.Zero], cts.Token));
    }
}
