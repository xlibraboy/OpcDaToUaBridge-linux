using OpcBridge.Influx;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class InfluxFluxTrendQueryTests
{
    [Fact]
    public void BuildFlux_IncludesBucketMeasurementAndTagFilters()
    {
        DateTime from = new(2026, 7, 24, 20, 0, 0, DateTimeKind.Utc);
        DateTime to = new(2026, 7, 24, 21, 0, 0, DateTimeKind.Utc);

        string flux = InfluxFluxTrendQuery.BuildFlux(
            "bridge_trends",
            "opc_tags",
            "default",
            "Random.Int1",
            from,
            to,
            500);

        Assert.Contains("bridge_trends", flux, StringComparison.Ordinal);
        Assert.Contains("opc_tags", flux, StringComparison.Ordinal);
        Assert.Contains("source_id == \"default\"", flux, StringComparison.Ordinal);
        Assert.Contains("da_item_id == \"Random.Int1\"", flux, StringComparison.Ordinal);
        Assert.Contains("limit(n: 500)", flux, StringComparison.Ordinal);
        Assert.Contains("_field == \"value\"", flux, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeFluxString_EscapesQuotesAndBackslashes()
    {
        string escaped = InfluxFluxTrendQuery.EscapeFluxString("a\"b\\c");
        Assert.Equal("a\\\"b\\\\c", escaped);
    }
}
