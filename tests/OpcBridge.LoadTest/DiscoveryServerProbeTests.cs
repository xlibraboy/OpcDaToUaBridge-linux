using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The discovery-server probe backs a dashboard warning, so it has to name a running LDS, stay
/// silent when there is none, and must not walk the process list on every one-second poll of
/// /api/status/ports.
/// </summary>
public sealed class DiscoveryServerProbeTests
{
    [Fact]
    public void Detect_ReturnsNull_WhenNoKnownDiscoveryServerRuns()
    {
        DiscoveryServerProbe probe = new(new ManualClock(), _ => false);

        Assert.Null(probe.Detect());
    }

    [Fact]
    public void Detect_NamesTheDiscoveryServer_WhenOneRuns()
    {
        DiscoveryServerProbe probe = new(new ManualClock(), name => name == "opcualds");

        Assert.Equal("opcualds", probe.Detect());
    }

    [Fact]
    public void Detect_ScansTheProcessListAtMostOncePerCacheWindow()
    {
        ManualClock clock = new();
        int scans = 0;
        DiscoveryServerProbe probe = new(clock, _ => { scans++; return false; }, TimeSpan.FromSeconds(30));

        probe.Detect();
        probe.Detect();
        probe.Detect();
        Assert.Equal(1, scans);

        clock.Advance(TimeSpan.FromSeconds(29));
        probe.Detect();
        Assert.Equal(1, scans);

        clock.Advance(TimeSpan.FromSeconds(2));
        probe.Detect();
        Assert.Equal(2, scans);
    }

    [Fact]
    public void Detect_ReplacesACachedHit_OnceTheWindowElapses()
    {
        ManualClock clock = new();
        bool running = true;
        DiscoveryServerProbe probe = new(clock, _ => running, TimeSpan.FromSeconds(5));

        Assert.Equal("opcualds", probe.Detect());

        running = false;
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Null(probe.Detect());
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now_ = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now_;

        public void Advance(TimeSpan by) => now_ += by;
    }
}
