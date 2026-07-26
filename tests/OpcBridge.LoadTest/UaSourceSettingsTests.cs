using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class UaSourceSettingsTests
{
    [Fact]
    public void LoadFromDisk_MissingSourceType_DefaultsToOpcDa()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "line1",
            ProgId = "X",
            Host = "h",
            UpdateRateMs = 500
        }, defaultUpdateRate: 1000);

        Assert.Equal(SourceTypes.OpcDa, source.SourceType);
        Assert.Equal("X", source.ProgId);
        Assert.Equal("", source.EndpointUrl);
        Assert.Equal(50000, source.MaxMappedTags);
    }

    [Fact]
    public void FromDto_OpcUa_RequiresEndpointFields()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "kep",
            SourceType = "OpcUa",
            EndpointUrl = "opc.tcp://kepware:49320",
            SecurityMode = "SignAndEncrypt",
            SecurityPolicy = "Basic256Sha256",
            UpdateRateMs = 1000
        }, 1000);

        Assert.Equal(SourceTypes.OpcUa, source.SourceType);
        Assert.Equal("opc.tcp://kepware:49320", source.EndpointUrl);
        Assert.Equal("SignAndEncrypt", source.SecurityMode);
    }
}
