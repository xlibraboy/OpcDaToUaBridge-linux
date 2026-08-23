// tests/OpcBridge.LoadTest/UaSubscriptionPlanTests.cs
using OpcBridge.Core;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class UaSubscriptionPlanTests
{
    private static readonly UaSubscriptionSettings[] Subs =
    {
        new("Fast", 250),
        new("Slow", 5000)
    };

    private static TagMapping Map(
        string itemId,
        string subscription = "",
        int pollRateMs = 0,
        bool enabled = true,
        string mode = TagMode.Source,
        string accessRights = TagAccessRights.Read)
        => new()
        {
            SourceId = "ua-a",
            ItemId = itemId,
            Subscription = subscription,
            PollRateMs = pollRateMs,
            Enabled = enabled,
            Mode = mode,
            AccessRights = accessRights
        };

    [Fact]
    public void AssignedTags_GoToTheirNamedBucket_AtBucketRate()
    {
        var mappings = new List<TagMapping>
        {
            Map("ns=2;s=A", subscription: "FAST"),   // case-insensitive
            Map("ns=2;s=B", subscription: "slow")
        };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, Subs, defaultSamplingMs: 1000);

        Assert.Equal(250, plan["Fast"]["ns=2;s=A"]);
        Assert.Equal(5000, plan["Slow"]["ns=2;s=B"]);
        Assert.False(plan.ContainsKey(UaSubscriptionPlan.DefaultBucketKey));
    }

    [Fact]
    public void UnassignedAndUnknownTags_FallBackToDefaultBucket_WithLegacyRates()
    {
        var mappings = new List<TagMapping>
        {
            Map("ns=2;s=D1"),                          // default rate
            Map("ns=2;s=D2", pollRateMs: 400),         // per-tag override still wins in default
            Map("ns=2;s=X", subscription: "Ghost")     // unknown sub -> default
        };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, Subs, defaultSamplingMs: 1000);

        Assert.Single(plan);
        Dictionary<string, int> defaultBucket = plan[UaSubscriptionPlan.DefaultBucketKey];
        Assert.Equal(1000, defaultBucket["ns=2;s=D1"]);
        Assert.Equal(400, defaultBucket["ns=2;s=D2"]);
        Assert.Equal(1000, defaultBucket["ns=2;s=X"]);
    }

    [Fact]
    public void Filters_ParityWithBuildDesiredSampling()
    {
        var mappings = new List<TagMapping>
        {
            Map("ns=2;s=Off", enabled: false),
            Map("ns=2;s=Man", mode: TagMode.Manual),
            Map("   "),                                     // empty itemId
            Map("ns=2;s=W", accessRights: TagAccessRights.Write), // write-only not source-read
            Map("ns=2;s=Ok", subscription: "Fast")
        };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, Subs, defaultSamplingMs: 1000);

        Assert.False(plan.ContainsKey(UaSubscriptionPlan.DefaultBucketKey));
        Assert.Equal(new[] { "ns=2;s=Ok" }, plan["Fast"].Keys.ToArray());
    }

    [Fact]
    public void NullSubscriptions_AllTagsGoToDefault_LegacyShape()
    {
        var mappings = new List<TagMapping> { Map("ns=2;s=A", pollRateMs: 300), Map("ns=2;s=B") };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, null, defaultSamplingMs: 1000);

        Assert.Single(plan);
        Assert.Equal(300, plan[""][ "ns=2;s=A"]);
        Assert.Equal(1000, plan[""]["ns=2;s=B"]);
    }

    [Fact]
    public void DuplicateNodeIds_FirstWins_PerBucket()
    {
        var mappings = new List<TagMapping>
        {
            Map("ns=2;s=A", subscription: "Fast"),
            Map(" ns=2;s=A ", subscription: "Fast", pollRateMs: 999) // same node after trim
        };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, Subs, defaultSamplingMs: 1000);

        Assert.Equal(250, plan["Fast"]["ns=2;s=A"]); // first wins; named bucket ignores PollRateMs
    }
}
