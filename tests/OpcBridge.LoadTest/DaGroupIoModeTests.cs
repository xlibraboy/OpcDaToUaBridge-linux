using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Per-rate-group I/O mode overrides: survive the config migration round-trip, are
/// normalized on load, flow into <see cref="DaClientOptions.GroupIoModes"/>, and are
/// upserted/reset through <see cref="DaRuntimeSettings"/>.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class DaGroupIoModeTests
{
    private static DaSourceRuntimeSettings DaSource(string sourceId, IReadOnlyList<DaGroupIoMode>? groups = null)
        => new(
            sourceId,
            sourceId,
            SourceTypes.OpcDa,
            1000,
            true,
            50000,
            new OpcDaSourceOptions("Server.1", "localhost", null, null, null, groups),
            null,
            null,
            null,
            null);

    [Fact]
    public void ToDto_FromDto_RoundTripsGroupIoModes()
    {
        DaSourceRuntimeSettings source = DaSource("da1", new DaGroupIoMode[]
        {
            new("OpcBridge_500", 500, "Sync"),
            new("OpcBridge_1000", 1000, "Async20")
        });

        SourceConfigDto dto = SourceConfigMigration.ToDto(source);
        Assert.NotNull(dto.OpcDa);
        Assert.NotNull(dto.OpcDa!.Groups);
        Assert.Equal(2, dto.OpcDa.Groups!.Count);

        DaSourceRuntimeSettings restored = SourceConfigMigration.FromDto(dto, 1000);
        Assert.Equal(2, restored.GroupIoModes.Count);
        Assert.Equal("Sync", restored.GroupIoModes.Single(g => g.Rate == 500).IoMode);
        Assert.Equal("Async20", restored.GroupIoModes.Single(g => g.Rate == 1000).IoMode);
    }

    [Fact]
    public void Normalize_KeepsGroupModesAndNormalizesValues()
    {
        DaSourceRuntimeSettings source = DaSource("da1", new DaGroupIoMode[]
        {
            new("OpcBridge_500", 500, "sync"), // lowercase → Sync
            new("OpcBridge_1000", 1000, "Bogus"), // unknown → AutoDetect
            new("OpcBridge_50", 50, "Async20"), // below the 100ms minimum → dropped
            new("OpcBridge_1000", 1000, "Async20"), // duplicate rate → last wins
            new("OpcBridge_2000", 2000, "Async20")
        });

        DaSourceRuntimeSettings normalized = SourceConfigMigration.Normalize(source, 1000);

        Assert.Equal(3, normalized.GroupIoModes.Count);
        Assert.Equal("Sync", normalized.GroupIoModes.Single(g => g.Rate == 500).IoMode);
        Assert.Equal("Async20", normalized.GroupIoModes.Single(g => g.Rate == 1000).IoMode);
        Assert.Equal("Async20", normalized.GroupIoModes.Single(g => g.Rate == 2000).IoMode);
    }

    [Fact]
    public void ToOptions_GroupIoModes_BuildsRateMap()
    {
        DaSourceRuntimeSettings source = DaSource("da1", new DaGroupIoMode[]
        {
            new("OpcBridge_500", 500, "Sync"),
            new("OpcBridge_1000", 1000, "Async20")
        });

        DaClientOptions options = source.ToOptions(useSubscriptions: true, ioMode: "AutoDetect");

        Assert.Equal(2, options.GroupIoModes.Count);
        Assert.Equal("Sync", options.GroupIoModes[500]);
        Assert.Equal("Async20", options.GroupIoModes[1000]);
    }

    [Fact]
    public void SetSourceGroupIoMode_UpsertsByRate()
    {
        string sourcesPath = Path.Combine(AppContext.BaseDirectory, "sources.json");
        try
        {
            DaRuntimeSettings settings = new(Options.Create(new DaClientOptions
            {
                Sources =
                [
                    new DaSourceOptions { SourceId = "da1", DisplayName = "DA1", ProgId = "Server.1", Host = "localhost" }
                ]
            }));

            DaRuntimeSettingsSnapshot after = settings.SetSourceGroupIoMode("da1", "OpcBridge_1000", 1000, "Async20");
            DaSourceRuntimeSettings source = after.GetSource("da1")!;
            Assert.Single(source.GroupIoModes);
            Assert.Equal("Async20", source.GroupIoModes[0].IoMode);

            // Upsert replaces rather than duplicating.
            after = settings.SetSourceGroupIoMode("da1", "OpcBridge_1000", 1000, "Sync");
            source = after.GetSource("da1")!;
            Assert.Single(source.GroupIoModes);
            Assert.Equal("Sync", source.GroupIoModes[0].IoMode);
        }
        finally
        {
            if (File.Exists(sourcesPath))
            {
                File.Delete(sourcesPath);
            }
        }
    }

    [Fact]
    public void ResetSourceGroupIoMode_ClearsOneOrAll()
    {
        string sourcesPath = Path.Combine(AppContext.BaseDirectory, "sources.json");
        try
        {
            DaRuntimeSettings settings = new(Options.Create(new DaClientOptions
            {
                Sources =
                [
                    new DaSourceOptions { SourceId = "da1", DisplayName = "DA1", ProgId = "Server.1", Host = "localhost" }
                ]
            }));
            settings.SetSourceGroupIoMode("da1", "OpcBridge_500", 500, "Sync");
            settings.SetSourceGroupIoMode("da1", "OpcBridge_1000", 1000, "Async20");

            DaRuntimeSettingsSnapshot after = settings.ResetSourceGroupIoMode("da1", "OpcBridge_500", 500);
            DaSourceRuntimeSettings source = after.GetSource("da1")!;
            Assert.Single(source.GroupIoModes);
            Assert.Equal(1000, source.GroupIoModes[0].Rate);

            after = settings.ResetSourceGroupIoMode("da1", null, null);
            source = after.GetSource("da1")!;
            Assert.Empty(source.GroupIoModes);
            Assert.Null(source.OpcDa!.GroupIoModes);
        }
        finally
        {
            if (File.Exists(sourcesPath))
            {
                File.Delete(sourcesPath);
            }
        }
    }
}
