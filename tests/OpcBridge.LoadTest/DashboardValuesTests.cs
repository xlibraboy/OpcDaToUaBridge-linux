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
}
