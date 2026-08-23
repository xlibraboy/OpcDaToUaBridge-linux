using System.Text.Json;
using System.Text.Json.Serialization;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class CoreUaSubscriptionTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    [Fact]
    public void TagMapping_Subscription_DefaultsEmpty_AndRoundTrips()
    {
        TagMapping mapping = new() { SourceId = "ua-a", ItemId = "ns=2;s=Tag1" };
        Assert.Equal(string.Empty, mapping.Subscription);

        mapping.Subscription = "Fast";
        string json = JsonSerializer.Serialize(mapping, SerializerOptions);
        Assert.Contains("\"subscription\"", json);

        TagMapping? parsed = JsonSerializer.Deserialize<TagMapping>(json, SerializerOptions);
        Assert.NotNull(parsed);
        Assert.Equal("Fast", parsed!.Subscription);
    }

    [Fact]
    public void TagMapping_WithoutSubscriptionField_LoadsAsEmpty()
    {
        // Old payloads (pre-feature) must load unchanged.
        string legacyJson = "{\"SourceId\":\"ua-a\",\"ItemId\":\"ns=2;s=Tag1\"}";
        TagMapping? parsed = JsonSerializer.Deserialize<TagMapping>(legacyJson, SerializerOptions);
        Assert.NotNull(parsed);
        Assert.Equal(string.Empty, parsed!.Subscription);
    }

    [Fact]
    public void UaSubscriptionSettings_RecordEquality_IsCaseSensitiveOnName()
    {
        var a = new UaSubscriptionSettings("Fast", 250);
        var b = new UaSubscriptionSettings("Fast", 250);
        var c = new UaSubscriptionSettings("fast", 250);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
