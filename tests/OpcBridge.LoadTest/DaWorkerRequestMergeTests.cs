using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Regression tests for the worker-block merge on POST /api/da/sources (issue #39).
/// Switching an own/group worker that has a stored run-as password back to in-process used
/// to carry the stored password into the request, which the validator rejected ("A run-as
/// password requires a run-as account") — the save failed and the dashboard reloaded the
/// old mode, so the change appeared to snap back on its own.
/// </summary>
public sealed class DaWorkerRequestMergeTests
{
    private static readonly DaWorkerOptions StoredOwnWorker =
        new(DaWorkerModes.Own, ".\\mesadm1", "secret", null);

    [Fact]
    public void SwitchToInProcess_DropsTheStoredPassword_AndResetsTheOptions()
    {
        DaWorkerOptions merged = DaWorkerRequestMerge.Merge(
            new DaWorkerRequest(Mode: DaWorkerModes.InProcess),
            StoredOwnWorker);

        Assert.Null(DaWorkerOptionsValidator.Validate(SourceTypes.OpcDa, merged));
        Assert.Null(SourceConfigMigration.NormalizeWorkerOptions(merged));
    }

    [Fact]
    public void SwitchToGroup_WithoutAnAccount_StillFailsValidation()
    {
        DaWorkerOptions merged = DaWorkerRequestMerge.Merge(
            new DaWorkerRequest(Mode: DaWorkerModes.Group),
            StoredOwnWorker);

        Assert.NotNull(DaWorkerOptionsValidator.Validate(SourceTypes.OpcDa, merged));
    }

    [Fact]
    public void SameWorker_BlankPassword_KeepsTheStoredPassword()
    {
        DaWorkerOptions merged = DaWorkerRequestMerge.Merge(
            new DaWorkerRequest(Mode: DaWorkerModes.Own, RunAsUser: ".\\mesadm1"),
            StoredOwnWorker);

        Assert.Equal("secret", merged.RunAsPassword);
    }

    [Fact]
    public void SameWorker_NewPassword_ReplacesTheStoredOne()
    {
        DaWorkerOptions merged = DaWorkerRequestMerge.Merge(
            new DaWorkerRequest(Mode: DaWorkerModes.Own, RunAsUser: ".\\mesadm1", RunAsPassword: "new"),
            StoredOwnWorker);

        Assert.Equal("new", merged.RunAsPassword);
    }

    [Fact]
    public void ClearingTheAccount_DropsTheStoredPassword_AndKeepsOwnMode()
    {
        DaWorkerOptions merged = DaWorkerRequestMerge.Merge(
            new DaWorkerRequest(Mode: DaWorkerModes.Own),
            StoredOwnWorker);

        Assert.Null(DaWorkerOptionsValidator.Validate(SourceTypes.OpcDa, merged));

        DaWorkerOptions? normalized = SourceConfigMigration.NormalizeWorkerOptions(merged);
        Assert.NotNull(normalized);
        Assert.Equal(DaWorkerModes.Own, normalized!.Mode);
        Assert.Null(normalized.RunAsPassword);
    }

    [Fact]
    public void NewSource_OwnWorkerWithBlankPassword_DoesNotInventAPassword()
    {
        DaWorkerOptions merged = DaWorkerRequestMerge.Merge(
            new DaWorkerRequest(Mode: DaWorkerModes.Own, RunAsUser: ".\\mesadm1"),
            existing: null);

        Assert.Null(merged.RunAsPassword);
        Assert.Null(DaWorkerOptionsValidator.Validate(SourceTypes.OpcDa, merged));
    }
}
