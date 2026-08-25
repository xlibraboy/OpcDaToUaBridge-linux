using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DashboardValuesTests
{
    private static TagMapping Mapping(string sourceId, string itemId, string dataType) => new()
    {
        SourceId = sourceId,
        ItemId = itemId,
        DataType = dataType
    };

    [Fact]
    public void Lookup_ReturnsMappedDataTypePerTag()
    {
        var lookup = DashboardValues.BuildDataTypeLookup(new[]
        {
            Mapping("ua-a", "Tag00001", "Double"),
            Mapping("ua-b", "Tag00001", "Int32")
        });

        Assert.Equal("Double", DashboardValues.LookupDataType(lookup, "ua-a", "Tag00001"));
        Assert.Equal("Int32", DashboardValues.LookupDataType(lookup, "ua-b", "Tag00001"));
    }

    [Fact]
    public void Lookup_IsCaseInsensitiveOnSourceAndItem()
    {
        var lookup = DashboardValues.BuildDataTypeLookup(new[] { Mapping("UA-A", "Tag00001", "Double") });

        Assert.Equal("Double", DashboardValues.LookupDataType(lookup, "ua-a", "tag00001"));
    }

    [Fact]
    public void Lookup_ToleratesSurroundingWhitespace()
    {
        var lookup = DashboardValues.BuildDataTypeLookup(new[] { Mapping(" ua-a ", " Tag00001 ", "Double") });

        Assert.Equal("Double", DashboardValues.LookupDataType(lookup, "ua-a", "Tag00001"));
    }

    [Fact]
    public void Lookup_ReturnsNullForUnknownTag()
    {
        var lookup = DashboardValues.BuildDataTypeLookup(new[] { Mapping("ua-a", "Tag00001", "Double") });

        Assert.Null(DashboardValues.LookupDataType(lookup, "ua-a", "Tag99999"));
        Assert.Null(DashboardValues.LookupDataType(lookup, "other", "Tag00001"));
    }

    public static TheoryData<object, string> ClrTypeCases => new()
    {
        { true, "Boolean" },
        { (sbyte)1, "SByte" },
        { (byte)1, "Byte" },
        { (short)1, "Int16" },
        { (ushort)1, "UInt16" },
        { 1, "Int32" },
        { 1u, "UInt32" },
        { 1L, "Int64" },
        { 1ul, "UInt64" },
        { 1.5f, "Float" },
        { 1.5d, "Double" },
        { 1.5m, "Decimal" },
        { "hello", "String" },
    };

    [Theory]
    [MemberData(nameof(ClrTypeCases))]
    public void InferDataType_MapsCommonClrTypes(object value, string expected)
    {
        Assert.Equal(expected, DashboardValues.InferDataType(value));
    }

    [Fact]
    public void InferDataType_MapsDateTimeAndByteArray()
    {
        Assert.Equal("DateTime", DashboardValues.InferDataType(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("ByteString", DashboardValues.InferDataType(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void InferDataType_ReturnsNullForNullAndUnknownTypes()
    {
        Assert.Null(DashboardValues.InferDataType(null));
        Assert.Null(DashboardValues.InferDataType(new object()));
        Assert.Null(DashboardValues.InferDataType(Guid.NewGuid()));
    }

    [Fact]
    public void ResolveDataType_RuntimeTypeWinsOverMappingType()
    {
        var lookup = DashboardValues.BuildDataTypeLookup(new[]
        {
            Mapping("ua-a", "Tag00001", "String") // configured differently than reality
        });

        // Source really sent an Int32: show Int32, not the configured String.
        Assert.Equal("Int32", DashboardValues.ResolveDataType(42, lookup, "ua-a", "Tag00001"));
    }

    [Fact]
    public void ResolveDataType_FallsBackToMappingTypeWhenValueHasNoRuntimeType()
    {
        var lookup = DashboardValues.BuildDataTypeLookup(new[] { Mapping("ua-a", "Tag00001", "Double") });

        Assert.Equal("Double", DashboardValues.ResolveDataType(null, lookup, "ua-a", "Tag00001"));
        Assert.Equal("Double", DashboardValues.ResolveDataType(new object(), lookup, "ua-a", "Tag00001"));
    }

    [Fact]
    public void ResolveDataType_ReturnsNullWhenNeitherKnown()
    {
        var lookup = DashboardValues.BuildDataTypeLookup(Array.Empty<TagMapping>());

        Assert.Null(DashboardValues.ResolveDataType(null, lookup, "ua-a", "Tag00001"));
    }

    [Fact]
    public void BuildUpdateRateLookup_SubscriptionAssignment_WinsOverPollRateAndDefault()
    {
        var subs = new Dictionary<string, IReadOnlyList<UaSubscriptionSettings>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ua-a"] = new List<UaSubscriptionSettings> { new("Fast", 250), new("Slow", 5000) }
        };
        var defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["ua-a"] = 1000 };
        var mappings = new List<TagMapping>
        {
            new() { SourceId = "ua-a", ItemId = "t1", Subscription = "fast", PollRateMs = 777 }, // sub wins
            new() { SourceId = "ua-a", ItemId = "t2", Subscription = "Ghost", PollRateMs = 333 }, // unknown -> poll rate
            new() { SourceId = "ua-a", ItemId = "t3" }                                            // default
        };

        var lookup = DashboardValues.BuildUpdateRateLookup(mappings, defaults, subs);

        Assert.Equal(250, DashboardValues.LookupUpdateRate(lookup, "ua-a", "t1"));
        Assert.Equal(333, DashboardValues.LookupUpdateRate(lookup, "ua-a", "t2"));
        Assert.Equal(1000, DashboardValues.LookupUpdateRate(lookup, "ua-a", "t3"));
    }

    [Fact]
    public void BuildUpdateRateLookup_SubscriptionBelowMinimum_IsClampedTo100()
    {
        var subs = new Dictionary<string, IReadOnlyList<UaSubscriptionSettings>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ua-a"] = new List<UaSubscriptionSettings> { new("Turbo", 50) }
        };
        var defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["ua-a"] = 1000 };
        var mappings = new List<TagMapping>
        {
            new() { SourceId = "ua-a", ItemId = "t1", Subscription = "turbo" } // assigned sub wins, clamped up
        };

        var lookup = DashboardValues.BuildUpdateRateLookup(mappings, defaults, subs);

        Assert.Equal(100, DashboardValues.LookupUpdateRate(lookup, "ua-a", "t1"));
    }

    [Fact]
    public void BuildUpdateRateLookup_PlcGroupWinsOverPerTagRate_UnknownFallsThrough()
    {
        var mappings = new[]
        {
            new TagMapping { SourceId = "mx1", ItemId = "D100", PlcGroup = "Fast", PollRateMs = 999 },
            new TagMapping { SourceId = "mx1", ItemId = "D101", PlcGroup = "Ghost", PollRateMs = 750 },
            new TagMapping { SourceId = "mx1", ItemId = "D102", PollRateMs = 0 }
        };
        var sourceRates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mx1"] = 2000 };
        var mxGroups = new Dictionary<string, IReadOnlyList<PlcGroupSettings>>(StringComparer.OrdinalIgnoreCase)
        {
            ["mx1"] = new[] { new PlcGroupSettings("Fast", 250) }
        };
        var uaSubs = new Dictionary<string, IReadOnlyList<UaSubscriptionSettings>>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, int> lookup = DashboardValues.BuildUpdateRateLookup(
            mappings, sourceRates, uaSubs,
            sourceId => mxGroups.TryGetValue(sourceId, out IReadOnlyList<PlcGroupSettings>? groups)
                ? groups
                : Array.Empty<PlcGroupSettings>());

        Assert.Equal(250, DashboardValues.LookupUpdateRate(lookup, "mx1", "D100")); // group wins
        Assert.Equal(750, DashboardValues.LookupUpdateRate(lookup, "mx1", "D101")); // unknown -> per-tag
        Assert.Equal(2000, DashboardValues.LookupUpdateRate(lookup, "mx1", "D102")); // default
    }
}
