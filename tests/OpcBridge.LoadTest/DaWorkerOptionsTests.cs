using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

// Touches AppContext.BaseDirectory/sources.json like the other DaRuntimeSettings tests, so it
// joins the shared collection and cleans the file up.
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class DaWorkerOptionsTests : IDisposable
{
    private readonly string _sourcesJsonPath;

    public DaWorkerOptionsTests()
    {
        _sourcesJsonPath = Path.Combine(AppContext.BaseDirectory, "sources.json");
        if (File.Exists(_sourcesJsonPath))
        {
            File.Delete(_sourcesJsonPath);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_sourcesJsonPath))
        {
            File.Delete(_sourcesJsonPath);
        }
    }

    private static DaSourceRuntimeSettings DaSource(DaWorkerOptions? worker = null) => new(
        SourceId: "pmd",
        DisplayName: "PMD",
        SourceType: SourceTypes.OpcDa,
        UpdateRateMs: 1000,
        UseSubscriptions: true,
        MaxMappedTags: 50000,
        OpcDa: new OpcDaSourceOptions(
            "PMD.DDT_OPCDataServer.1",
            "localhost",
            null,
            null,
            null,
            Worker: worker),
        OpcUa: null,
        Melsec: null,
        S7200: null);

    [Fact]
    public void FromDto_LegacySourceWithoutWorker_DefaultsToInProcess()
    {
        var dto = new SourceConfigDto
        {
            SourceId = "pmd",
            SourceType = SourceTypes.OpcDa,
            OpcDa = new OpcDaSourceOptionsDto { ProgId = "PMD.DDT_OPCDataServer.1", Host = "localhost" }
        };

        DaSourceRuntimeSettings source = SourceConfigMigration.FromDto(dto, 1000);

        Assert.Equal(DaWorkerModes.InProcess, source.Worker.Mode);
        Assert.Null(source.Worker.RunAsUser);
    }

    [Fact]
    public void ToDto_ProtectsTheRunAsPasswordAtRest()
    {
        DaSourceRuntimeSettings source = DaSource(new DaWorkerOptions(DaWorkerModes.Group, ".\\mesadm1", "secret", null));

        SourceConfigDto dto = SourceConfigMigration.ToDto(source);

        Assert.NotNull(dto.OpcDa);
        Assert.NotNull(dto.OpcDa!.Worker);
        Assert.Equal("group", dto.OpcDa.Worker!.Mode);
        Assert.Equal(".\\mesadm1", dto.OpcDa.Worker.RunAsUser);
        // Dpapi on Windows, pass-through elsewhere; either way the secret survives a reload.
        Assert.Equal("secret", SecretProtector.Unprotect(dto.OpcDa.Worker.RunAsPassword));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(SecretProtector.IsProtected(dto.OpcDa.Worker.RunAsPassword));
        }
    }

    [Fact]
    public void ToDto_DefaultWorkerOptions_AreNotPersisted()
    {
        SourceConfigDto dto = SourceConfigMigration.ToDto(DaSource(new DaWorkerOptions()));

        Assert.Null(dto.OpcDa!.Worker);
    }

    [Fact]
    public void RoundTrip_PreservesWorkerSettings()
    {
        DaSourceRuntimeSettings source = DaSource(new DaWorkerOptions(DaWorkerModes.Own, "DOMAIN\\opcu1", "pw1", "DOMAIN"));

        DaSourceRuntimeSettings reloaded = SourceConfigMigration.FromDto(SourceConfigMigration.ToDto(source), 1000);

        Assert.Equal(DaWorkerModes.Own, reloaded.Worker.Mode);
        Assert.Equal("DOMAIN\\opcu1", reloaded.Worker.RunAsUser);
        Assert.Equal("DOMAIN", reloaded.Worker.RunAsDomain);
        Assert.Equal("pw1", reloaded.Worker.RunAsPassword);
    }

    [Fact]
    public void Normalize_UnknownMode_FallsBackToInProcess()
    {
        Assert.Null(SourceConfigMigration.NormalizeWorkerOptions(new DaWorkerOptions("isolated")));
    }

    [Fact]
    public void Normalize_GroupWithoutAccount_FallsBackToInProcess()
    {
        Assert.Null(SourceConfigMigration.NormalizeWorkerOptions(new DaWorkerOptions(DaWorkerModes.Group, null, "pw")));
    }

    [Fact]
    public void Normalize_OwnWithoutAccount_IsKept()
    {
        DaWorkerOptions? options = SourceConfigMigration.NormalizeWorkerOptions(new DaWorkerOptions(DaWorkerModes.Own));

        Assert.NotNull(options);
        Assert.Equal(DaWorkerModes.Own, options!.Mode);
        Assert.Null(options.RunAsUser);
    }

    [Fact]
    public void Normalize_PasswordWithoutAccount_DropsThePassword()
    {
        DaWorkerOptions? options = SourceConfigMigration.NormalizeWorkerOptions(new DaWorkerOptions(DaWorkerModes.Own, null, "pw"));

        Assert.NotNull(options);
        Assert.Null(options!.RunAsPassword);
    }

    [Fact]
    public void Normalize_TrimsAccountAndDomain()
    {
        DaWorkerOptions? options = SourceConfigMigration.NormalizeWorkerOptions(new DaWorkerOptions(" Own ", " .\\mesadm1 ", "pw", " "));

        Assert.NotNull(options);
        Assert.Equal(DaWorkerModes.Own, options!.Mode);
        Assert.Equal(".\\mesadm1", options.RunAsUser);
        Assert.Null(options.RunAsDomain);
    }

    [Fact]
    public void SetSourceWorker_UpdatesTheSourceAndBumpsVersion()
    {
        var settings = new DaRuntimeSettings(Options.Create(new DaClientOptions()));
        settings.UpsertSource(DaSource());
        long version = settings.GetSnapshot().Version;

        DaRuntimeSettingsSnapshot snapshot = settings.SetSourceWorker(
            "pmd",
            new DaWorkerOptions(DaWorkerModes.Own, ".\\mesadm1", "pw", null));

        Assert.Equal(version + 1, snapshot.Version);
        DaSourceRuntimeSettings updated = snapshot.GetSource("pmd")!;
        Assert.Equal(DaWorkerModes.Own, updated.Worker.Mode);
        Assert.Equal(".\\mesadm1", updated.Worker.RunAsUser);
        Assert.Equal("pw", updated.Worker.RunAsPassword);
    }

    [Fact]
    public void SetSourceWorker_UnknownSource_LeavesVersionUnchanged()
    {
        var settings = new DaRuntimeSettings(Options.Create(new DaClientOptions()));
        long version = settings.GetSnapshot().Version;

        DaRuntimeSettingsSnapshot snapshot = settings.SetSourceWorker("missing", new DaWorkerOptions(DaWorkerModes.Own));

        Assert.Equal(version, snapshot.Version);
    }
}
