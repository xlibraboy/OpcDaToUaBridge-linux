using System.Text.Json;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class HmiTrendDtoTests
{
    [Fact]
    public void HmiTrendResponse_RoundTripsJson()
    {
        HmiTrendResponse original = new()
        {
            SourceId = "default",
            ItemId = "Random.Int1",
            FromUtc = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc),
            Truncated = true,
            Error = null,
            Points = new[]
            {
                new HmiTrendPoint
                {
                    T = new DateTime(2026, 7, 24, 0, 30, 0, DateTimeKind.Utc),
                    V = 12.5,
                    Q = 192,
                    Good = true
                }
            }
        };

        string json = JsonSerializer.Serialize(original);
        HmiTrendResponse? back = JsonSerializer.Deserialize<HmiTrendResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(back);
        Assert.Equal("default", back!.SourceId);
        Assert.Single(back.Points);
        Assert.Equal(12.5, ((JsonElement)back.Points[0].V!).GetDouble());
        Assert.True(back.Truncated);
    }
}
