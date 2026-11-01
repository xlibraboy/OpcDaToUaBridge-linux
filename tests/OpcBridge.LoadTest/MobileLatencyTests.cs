using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The latency state is a real round-trip to the bridge's public /health: the value is the
/// measured milliseconds, and the state is the good/fair/poor band around it.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class MobileLatencyTests
{
    [Theory]
    [InlineData(0, LatencyState.Good)]
    [InlineData(50, LatencyState.Good)]
    [InlineData(51, LatencyState.Fair)]
    [InlineData(150, LatencyState.Fair)]
    [InlineData(151, LatencyState.Poor)]
    public void Classify_UsesTheGoodAndFairBands(int milliseconds, LatencyState expected)
    {
        Assert.Equal(expected, LatencyGrade.Classify(milliseconds));
    }

    [Fact]
    public void Text_AlwaysCarriesTheMeasuredValue()
    {
        Assert.Equal("good · 12 ms", LatencyGrade.Text(LatencyState.Good, 12));
        Assert.Equal("fair · 87 ms", LatencyGrade.Text(LatencyState.Fair, 87));
        Assert.Equal("poor · 210 ms", LatencyGrade.Text(LatencyState.Poor, 210));
        Assert.Equal("offline", LatencyGrade.Text(LatencyState.Offline, null));
        Assert.Equal("latency —", LatencyGrade.Text(LatencyState.Unknown, null));
    }

    [Fact]
    public async Task Client_MeasuresTheRoundTripToABridge()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(static _ => { });
        using LogicApiClient client = new();
        client.SetBaseAddress(app.Client.BaseAddress!.ToString());

        MobileResult<int> first = await client.MeasureLatencyAsync(CancellationToken.None);
        MobileResult<int> second = await client.MeasureLatencyAsync(CancellationToken.None);

        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.InRange(first.Value, 0, 5000);
        Assert.InRange(second.Value, 0, 5000);
    }

    [Fact]
    public async Task Client_ReportsAnUnreachableBridgeAsNoMeasurement()
    {
        using LogicApiClient client = new();
        client.SetBaseAddress("http://127.0.0.1:1");

        MobileResult<int> latency = await client.MeasureLatencyAsync(CancellationToken.None);

        Assert.False(latency.Ok);
        Assert.NotNull(latency.Error);
    }

    [Fact]
    public async Task Client_WithoutAnAddress_ReportsNoMeasurement()
    {
        using LogicApiClient client = new();

        MobileResult<int> latency = await client.MeasureLatencyAsync(CancellationToken.None);

        Assert.False(latency.Ok);
        Assert.Equal("no bridge address", latency.Error);
    }
}
