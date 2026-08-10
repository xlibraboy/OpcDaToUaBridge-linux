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
        Assert.NotNull(source.OpcDa);
        Assert.Null(source.OpcUa);
        Assert.Null(source.Melsec);
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
        Assert.NotNull(source.OpcUa);
        Assert.Null(source.OpcDa);
        Assert.Null(source.Melsec);
    }

    [Fact]
    public void FromDto_OpcUa_NestedPreferred()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto
        {
            SourceId = "kep",
            SourceType = "OpcUa",
            UpdateRateMs = 1000,
            MaxMappedTags = 1000,
            OpcUa = new OpcUaSourceOptionsDto
            {
                EndpointUrl = "opc.tcp://nested:49320",
                SecurityMode = "None",
                SecurityPolicy = "None",
                SessionTimeoutMs = 30000,
                ReconnectDelayMs = 2000
            },
            // legacy flat should be ignored when nest present
            EndpointUrl = "opc.tcp://flat:1"
        }, 1000);

        Assert.Equal("opc.tcp://nested:49320", source.EndpointUrl);
        Assert.Equal(30000, source.SessionTimeoutMs);
        Assert.Equal(1000, source.MaxMappedTags);
        Assert.NotNull(source.OpcUa);
        Assert.Null(source.OpcDa);
    }

    [Fact]
    public void ToDto_PersistsNestedOnly()
    {
        var source = SourceConfigMigration.Normalize(new DaSourceRuntimeSettings(
            "kep",
            "Kepware",
            SourceTypes.OpcUa,
            1000,
            true,
            500,
            null,
            new OpcUaSourceOptions("opc.tcp://h:1", "None", "None", null, null, 60000, 5000),
            null,
            null,
            null), 1000);

        SourceConfigDto dto = SourceConfigMigration.ToDto(source);
        Assert.NotNull(dto.OpcUa);
        Assert.Equal("opc.tcp://h:1", dto.OpcUa!.EndpointUrl);
        Assert.Null(dto.OpcDa);
        Assert.Null(dto.Melsec);
        Assert.True(string.IsNullOrEmpty(dto.ProgId));
        Assert.True(string.IsNullOrEmpty(dto.EndpointUrl));
        Assert.Equal(500, dto.MaxMappedTags);
    }
}
