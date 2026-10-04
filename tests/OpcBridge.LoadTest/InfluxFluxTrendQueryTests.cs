using InfluxDB.Client.Core.Flux.Domain;
using OpcBridge.Client;
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
        Assert.Contains("_field == \"value_int\"", flux, StringComparison.Ordinal);
        Assert.Contains("_field == \"value_bool\"", flux, StringComparison.Ordinal);
        Assert.Contains("_field == \"value_str\"", flux, StringComparison.Ordinal);
    }

    [Fact]
    public void MapTables_ReadsEveryTypeStableValueField()
    {
        FluxTable table = new();
        table.Records.Add(Record("value_int", 42L, 1));
        table.Records.Add(Record("value_bool", true, 2));
        table.Records.Add(Record("value_str", "hello", 3));
        table.Records.Add(Record("value", 1.5, 4));

        List<HmiTrendPoint> points = InfluxFluxTrendQuery.MapTables([table]);

        Assert.Equal(4, points.Count);
        Assert.Equal(42L, points[0].V);
        Assert.Equal(true, points[1].V);
        Assert.Equal("hello", points[2].V);
        Assert.Equal(1.5, points[3].V);
        Assert.All(points, p => Assert.Equal(192, p.Q));
        Assert.All(points, p => Assert.True(p.Good));
    }

    private static FluxRecord Record(string field, object value, int seconds)
    {
        FluxRecord record = new(0);
        record.Values["_time"] = new DateTime(2026, 1, 1, 0, 0, seconds, DateTimeKind.Utc);
        record.Values[field] = value;
        record.Values["quality"] = 192L;
        record.Values["is_good"] = true;
        return record;
    }

    [Fact]
    public void EscapeFluxString_EscapesQuotesAndBackslashes()
    {
        string escaped = InfluxFluxTrendQuery.EscapeFluxString("a\"b\\c");
        Assert.Equal("a\\\"b\\\\c", escaped);
    }
}
