using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class InterlinkStatusTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    private static InterlinkStatusInput Input(
        bool enabled = true,
        bool providerHasValue = true,
        bool providerGood = true,
        bool consumerSourceConnected = true,
        long attempts = 0,
        long failures = 0,
        DateTime? lastForwardUtc = null,
        bool? lastWriteSuccess = null,
        string? lastError = null)
    {
        return new InterlinkStatusInput(
            enabled,
            providerHasValue,
            providerGood,
            consumerSourceConnected,
            attempts,
            failures,
            lastForwardUtc,
            lastWriteSuccess,
            lastError,
            Now);
    }

    [Fact]
    public void Derive_Flowing_WhenRecentForwardSucceeded()
    {
        var input = Input(
            attempts: 5,
            failures: 0,
            lastForwardUtc: Now.AddSeconds(-3),
            lastWriteSuccess: true);

        var health = InterlinkStatusEvaluator.Derive(input, out string? reason);

        Assert.Equal(InterlinkHealth.Flowing, health);
        Assert.Null(reason);
    }

    [Fact]
    public void Derive_Idle_WhenHealthyButNoRecentForward()
    {
        var input = Input(
            attempts: 2,
            lastForwardUtc: Now.AddSeconds(-120),
            lastWriteSuccess: true);

        var health = InterlinkStatusEvaluator.Derive(input, out string? reason);

        Assert.Equal(InterlinkHealth.Idle, health);
        Assert.Equal("no recent provider changes", reason);
    }

    [Fact]
    public void Derive_Idle_WhenNeverForwardedYet()
    {
        var input = Input();

        var health = InterlinkStatusEvaluator.Derive(input, out string? reason);

        Assert.Equal(InterlinkHealth.Idle, health);
    }

    [Fact]
    public void Derive_Waiting_WhenConsumerSourceDisconnected()
    {
        var input = Input(consumerSourceConnected: false, attempts: 4, lastWriteSuccess: true);

        var health = InterlinkStatusEvaluator.Derive(input, out string? reason);

        Assert.Equal(InterlinkHealth.Waiting, health);
        Assert.Contains("disconnected", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Derive_Waiting_WhenProviderHasNoValueYet()
    {
        var input = Input(providerHasValue: false);

        var health = InterlinkStatusEvaluator.Derive(input, out _);

        Assert.Equal(InterlinkHealth.Waiting, health);
    }

    [Fact]
    public void Derive_Waiting_WhenProviderBadQuality()
    {
        var input = Input(providerGood: false, attempts: 3, lastWriteSuccess: true);

        var health = InterlinkStatusEvaluator.Derive(input, out string? reason);

        Assert.Equal(InterlinkHealth.Waiting, health);
        Assert.Contains("quality", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Derive_Waiting_WhenLinkDisabled()
    {
        var input = Input(enabled: false, attempts: 1, lastWriteSuccess: true);

        var health = InterlinkStatusEvaluator.Derive(input, out string? reason);

        Assert.Equal(InterlinkHealth.Waiting, health);
        Assert.Contains("disabled", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Derive_WriteFailed_TakesPriorityOverFlowing()
    {
        // A failed write inside the flow window must surface as WriteFailed,
        // not hide behind a green Flowing badge.
        var input = Input(
            attempts: 6,
            failures: 1,
            lastForwardUtc: Now.AddSeconds(-2),
            lastWriteSuccess: false,
            lastError: "Bad write quality");

        var health = InterlinkStatusEvaluator.Derive(input, out string? reason);

        Assert.Equal(InterlinkHealth.WriteFailed, health);
        Assert.Equal("Bad write quality", reason);
    }

    [Fact]
    public void Derive_WriteFailed_EvenOutsideFlowWindow()
    {
        var input = Input(
            attempts: 2,
            failures: 1,
            lastForwardUtc: Now.AddSeconds(-300),
            lastWriteSuccess: false,
            lastError: "timeout");

        var health = InterlinkStatusEvaluator.Derive(input, out string? reason);

        Assert.Equal(InterlinkHealth.WriteFailed, health);
        Assert.Equal("timeout", reason);
    }

    [Fact]
    public void RecordLinkForward_UpdatesCountersAndOutcome()
    {
        BridgeState state = new(Microsoft.Extensions.Options.Options.Create(new OpcBridge.Core.BridgeOptions()));

        string consumerSid = "consA", consumerItem = "itemC";
        state.RecordLinkForward(consumerSid, consumerItem, success: true, error: null);
        state.RecordLinkForward(consumerSid.ToUpperInvariant(), " " + consumerItem + " ", success: false, error: "write rejected");

        var stats = Assert.Single(state.GetLinkStats()).Value;
        Assert.Equal(2, stats.Attempts);
        Assert.Equal(1, stats.Successes);
        Assert.Equal(1, stats.Failures);
        Assert.False(stats.LastWriteSuccess);
        Assert.Equal("write rejected", stats.LastError);
        Assert.NotNull(stats.LastForwardUtc);
    }
}
