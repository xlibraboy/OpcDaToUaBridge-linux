using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Pause is a runtime-only operator action (#5): a paused source must release its
/// upstream connection but the flag must never leak into sources.json, and it must
/// be dropped when the source is removed or the configuration is imported.
/// </summary>
public sealed class SourcePauseStoreTests : IDisposable
{
    private readonly string _sourcesJsonPath = Path.Combine(AppContext.BaseDirectory, "sources.json");

    public SourcePauseStoreTests()
    {
        if (File.Exists(_sourcesJsonPath)) File.Delete(_sourcesJsonPath);
    }

    public void Dispose()
    {
        if (File.Exists(_sourcesJsonPath)) File.Delete(_sourcesJsonPath);
    }

    private static DaRuntimeSettings CreateSettings()
    {
        return new DaRuntimeSettings(Options.Create(new DaClientOptions()));
    }

    private static DaSourceRuntimeSettings MxSource(string id)
    {
        return new DaSourceRuntimeSettings(
            id, id, SourceTypes.MxComponent, 1000, true, 50000,
            OpcDa: null, OpcUa: null, Melsec: null, S7200: null,
            MxComponent: new MxComponentSourceOptions(0, 3000, 2));
    }

    [Fact]
    public void SetPaused_TracksStateAndBumpsVersionSoTheWorkerReconciles()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.UpsertSource(MxSource("mx1"));
        long before = settings.GetSnapshot().Version;

        Assert.False(settings.IsPaused("mx1"));

        DaRuntimeSettingsSnapshot paused = settings.SetPaused("mx1", true);
        Assert.True(settings.IsPaused("mx1"));
        Assert.True(paused.Version > before, "version must move so the reconcile pass sees the pause");

        DaRuntimeSettingsSnapshot resumed = settings.SetPaused("MX1", true); // case-insensitive
        Assert.True(resumed.Version > paused.Version);
        Assert.True(settings.IsPaused("mx1"));

        settings.SetPaused("mx1", false);
        Assert.False(settings.IsPaused("mx1"));
    }

    [Fact]
    public void SetPaused_UnknownSourceStillTracksFlag_ButSnapshotIsUnchanged()
    {
        // IsPaused stays keyed by id so a late-arriving upsert of the same id
        // cannot inherit a stale pause from an earlier session of the same id.
        DaRuntimeSettings settings = CreateSettings();
        long version = settings.GetSnapshot().Version;

        settings.SetPaused("ghost", true);
        Assert.True(settings.IsPaused("ghost"));

        Assert.Equal(version + 1, settings.GetSnapshot().Version);
    }

    [Fact]
    public void Pause_IsNotPersistedToSourcesJson()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.UpsertSource(MxSource("mx1"));
        Assert.True(File.Exists(_sourcesJsonPath), "precondition: upsert persists config");

        string jsonBefore = File.ReadAllText(_sourcesJsonPath);
        settings.SetPaused("mx1", true);
        Assert.True(settings.IsPaused("mx1"));

        string jsonAfter = File.ReadAllText(_sourcesJsonPath);
        Assert.Equal(jsonBefore, jsonAfter);
        Assert.DoesNotContain("Paused", jsonAfter, StringComparison.OrdinalIgnoreCase);

        // A brand-new instance (simulated restart) resumes the source.
        DaRuntimeSettings restarted = CreateSettings();
        Assert.False(restarted.IsPaused("mx1"));
    }

    [Fact]
    public void TryRemoveSource_DropsThePauseFlag()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.UpsertSource(MxSource("mx1"));
        settings.SetPaused("mx1", true);
        Assert.True(settings.IsPaused("mx1"));

        Assert.True(settings.TryRemoveSource("mx1", out _));
        Assert.False(settings.IsPaused("mx1"));
    }

    [Fact]
    public void RestoreFromSnapshot_ClearsRuntimePauses()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.UpsertSource(MxSource("mx1"));
        settings.UpsertSource(MxSource("mx2"));
        settings.SetPaused("mx1", true);
        settings.SetPaused("mx2", true);

        DaRuntimeSettingsSnapshot imported = settings.GetSnapshot();
        settings.RestoreFromSnapshot(imported);

        Assert.False(settings.IsPaused("mx1"));
        Assert.False(settings.IsPaused("mx2"));
    }
}
