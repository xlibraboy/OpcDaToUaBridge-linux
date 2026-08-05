using Opc.Ua;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class BridgeNodeManagerTests
{
    [Theory]
    [InlineData("Read", AccessLevels.CurrentRead)]
    [InlineData("read", AccessLevels.CurrentRead)]
    [InlineData("", AccessLevels.CurrentRead)]
    [InlineData(null!, AccessLevels.CurrentRead)]
    [InlineData("Read-Write", AccessLevels.CurrentRead | AccessLevels.CurrentWrite)]
    [InlineData("read-write", AccessLevels.CurrentRead | AccessLevels.CurrentWrite)]
    [InlineData("Write", AccessLevels.CurrentWrite)]
    [InlineData("write", AccessLevels.CurrentWrite)]
    [InlineData("Bogus", AccessLevels.CurrentRead)]
    public void ToAccessLevel_MapsRightsToUaAccessLevel(string rights, byte expected)
    {
        byte actual = BridgeNodeManager.ToAccessLevel(rights);

        Assert.Equal(expected, actual);
    }

    public static TheoryData<string, NodeId> DataTypeCases() => new()
    {
        { "Auto", DataTypeIds.BaseDataType },
        { "Double", DataTypeIds.Double },
        { "REAL8", DataTypeIds.Double },
        { "Int32", DataTypeIds.Int32 },
        { "INT", DataTypeIds.Int32 },
        { "Int64", DataTypeIds.Int64 },
        { "LONG", DataTypeIds.Int64 },
        { "Boolean", DataTypeIds.Boolean },
        { "BOOL", DataTypeIds.Boolean },
        { "String", DataTypeIds.String },
        { "Byte", DataTypeIds.Byte },
        { "Int16", DataTypeIds.Int16 },
        { "SHORT", DataTypeIds.Int16 },
        { "Float", DataTypeIds.Float },
        { "SINGLE", DataTypeIds.Float },
        { "UnknownThing", DataTypeIds.BaseDataType }
    };

    [Theory]
    [MemberData(nameof(DataTypeCases))]
    public void ToDataTypeId_MapsDeclaredTypeToUaDataType(string dataType, NodeId expected)
    {
        NodeId actual = BridgeNodeManager.ToDataTypeId(dataType);

        Assert.Equal(expected, actual);
    }
}
