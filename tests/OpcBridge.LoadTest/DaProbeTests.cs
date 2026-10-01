using System.Text.Json;
using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The probe's cross-process JSON contract: the parent and the child must agree on the request
/// and on reading the child's last stdout line, because a mismatch reads as "probe died".
/// The spawn itself is Windows/Lab-only and not exercised here.
/// </summary>
public sealed class DaProbeTests
{
    [Fact]
    public void Request_RoundTripsThroughTheProbeJsonOptions()
    {
        DaProbeRequest request = new("PMD.DDT_OPCDataServer.1", "10.0.0.5", "mesadm1", "secret", "PRW11709");

        string json = JsonSerializer.Serialize(request, DaProbe.JsonOptions);
        DaProbeRequest? parsed = JsonSerializer.Deserialize<DaProbeRequest>(json, DaProbe.JsonOptions);

        Assert.Equal(request, parsed);
        Assert.Contains("\"progId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseLastJsonLine_ReturnsTheLastParsableLine()
    {
        string output = "some noise\n{\"ok\":true,\"activated\":true,\"serverInfo\":\"PMD Data Access\"}\n";

        DaProbeResult? result = DaProbe.ParseLastJsonLine(output);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.True(result.Activated);
        Assert.Equal("PMD Data Access", result.ServerInfo);
    }

    [Fact]
    public void ParseLastJsonLine_WithoutJson_ReturnsNull()
    {
        Assert.Null(DaProbe.ParseLastJsonLine(string.Empty));
        Assert.Null(DaProbe.ParseLastJsonLine("not json\n{broken\n"));
    }

    [Fact]
    public void Result_ParsesFailureShapeWithHresultAndDied()
    {
        string json =
            "{\"ok\":false,\"activated\":false,\"error\":\"Class not registered\"," +
            "\"hresult\":-2147221164,\"classification\":\"class-not-registered\",\"died\":true}";

        DaProbeResult? result = DaProbe.ParseLastJsonLine(json);

        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.True(result.Died);
        Assert.Equal(unchecked((int)0x80040154), result.HResult);
        Assert.Equal("class-not-registered", result.Classification);
    }

    [Fact]
    public void IsProbeInvocation_MatchesOnlyTheExactSwitch()
    {
        Assert.True(DaProbe.IsProbeInvocation(new[] { "--da-probe" }));
        Assert.False(DaProbe.IsProbeInvocation(Array.Empty<string>()));
        Assert.False(DaProbe.IsProbeInvocation(new[] { "--da-probe", "extra" }));
        Assert.False(DaProbe.IsProbeInvocation(new[] { "--other" }));
    }
}
