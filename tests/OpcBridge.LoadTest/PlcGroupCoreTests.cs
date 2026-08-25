using System.Text.Json;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// PLC group core data types: record shape, JSON contract name on TagMapping, and
/// default-empty semantics (unassigned tags ride the source default bucket — spec §4).
/// </summary>
public sealed class PlcGroupCoreTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PlcGroupSettings_ExposesNameAndRate()
    {
        PlcGroupSettings group = new("Fast", 250);
        Assert.Equal("Fast", group.Name);
        Assert.Equal(250, group.UpdateRateMs);
    }

    [Fact]
    public void TagMapping_PlcGroup_DefaultsEmpty_AndRoundTripsJsonProperty()
    {
        TagMapping mapping = new() { SourceId = "mx1", ItemId = "D100", UaNodeId = "ns=2;s=D100" };
        Assert.Equal(string.Empty, mapping.PlcGroup);

        mapping.PlcGroup = "Fast";
        string json = JsonSerializer.Serialize(mapping, SerializerOptions);
        Assert.Contains("\"plcGroup\":\"Fast\"", json);

        TagMapping parsed = JsonSerializer.Deserialize<TagMapping>(json, SerializerOptions)!;
        Assert.Equal("Fast", parsed.PlcGroup);
    }

    [Fact]
    public void TagMapping_Deserialize_WithoutPlcGroup_DefaultsEmpty()
    {
        TagMapping parsed = JsonSerializer.Deserialize<TagMapping>(
            "{\"sourceId\":\"mx1\",\"itemId\":\"D100\",\"uaNodeId\":\"ns=2;s=D100\"}", SerializerOptions)!;
        Assert.Equal(string.Empty, parsed.PlcGroup);
    }
}
