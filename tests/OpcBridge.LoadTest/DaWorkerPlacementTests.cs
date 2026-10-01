using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaWorkerPlacementTests
{
    private static DaSourceRuntimeSettings Da(string id, DaWorkerOptions? worker = null) => new(
        SourceId: id,
        DisplayName: id,
        SourceType: SourceTypes.OpcDa,
        UpdateRateMs: 1000,
        UseSubscriptions: true,
        MaxMappedTags: 50000,
        OpcDa: new OpcDaSourceOptions("Vendor.Server.1", "localhost", null, null, null, Worker: worker),
        OpcUa: null,
        Melsec: null,
        S7200: null);

    [Fact]
    public void InProcess_HasNoWorkerKey()
    {
        Assert.Null(WorkerPlacement.WorkerKeyFor(Da("s1")));
    }

    [Fact]
    public void OwnWithoutAccount_GetsItsOwnKey()
    {
        Assert.Equal("own:s1", WorkerPlacement.WorkerKeyFor(Da("s1", new DaWorkerOptions(DaWorkerModes.Own))));
    }

    [Fact]
    public void OwnWithAccount_StillGetsItsOwnKey()
    {
        Assert.Equal(
            "own:s1",
            WorkerPlacement.WorkerKeyFor(Da("s1", new DaWorkerOptions(DaWorkerModes.Own, ".\\mesadm1"))));
    }

    [Fact]
    public void Group_SharesOneKeyPerAccount()
    {
        string? first = WorkerPlacement.WorkerKeyFor(Da("a", new DaWorkerOptions(DaWorkerModes.Group, ".\\mesadm1")));
        string? second = WorkerPlacement.WorkerKeyFor(Da("b", new DaWorkerOptions(DaWorkerModes.Group, " .\\MESADM1 ")));

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void GroupWithoutAccount_IsInProcess()
    {
        Assert.Null(WorkerPlacement.WorkerKeyFor(Da("s1", new DaWorkerOptions(DaWorkerModes.Group))));
    }

    [Fact]
    public void NonDaSource_IsNeverPlaced()
    {
        DaSourceRuntimeSettings ua = Da("ua1", new DaWorkerOptions(DaWorkerModes.Own)) with
        {
            SourceType = SourceTypes.OpcUa
        };

        Assert.Null(WorkerPlacement.WorkerKeyFor(ua));
    }

    [Fact]
    public void SourcesFor_ReturnsOnlyTheGroupMembers()
    {
        var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[]
        {
            Da("a", new DaWorkerOptions(DaWorkerModes.Group, ".\\mesadm1")),
            Da("b", new DaWorkerOptions(DaWorkerModes.Group, ".\\mesadm1")),
            Da("c", new DaWorkerOptions(DaWorkerModes.Own, ".\\mesadm1")),
            Da("d")
        }, 1);

        IReadOnlyList<DaSourceRuntimeSettings> group = WorkerPlacement.SourcesFor(snapshot, "acct:.\\MESADM1");

        Assert.Equal(new[] { "a", "b" }, group.Select(source => source.SourceId));
    }
}
