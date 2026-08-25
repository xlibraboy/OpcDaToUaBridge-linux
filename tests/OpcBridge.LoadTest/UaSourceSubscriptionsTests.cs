using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

// Joins InterlinkApiAppCollection because every test round-trips AppContext.BaseDirectory/sources.json
// (ctor/Dispose delete it; UpsertUaSubscription persists to it) — parallel collections writing or
// deleting that same file would race the disk round-trip in SourcesJson_RoundTripsSubscriptions.
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class UaSourceSubscriptionsTests : IDisposable
{
    private readonly string _binDir = AppContext.BaseDirectory;
    private readonly string _sourcesPath;

    public UaSourceSubscriptionsTests()
    {
        _sourcesPath = Path.Combine(_binDir, "sources.json");
        if (File.Exists(_sourcesPath)) File.Delete(_sourcesPath);
    }

    public void Dispose()
    {
        if (File.Exists(_sourcesPath)) File.Delete(_sourcesPath);
    }

    private static DaRuntimeSettings CreateSettings() => new(
        Options.Create(new DaClientOptions { ProgId = "P", Host = "h", UpdateRateMs = 1000 }));

    private static DaSourceRuntimeSettings UaSource(string id = "ua-a") => SourceConfigMigration.Normalize(
        new DaSourceRuntimeSettings(id, id, SourceTypes.OpcUa, 1000, true, 10000,
            OpcDa: null,
            OpcUa: new OpcUaSourceOptions("opc.tcp://127.0.0.1:49321/x", "None", "None", null, null, 60000, 5000),
            Melsec: null, S7200: null, MxComponent: null),
        1000);

    [Fact]
    public void Upsert_AddsValidatesClampsAndDedupes()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));

        DaRuntimeSettingsSnapshot s1 = settings.UpsertUaSubscription("ua-a", "  Fast ", 250);
        Assert.Single(s1.GetSource("ua-a")!.UaSubscriptions);
        Assert.Equal("Fast", s1.GetSource("ua-a")!.UaSubscriptions[0].Name);
        Assert.Equal(250, s1.GetSource("ua-a")!.UaSubscriptions[0].UpdateRateMs);

        // Rate below floor clamps to 100; re-upsert same name (case-insensitive) updates in place.
        DaRuntimeSettingsSnapshot s2 = settings.UpsertUaSubscription("UA-A", "fAst", 10);
        DaSourceRuntimeSettings src = s2.GetSource("ua-a")!;
        Assert.Single(src.UaSubscriptions);
        Assert.Equal(100, src.UaSubscriptions[0].UpdateRateMs);
    }

    [Fact]
    public void Upsert_RejectsBadInput()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));

        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("ua-a", "   ", 250));   // blank
        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("ua-a", new string('x', 65), 250)); // too long
        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("nope", "Fast", 250)); // unknown source

        DaRuntimeSettingsSnapshot capped = settings.UpsertUaSubscription("ua-a", $"S{new string('y', 60)}", 250);
        _ = capped;
        for (int i = 0; i < 15; i++)
        {
            settings.UpsertUaSubscription("ua-a", $"sub{i}", 500 + i);
        }
        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("ua-a", "overflow", 250)); // cap 16
    }

    [Fact]
    public void Upsert_RejectsNonPositiveRate()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));

        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("ua-a", "Zero", 0));
        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("ua-a", "Negative", -5));
    }

    [Fact]
    public void Upsert_RejectsNonUaSource()
    {
        DaRuntimeSettings settings = CreateSettings();
        DaSourceRuntimeSettings da = SourceConfigMigration.Normalize(
            new DaSourceRuntimeSettings("opc-vm", "opc-vm", SourceTypes.OpcDa, 1000, true, 10000,
            OpcDa: new OpcDaSourceOptions("Matrikon.OPC.Simulation.1", "localhost", null, null, null),
            OpcUa: null, Melsec: null, S7200: null, MxComponent: null), 1000);
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { da }, 0));

        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("opc-vm", "Fast", 250));
    }

    [Fact]
    public void Remove_DeletesByName_CaseInsensitive_AndThrowsWhenMissing()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));
        settings.UpsertUaSubscription("ua-a", "Fast", 250);

        DaRuntimeSettingsSnapshot after = settings.RemoveUaSubscription("ua-a", "fAST");
        Assert.Empty(after.GetSource("ua-a")!.UaSubscriptions);
        Assert.Throws<ArgumentException>(() => settings.RemoveUaSubscription("ua-a", "Fast"));
    }

    [Fact]
    public void SubscriptionsEqual_OrderInsensitive_NameCaseInsensitive()
    {
        DaSourceRuntimeSettings a = UaSource() with
        {
            OpcUa = new OpcUaSourceOptions("opc.tcp://x", "None", "None", null, null, 60000, 5000,
                Subscriptions: new List<UaSubscriptionSettings> { new("Fast", 250), new("Slow", 5000) })
        };
        DaSourceRuntimeSettings b = a with
        {
            OpcUa = new OpcUaSourceOptions("opc.tcp://x", "None", "None", null, null, 60000, 5000,
                Subscriptions: new List<UaSubscriptionSettings> { new("slow", 5000), new("FAST", 250) })
        };
        DaSourceRuntimeSettings c = a with
        {
            OpcUa = new OpcUaSourceOptions("opc.tcp://x", "None", "None", null, null, 60000, 5000,
                Subscriptions: new List<UaSubscriptionSettings> { new("Fast", 300), new("Slow", 5000) })
        };

        Assert.True(a.UaSubscriptionsEqual(b));
        Assert.False(a.UaSubscriptionsEqual(c));
    }

    [Fact]
    public void ToUaOptions_CarriesSubscriptions()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));
        settings.UpsertUaSubscription("ua-a", "Fast", 250);

        DaRuntimeSettingsSnapshot snap = settings.GetSnapshot();
        OpcUaSourceClientOptions opts = snap.GetSource("ua-a")!.ToUaOptions(snap);

        Assert.Single(opts.Subscriptions);
        Assert.Equal("Fast", opts.Subscriptions[0].Name);
    }

    [Fact]
    public void SourcesJson_RoundTripsSubscriptions()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));
        settings.UpsertUaSubscription("ua-a", "Fast", 250);

        DaRuntimeSettings reloaded = CreateSettings(); // loads sources.json from disk
        DaRuntimeSettingsSnapshot snap = reloaded.GetSnapshot();
        Assert.Single(snap.GetSource("ua-a")!.UaSubscriptions);
        Assert.Equal(250, snap.GetSource("ua-a")!.UaSubscriptions[0].UpdateRateMs);
    }
}
