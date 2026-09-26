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

    [Fact]
    public void RootFolder_IsProtocolNeutral()
    {
        // Issue #13: the root folder must not present the bridge as OPC-DA-only —
        // sources can be OPC DA, OPC UA, Melsec or S7-200.
        Assert.Equal("BridgeTags", BridgeNodeManager.RootFolderPath);
        Assert.Equal("Bridge Tags", BridgeNodeManager.RootFolderDisplayName);
    }

    [Fact]
    public void MeasureNotificationBytes_UsesTheEncodedPayloadSize()
    {
        // Issue #19: bandwidth is measured by encoding the actual notification payload,
        // not assumed at ~80 bytes per notification. A small value must come out well
        // below that guess, and a long string must grow the measurement by its own length.
        ServiceMessageContext context = new(DefaultTelemetry.Create(_ => { }));
        DateTime timestamp = DateTime.UtcNow;

        int small = BridgeNodeManager.MeasureNotificationBytes(
            context, new Variant(1), StatusCodes.Good, timestamp);
        int large = BridgeNodeManager.MeasureNotificationBytes(
            context, new Variant(new string('x', 512)), StatusCodes.Good, timestamp);

        Assert.True(small is > 0 and < 80, $"expected an Int32 payload below the old 80-byte guess, got {small}");
        Assert.True(large >= small + 512, $"expected the string payload to carry its 512 bytes, got {large} vs {small}");
    }
}
