using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaWorkerOptionsValidatorTests
{
    [Theory]
    [InlineData(SourceTypes.OpcDa, "inProcess", null, null, true)]
    [InlineData(SourceTypes.OpcDa, "own", null, null, true)]
    [InlineData(SourceTypes.OpcDa, "own", ".\\mesadm1", "pw", true)]
    [InlineData(SourceTypes.OpcDa, "own", "DOMAIN\\user", null, true)]
    [InlineData(SourceTypes.OpcDa, "group", ".\\mesadm1", "pw", true)]
    [InlineData(SourceTypes.OpcDa, "group", ".\\mesadm1", null, true)]
    [InlineData("OpcUa", "inProcess", null, null, true)]
    [InlineData("OpcUa", "own", ".\\mesadm1", "pw", false)]
    [InlineData(SourceTypes.OpcDa, "group", null, null, false)]
    [InlineData(SourceTypes.OpcDa, "own", null, "pw", false)]
    [InlineData(SourceTypes.OpcDa, "inProcess", ".\\mesadm1", null, false)]
    [InlineData(SourceTypes.OpcDa, "isolated", null, null, false)]
    [InlineData(SourceTypes.OpcDa, "own", "bad name", null, false)]
    [InlineData(SourceTypes.OpcDa, "own", "a\\b\\c", null, false)]
    public void Validate_AcceptsOnlyWellFormedCombinations(string sourceType, string mode, string? user, string? password, bool accepted)
    {
        string? error = DaWorkerOptionsValidator.Validate(sourceType, new DaWorkerOptions(mode, user, password));

        Assert.Equal(accepted, error is null);
    }

    [Fact]
    public void Validate_NonOpcDaRunAs_ExplainsTheRestriction()
    {
        string? error = DaWorkerOptionsValidator.Validate(SourceTypes.OpcUa, new DaWorkerOptions(DaWorkerModes.Own, ".\\user", null));

        Assert.NotNull(error);
        Assert.Contains("OPC DA", error);
    }
}
