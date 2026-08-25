# PLC Groups (MX Component) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give MxComponent sources named polling groups (`PlcGroupSettings`: name + rate) with per-tag assignment, managed via `/api/plc/groups*` endpoints and a dashboard **PLC Groups** tab — bridge-side timer buckets, since MX Component's COM API has no native value subscription.

**Architecture:** Groups are stored top-level on `DaSourceRuntimeSettings` (driver-unified); tags carry a `PlcGroup` name. `SourceMappingCache` resolves each tag's *effective* rate (assigned group → group rate; else per-tag `PollRateMs`; else bridge default) at query time through a live resolver, so the existing one-poller-per-distinct-rate machinery schedules everything unchanged. Group definition edits restart only that source's pollers via a fingerprint check in `ReconfigureSessionsAsync`; the ActUtlType session is never reopened.

**Tech Stack:** .NET 8 (net8.0), C# 12, xUnit, System.Text.Json, ASP.NET minimal APIs, Avalonia HMI (thin client over HTTP), dashboard is server-rendered HTML/JS in `DashboardPage.cs`.

**Spec:** `docs/superpowers/specs/2026-08-25-plc-groups-mx-component-design.md`

## Global Constraints

- Worktree: `/mnt/c/Users/xlibr/Documents/OpcDaToUaBridge/.worktrees/feature/mx-component-group` — all commands run from there.
- Build/test CLI is NOT on PATH. Use: `export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"` then `"$HOME/.dotnet/dotnet" test OpcBridge.sln -v q --nologo`. Baseline: 558 passed / 0 failed.
- `TagMapping` is a **mutable sealed class** — there is no `with`; mutate copies explicitly.
- `DaSourceRuntimeSettings` is a positional record with many existing positional constructor calls — new parameters MUST be appended last with defaults.
- JSON contract names are camelCase via `[JsonPropertyName]` on `TagMapping`; `sources.json` DTOs use PascalCase C# properties (serializer applies naming policy).
- Validation constants (verbatim from spec §4): name trimmed 1–64 chars, unique case-insensitive per source; rate ≥ 1 accepted, clamped to **100 ms floor**; max **16 groups/source** (`MaxPlcGroupsPerSource = 16`); only `SourceType == SourceTypes.MxComponent` accepted this iteration.
- House API style: POST bodies; success `{ ok: true, version }`; errors `400 { error: "<message>" }` via `catch (ArgumentException)`.
- Commit style (conventional, lowercase scope): `feat(plc-groups): ...`, `test(plc-groups): ...`.
- Do NOT modify `SourceConnectionEquals` for MxComponent (type/timeout/retry only — connection identity untouched).
- Test files live flat in `tests/OpcBridge.LoadTest/`, namespace `OpcBridge.LoadTest`, xUnit `[Fact]`/`[Theory]`; app-backed tests use `[Collection(nameof(InterlinkApiAppCollection))]`.

---

### Task 1: Core types — `PlcGroupSettings` record and `TagMapping.PlcGroup`

**Files:**
- Create: `src/OpcBridge.Core/PlcGroupSettings.cs`
- Modify: `src/OpcBridge.Core/TagMapping.cs` (add field after `Subscription`, ~line 42)
- Test: `tests/OpcBridge.LoadTest/PlcGroupCoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `OpcBridge.Core.PlcGroupSettings(string Name, int UpdateRateMs)`; `TagMapping.PlcGroup` (`string`, default `""`, JSON `"plcGroup"`). Every later task depends on these exact names.

- [ ] **Step 1: Write the failing test**

Create `tests/OpcBridge.LoadTest/PlcGroupCoreTests.cs`:

```csharp
using System.Text.Json;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// PLC group core data types: record shape, JSON contract name on TagMapping, and
/// default-empty semantics (unassigned tags ride the source default bucket — spec §4).
/// </summary>
public sealed class PlcGroupCoreTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PlcGroupSettings_ExposesNameAndRate()
    {
        PlcGroupSettings group = new("Fast", 250);
        Assert.Equal("Fast", group.Name);
        Assert.Equal(250, group.UpdateRateMs);
    }

    [Fact]
    public void TagMapping_PlcGroup_DefaultsEmpty_AndRoundTripsJsonProperty()
    {
        TagMapping mapping = new() { SourceId = "mx1", ItemId = "D100", UaNodeId = "ns=2;s=D100" };
        Assert.Equal(string.Empty, mapping.PlcGroup);

        mapping.PlcGroup = "Fast";
        string json = JsonSerializer.Serialize(mapping, SerializerOptions);
        Assert.Contains("\"plcGroup\":\"Fast\"", json);

        TagMapping parsed = JsonSerializer.Deserialize<TagMapping>(json, SerializerOptions)!;
        Assert.Equal("Fast", parsed.PlcGroup);
    }

    [Fact]
    public void TagMapping_Deserialize_WithoutPlcGroup_DefaultsEmpty()
    {
        TagMapping parsed = JsonSerializer.Deserialize<TagMapping>(
            "{\"sourceId\":\"mx1\",\"itemId\":\"D100\",\"uaNodeId\":\"ns=2;s=D100\"}", SerializerOptions)!;
        Assert.Equal(string.Empty, parsed.PlcGroup);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupCoreTests -v q --nologo`
Expected: FAIL — `PlcGroupSettings` not found / `TagMapping` has no `PlcGroup`.

- [ ] **Step 3: Write minimal implementation**

Create `src/OpcBridge.Core/PlcGroupSettings.cs`:

```csharp
namespace OpcBridge.Core;

/// <summary>
/// One named PLC polling group on a PLC-type source: a display name and the update rate (ms)
/// its member tags are polled at. Pure data type — validation/clamping happens at the
/// settings/API layer (100 ms floor), mirroring <see cref="UaSubscriptionSettings"/>.
/// </summary>
public sealed record PlcGroupSettings(string Name, int UpdateRateMs);
```

In `src/OpcBridge.Core/TagMapping.cs`, immediately after the `Subscription` property (line ~42), add:

```csharp
    /// <summary>
    /// PLC sources (MxComponent today) only: name of the source-defined PLC group this
    /// tag rides on. Empty string = source default bucket (default-rate semantics).
    /// Unknown names fall back to the default bucket at runtime (spec §4).
    /// </summary>
    [JsonPropertyName("plcGroup")]
    public string PlcGroup { get; set; } = string.Empty;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupCoreTests -v q --nologo`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.Core/PlcGroupSettings.cs src/OpcBridge.Core/TagMapping.cs tests/OpcBridge.LoadTest/PlcGroupCoreTests.cs
git commit -m "feat(plc-groups): core PlcGroupSettings record and TagMapping.PlcGroup field"
```

---

### Task 2: Source settings — `PlcGroups` storage and `PlcGroupsEqual`

**Files:**
- Modify: `src/OpcBridge.App/DaRuntimeSettings.cs` — `DaSourceRuntimeSettings` record (line 664–676: append parameter AFTER `string IoMode = "AutoDetect"`), compat getters region (~line 710–712)
- Test: `tests/OpcBridge.LoadTest/PlcGroupSettingsTests.cs`

**Interfaces:**
- Consumes: `PlcGroupSettings` from Task 1.
- Produces: `DaSourceRuntimeSettings.PlcGroups` (`IReadOnlyList<PlcGroupSettings>?`, optional LAST ctor param, default null); `DaSourceRuntimeSettings.PlcGroupsList` (never-null getter); `bool PlcGroupsEqual(DaSourceRuntimeSettings other)` (order-insensitive, case-insensitive names, rate-sensitive). Tasks 3, 4, 7, 9 use these.

- [ ] **Step 1: Write the failing test**

Create `tests/OpcBridge.LoadTest/PlcGroupSettingsTests.cs`:

```csharp
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// PlcGroups storage on DaSourceRuntimeSettings: legacy configs default to empty and
/// PlcGroupsEqual compares order-insensitively by (name CI, rate) — the worker restart
/// fingerprint (spec §5).
/// </summary>
public sealed class PlcGroupSettingsTests
{
    private static DaSourceRuntimeSettings MxSource(IReadOnlyList<PlcGroupSettings>? groups = null)
        => new("mx1", "MX", SourceTypes.MxComponent, 1000, true, 50000,
            null, null, null, null,
            new MxComponentSourceOptions(0, 3000, 2),
            IoMode: "AutoDetect", PlcGroups: groups);

    [Fact]
    public void LegacyConstruction_PlcGroupsList_IsEmpty()
    {
        // Positional call WITHOUT the new parameter must still compile (back-compat guarantee).
        DaSourceRuntimeSettings legacy = new("mx1", "MX", SourceTypes.MxComponent, 1000, true, 50000,
            null, null, null, null, new MxComponentSourceOptions(0, 3000, 2));
        Assert.Empty(legacy.PlcGroupsList);
    }

    [Fact]
    public void PlcGroupsEqual_SameMembersDifferentOrder_True()
    {
        DaSourceRuntimeSettings a = MxSource(new[] { new PlcGroupSettings("Fast", 250), new PlcGroupSettings("Slow", 5000) });
        DaSourceRuntimeSettings b = MxSource(new[] { new PlcGroupSettings("slow", 5000), new PlcGroupSettings("FAST", 250) });
        Assert.True(a.PlcGroupsEqual(b));
        Assert.True(b.PlcGroupsEqual(a));
    }

    [Theory]
    [InlineData(250, 500)]   // rate differs
    [InlineData(1, 1)]       // count differs handled below; here same-count different names
    public void PlcGroupsEqual_DifferentDefinitions_False(int rateA, int rateB)
    {
        DaSourceRuntimeSettings a = MxSource(new[] { new PlcGroupSettings("Fast", rateA) });
        DaSourceRuntimeSettings b = MxSource(new[] { new PlcGroupSettings(rateA == rateB ? "Other" : "Fast", rateB) });
        Assert.False(a.PlcGroupsEqual(b));
    }

    [Fact]
    public void PlcGroupsEqual_EmptyVsNull_True()
    {
        Assert.True(MxSource(null).PlcGroupsEqual(MxSource(Array.Empty<PlcGroupSettings>())));
    }
}
```

Note: if `[InlineData(1, 1)]` produces two identical-name groups ("Fast" == "Fast"), adjust the second name in the test body so the pair genuinely differs — the assertion targets *different definition sets*, so keep `rateA == rateB ? "Other" : "Fast"`.

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupSettingsTests -v q --nologo`
Expected: FAIL — no `PlcGroups` parameter / `PlcGroupsList` / `PlcGroupsEqual`.

- [ ] **Step 3: Write minimal implementation**

In `src/OpcBridge.App/DaRuntimeSettings.cs`:

(a) Append the parameter to `DaSourceRuntimeSettings` (after `string IoMode = "AutoDetect"`):

```csharp
    MxComponentSourceOptions? MxComponent,
    string IoMode = "AutoDetect",
    IReadOnlyList<PlcGroupSettings>? PlcGroups = null)
```

(b) Next to the `UaSubscriptions` getter (line ~710–712), add:

```csharp
    /// <summary>Named PLC group definitions; empty for non-MX sources or legacy configs.</summary>
    public IReadOnlyList<PlcGroupSettings> PlcGroupsList
        => PlcGroups ?? Array.Empty<PlcGroupSettings>();

    /// <summary>Order-insensitive comparison of named PLC group definitions (case-insensitive names).</summary>
    public bool PlcGroupsEqual(DaSourceRuntimeSettings other)
    {
        IReadOnlyList<PlcGroupSettings> left = PlcGroupsList;
        IReadOnlyList<PlcGroupSettings> right = other.PlcGroupsList;
        if (left.Count != right.Count)
        {
            return false;
        }

        Dictionary<string, int> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (PlcGroupSettings g in left)
        {
            byName[g.Name.Trim()] = g.UpdateRateMs;
        }

        foreach (PlcGroupSettings g in right)
        {
            string key = g.Name.Trim();
            if (!byName.TryGetValue(key, out int rate) || rate != g.UpdateRateMs)
            {
                return false;
            }

            byName.Remove(key);
        }

        return byName.Count == 0;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupSettingsTests -v q --nologo`
Expected: PASS (5 tests). Then run the FULL suite once — the appended record parameter can break other positional constructors only if they passed `IoMode:` by name followed by more args (they don't; verify green).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DaRuntimeSettings.cs tests/OpcBridge.LoadTest/PlcGroupSettingsTests.cs
git commit -m "feat(plc-groups): PlcGroups storage on DaSourceRuntimeSettings with order-insensitive equality"
```

---

### Task 3: Normalization helpers and persistence round-trip

**Files:**
- Modify: `src/OpcBridge.App/DaRuntimeSettings.cs` — `SourceConfigMigration` (constants/helpers near lines 1396–1424; `Normalize` at 1224–1356; `FromDto` at 940–1150; `ToDto` at 1152–1222) and `SourceConfigDto` (~line 810)
- Test: extend `tests/OpcBridge.LoadTest/PlcGroupSettingsTests.cs`

**Interfaces:**
- Consumes: Task 2 storage.
- Produces: `SourceConfigMigration.MaxPlcGroupsPerSource = 16`; `static IReadOnlyList<PlcGroupSettings> NormalizePlcGroups(IEnumerable<PlcGroupSettings>? groups)` (trim, drop blanks, dedupe first-wins CI, clamp ≥ 100 ms); `SourceConfigDto.PlcGroups` (`List<PlcGroupDto>?`); `PlcGroupDto` (Name, UpdateRateMs). Round-trip guarantees `sources.json` persistence (used by Task 4 endpoints).

- [ ] **Step 1: Write the failing test**

Append to `tests/OpcBridge.LoadTest/PlcGroupSettingsTests.cs`:

```csharp
[Fact]
public void ToDto_FromDto_RoundTripsPlcGroups_ForMxSources()
{
    DaSourceRuntimeSettings source = MxSource(new[]
    {
        new PlcGroupSettings("Fast", 250),
        new PlcGroupSettings("Slow", 5000)
    });

    SourceConfigDto dto = SourceConfigMigration.ToDto(source);
    Assert.NotNull(dto.PlcGroups);
    Assert.Equal(2, dto.PlcGroups!.Count);

    DaSourceRuntimeSettings restored = SourceConfigMigration.FromDto(dto, 1000);
    Assert.True(restored.PlcGroupsEqual(source));
}

[Fact]
public void FromDto_ClearsPlcGroups_ForNonMxSources()
{
    SourceConfigDto dto = new()
    {
        SourceId = "da1",
        SourceType = SourceTypes.OpcDa,
        ProgId = "Server.1",
        Host = "localhost",
        PlcGroups = new List<PlcGroupDto> { new() { Name = "Fast", UpdateRateMs = 250 } }
    };

    DaSourceRuntimeSettings restored = SourceConfigMigration.FromDto(dto, 1000);
    Assert.Empty(restored.PlcGroupsList);
}

[Fact]
public void NormalizePlcGroups_TrimsDedupesClampsAndDropsBlanks()
{
    IReadOnlyList<PlcGroupSettings> normalized = SourceConfigMigration.NormalizePlcGroups(new[]
    {
        new PlcGroupSettings("  Fast ", 1),     // clamped to 100
        new PlcGroupSettings("fast", 999),      // duplicate CI — first wins (100)
        new PlcGroupSettings("   ", 500),       // blank dropped
        new PlcGroupSettings("Slow", 0)         // clamped to 100
    });

    Assert.Equal(2, normalized.Count);
    Assert.Equal("Fast", normalized[0].Name);
    Assert.Equal(100, normalized[0].UpdateRateMs);
    Assert.Equal("Slow", normalized[1].Name);
    Assert.Equal(100, normalized[1].UpdateRateMs);
}
```

If `SourceConfigDto` requires more mandatory fields to compile in the `da1` test (check its initializer requirements near line 810), fill them the same way existing tests construct DA DTOs (see `DaGroupIoModeTests.ToDto_FromDto_RoundTripsGroupIoModes` for reference shape).

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupSettingsTests -v q --nologo`
Expected: FAIL — no `ToDto` mapping / no `NormalizePlcGroups`.

- [ ] **Step 3: Write minimal implementation**

(a) In `SourceConfigMigration` next to `MaxUaSubscriptionsPerSource` (line ~1396):

```csharp
    public const int MaxPlcGroupsPerSource = 16;

    /// <summary>Trim names, dedupe case-insensitively (first wins), clamp rates to >= 100 ms, drop blanks.</summary>
    public static IReadOnlyList<PlcGroupSettings> NormalizePlcGroups(IEnumerable<PlcGroupSettings>? groups)
    {
        if (groups is null)
        {
            return Array.Empty<PlcGroupSettings>();
        }

        Dictionary<string, PlcGroupSettings> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (PlcGroupSettings group in groups)
        {
            string name = group.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            int rate = Math.Max(100, group.UpdateRateMs);
            if (!result.ContainsKey(name))
            {
                result[name] = new PlcGroupSettings(name, rate);
            }
        }

        return result.Values.ToList();
    }
```

(b) Add to `SourceConfigDto` (near line 810, alongside its flat fields):

```csharp
    public List<PlcGroupDto>? PlcGroups { get; set; }
```

and the DTO class (next to `MxComponentSourceOptionsDto`, ~line 923):

```csharp
public sealed class PlcGroupDto
{
    public string? Name { get; set; }
    public int UpdateRateMs { get; set; }
}
```

(c) `FromDto`: before the final `return Normalize(...)` (line ~1137), resolve groups (only meaningful for MX):

```csharp
        IReadOnlyList<PlcGroupSettings>? plcGroups = null;
        if (dto.PlcGroups is { Count: > 0 }
            && string.Equals(sourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
        {
            plcGroups = dto.PlcGroups.Select(g => new PlcGroupSettings(g.Name ?? string.Empty, g.UpdateRateMs)).ToList();
        }
```

(d) `Normalize` (line ~1312, inside the `MxComponent` branch is driver options only — groups are top-level, so handle BEFORE the final construction, ~line 1342):

```csharp
        IReadOnlyList<PlcGroupSettings>? plcGroups = null;
        if (string.Equals(sourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<PlcGroupSettings> normalizedGroups = NormalizePlcGroups(source.PlcGroups);
            if (normalizedGroups.Count > 0)
            {
                plcGroups = normalizedGroups;
            }
        }
```

then append `PlcGroups: plcGroups` to the `return new DaSourceRuntimeSettings(...)` at line ~1343–1355.

(e) `ToDto` (line ~1152): after `MxComponent = ...` assignment add:

```csharp
            PlcGroups = source.PlcGroupsList.Count == 0
                ? null
                : source.PlcGroupsList.Select(g => new PlcGroupDto { Name = g.Name, UpdateRateMs = g.UpdateRateMs }).ToList()
```

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupSettingsTests -v q --nologo`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DaRuntimeSettings.cs tests/OpcBridge.LoadTest/PlcGroupSettingsTests.cs
git commit -m "feat(plc-groups): normalize helpers and sources.json round-trip"
```

---

### Task 4: Registry — `UpsertPlcGroup` / `RemovePlcGroup` on `DaRuntimeSettings`

**Files:**
- Modify: `src/OpcBridge.App/DaRuntimeSettings.cs` — add methods after `RemoveUaSubscription` (~line 260)
- Test: `tests/OpcBridge.LoadTest/PlcGroupRegistryTests.cs`

**Interfaces:**
- Consumes: Tasks 2–3.
- Produces: `DaRuntimeSettingsSnapshot UpsertPlcGroup(string sourceId, string name, int updateRateMs)`; `DaRuntimeSettingsSnapshot RemovePlcGroup(string sourceId, string name)`. Both throw `ArgumentException` with operator-readable messages; both bump snapshot Version and Persist. Task 8's endpoints wrap exactly these.

- [ ] **Step 1: Write the failing test**

Create `tests/OpcBridge.LoadTest/PlcGroupRegistryTests.cs` (uses the temp-file persistence pattern from `DaGroupIoModeTests` — copy its fixture setup for `DaRuntimeSettings` construction; if that fixture uses `IOptions<BridgeOptions>` + temp dir, replicate identically):

```csharp
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// UpsertPlcGroup/RemovePlcGroup validation: MX-sources-only gate, name rules, 100 ms clamp,
/// 16-group soft cap, version bumps. Mirrors UpsertUaSubscription semantics (spec §4).
/// </summary>
public sealed class PlcGroupRegistryTests
{
    private static (DaRuntimeSettings Settings, string SourceId) CreateWithMxSource()
    {
        // Reuse the exact DaRuntimeSettings fixture construction from DaGroupIoModeTests
        // (temp directory persistence + IOptions<BridgeOptions>). Seed one MxComponent source
        // named "mx1" via FromDto/SetSources equivalent used by that fixture.
        throw new NotImplementedException("Copy fixture setup from DaGroupIoModeTests");
    }
    // NOTE to implementer: replace the throw above with the real fixture copied from
    // DaGroupIoModeTests.cs (same pattern; seed source via SourceConfigMigration.FromDto with
    // SourceType=MxComponent). The tests below are the contract — wire the fixture, don't change them.

    [Fact]
    public void Upsert_AddsAndUpdatesCaseInsensitively_ClampsRate_BumpsVersion()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        long v0 = settings.GetSnapshot().Version;

        settings.UpsertPlcGroup(sourceId, "  Fast ", 1);
        DaRuntimeSettingsSnapshot afterAdd = settings.GetSnapshot();
        Assert.Single(afterAdd.GetSource(sourceId)!.PlcGroupsList);
        Assert.Equal("Fast", afterAdd.GetSource(sourceId)!.PlcGroupsList[0].Name);
        Assert.Equal(100, afterAdd.GetSource(sourceId)!.PlcGroupsList[0].UpdateRateMs);
        Assert.True(afterAdd.Version > v0);

        settings.UpsertPlcGroup(sourceId, "fast", 5000);
        DaRuntimeSettingsSnapshot afterUpdate = settings.GetSnapshot();
        Assert.Single(afterUpdate.GetSource(sourceId)!.PlcGroupsList);
        Assert.Equal(5000, afterUpdate.GetSource(sourceId)!.PlcGroupsList[0].UpdateRateMs);
    }

    [Fact]
    public void Upsert_RejectsNonMxSource()
    {
        (DaRuntimeSettings settings, _) = CreateWithMxSource();
        // Seed/create or reuse an existing non-MX source id from the fixture (e.g. "da1").
        ArgumentException ex = Assert.Throws<ArgumentException>(() => settings.UpsertPlcGroup("da1", "Fast", 250));
        Assert.Contains("MX Component", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Upsert_RejectsUnknownSource_AndBlankNames()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        Assert.Throws<ArgumentException>(() => settings.UpsertPlcGroup("nope", "Fast", 250));
        Assert.Throws<ArgumentException>(() => settings.UpsertPlcGroup(sourceId, "   ", 250));
    }

    [Fact]
    public void Upsert_EnforcesSixteenGroupCap()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        for (int i = 0; i < SourceConfigMigration.MaxPlcGroupsPerSource; i++)
        {
            settings.UpsertPlcGroup(sourceId, $"G{i:00}", 100 * (i + 1));
        }

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => settings.UpsertPlcGroup(sourceId, "Overflow", 250));
        Assert.Contains("maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_DeletesDefinition_AndThrowsWhenMissing()
    {
        (DaRuntimeSettings settings, string sourceId) = CreateWithMxSource();
        settings.UpsertPlcGroup(sourceId, "Fast", 250);
        settings.RemovePlcGroup(sourceId, "fast");
        Assert.Empty(settings.GetSnapshot().GetSource(sourceId)!.PlcGroupsList);
        Assert.Throws<ArgumentException>(() => settings.RemovePlcGroup(sourceId, "Fast"));
    }
}
```

**Implementer note (binding):** the fixture body `CreateWithMxSource` MUST be filled in with the real construction pattern from `DaGroupIoModeTests` (which builds a `DaRuntimeSettings` against a temp persistence path). Seed TWO sources: `"mx1"` (SourceType `SourceTypes.MxComponent`, `new MxComponentSourceOptions(0, 3000, 2)`) and `"da1"` (SourceType `SourceTypes.OpcDa`, `new OpcDaSourceOptions("Server.1", "localhost", null, null, null)`), using whatever seed method that fixture uses. Do not alter the test methods.

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupRegistryTests -v q --nologo`
Expected: FAIL — `UpsertPlcGroup` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `DaRuntimeSettings`, after `RemoveUaSubscription` (~line 260), add (mirror of the UA pair, MX-gated):

```csharp
    /// <summary>Add or update a named PLC group on an MxComponent source. Throws ArgumentException
    /// for unknown sources, non-MX sources (PLC Groups are MX Component-only this iteration),
    /// invalid names, or past the 16-group cap. Clamps the rate to the 100 ms floor (spec §4).</summary>
    public DaRuntimeSettingsSnapshot UpsertPlcGroup(string sourceId, string name, int updateRateMs)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 64)
        {
            throw new ArgumentException("PLC group name must be 1-64 characters.", nameof(name));
        }

        int clampedRate = Math.Max(100, updateRateMs);

        lock (sync_)
        {
            List<DaSourceRuntimeSettings> sources = snapshot_.Sources.ToList();
            int index = sources.FindIndex(source =>
                string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                throw new ArgumentException($"Source '{sourceId}' does not exist.", nameof(sourceId));
            }

            if (!string.Equals(sources[index].SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Source '{sourceId}' is not an MX Component source; PLC Groups apply to MX Component sources only.",
                    nameof(sourceId));
            }

            DaSourceRuntimeSettings current = sources[index];
            List<PlcGroupSettings> groups = SourceConfigMigration
                .NormalizePlcGroups(current.PlcGroups)
                .ToList();
            PlcGroupSettings updated = new(trimmed, clampedRate);
            int groupIndex = groups.FindIndex(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (groupIndex >= 0)
            {
                groups[groupIndex] = updated;
            }
            else
            {
                if (groups.Count >= SourceConfigMigration.MaxPlcGroupsPerSource)
                {
                    throw new ArgumentException(
                        $"Source '{sourceId}' already has the maximum of {SourceConfigMigration.MaxPlcGroupsPerSource} PLC groups.");
                }

                groups.Add(updated);
            }

            sources[index] = current with { PlcGroups = groups };
            snapshot_ = snapshot_ with { Sources = sources, Version = snapshot_.Version + 1 };
            Persist();
            return snapshot_;
        }
    }

    /// <summary>Remove a named PLC group. Throws ArgumentException when the source/group doesn't exist
    /// or the source is not MX Component type. Member-tag reassignment runs through MappingStore
    /// at the API layer (mirrors the UA subscription remove flow).</summary>
    public DaRuntimeSettingsSnapshot RemovePlcGroup(string sourceId, string name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        lock (sync_)
        {
            List<DaSourceRuntimeSettings> sources = snapshot_.Sources.ToList();
            int index = sources.FindIndex(source =>
                string.Equals(source.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                throw new ArgumentException($"Source '{sourceId}' does not exist.", nameof(sourceId));
            }

            if (!string.Equals(sources[index].SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Source '{sourceId}' is not an MX Component source; PLC Groups apply to MX Component sources only.",
                    nameof(sourceId));
            }

            DaSourceRuntimeSettings current = sources[index];
            List<PlcGroupSettings> groups = SourceConfigMigration
                .NormalizePlcGroups(current.PlcGroups)
                .ToList();
            int groupIndex = groups.FindIndex(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (groupIndex < 0)
            {
                throw new ArgumentException($"Source '{sourceId}' has no PLC group named '{trimmed}'.", nameof(name));
            }

            groups.RemoveAt(groupIndex);
            sources[index] = current with { PlcGroups = groups };
            snapshot_ = snapshot_ with { Sources = sources, Version = snapshot_.Version + 1 };
            Persist();
            return snapshot_;
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupRegistryTests -v q --nologo`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DaRuntimeSettings.cs tests/OpcBridge.LoadTest/PlcGroupRegistryTests.cs
git commit -m "feat(plc-groups): UpsertPlcGroup/RemovePlcGroup registry with MX-only validation"
```

---

### Task 5: MappingStore — `ReassignPlcGroup` and detach hygiene

**Files:**
- Modify: `src/OpcBridge.App/MappingStore.cs` — new method after `ClearDaGroup` (~line 287); detach rule inside `TryUpdate` (line 91–108)
- Test: `tests/OpcBridge.LoadTest/PlcGroupMappingStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 (`TagMapping.PlcGroup`).
- Produces: `int ReassignPlcGroup(string sourceId, string groupName)` (batch move to default + zero `PollRateMs`, single persist/event, returns count). Plus invariant: `TryUpdate` transitioning a tag from non-empty `PlcGroup` to empty clears `PollRateMs` to 0 (no stale numeric overrides — spec §4). Task 8's remove endpoint consumes `ReassignPlcGroup`.

- [ ] **Step 1: Write the failing test**

Create `tests/OpcBridge.LoadTest/PlcGroupMappingStoreTests.cs` (copy the `MappingStore` temp-file fixture pattern from `MappingGroupTests.cs` — same construction, same temp-dir cleanup):

```csharp
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Group-rate-wins hygiene in the mapping store: batch reassign zeroes PollRateMs and
/// single-tag unassign via TryUpdate also drops the stale numeric override (spec §4).
/// </summary>
public sealed class PlcGroupMappingStoreTests
{
    private static TagMapping Tag(string itemId, string plcGroup = "", int pollRateMs = 0)
        => new() { SourceId = "mx1", ItemId = itemId, UaNodeId = $"ns=2;s={itemId}", PlcGroup = plcGroup, PollRateMs = pollRateMs };

    // Implementer: instantiate MappingStore exactly like MappingGroupTests does (temp path).

    [Fact]
    public void ReassignPlcGroup_MovesOnlyNamedMembers_ZeroesPollRate_SingleEvent()
    {
        MappingStore store = CreateStore(); // copy fixture from MappingGroupTests
        store.SetAll(new[] { Tag("D100", "Fast", 999), Tag("D101", "Fast"), Tag("D102", "Slow"), Tag("M0") });

        long beforeVersion = 0;
        int events = 0;
        store.Changed += _ => events++;

        int moved = store.ReassignPlcGroup("mx1", "fast"); // CI match

        Assert.Equal(2, moved);
        IReadOnlyList<TagMapping> all = store.GetSnapshot().Mappings;
        Assert.All(all.Where(m => m.ItemId is "D100" or "D101"), m =>
        {
            Assert.Equal(string.Empty, m.PlcGroup);
            Assert.Equal(0, m.PollRateMs);      // D100's stale 999 dropped
        });
        Assert.Equal("Slow", all.First(m => m.ItemId == "D102").PlcGroup);
        Assert.Equal(1, events);                 // ONE Changed event
    }

    [Fact]
    public void ReassignPlcGroup_NoMatches_ReturnsZero_NoEvent()
    {
        MappingStore store = CreateStore();
        store.SetAll(new[] { Tag("D100", "Fast") });
        int events = 0;
        store.Changed += _ => events++;

        Assert.Equal(0, store.ReassignPlcGroup("mx1", "Missing"));
        Assert.Equal(0, events);
    }

    [Fact]
    public void TryUpdate_UnassigningGroup_ZeroesStalePollRate()
    {
        MappingStore store = CreateStore();
        store.SetAll(new[] { Tag("D100", "Fast", 750) });

        TagMapping edited = Tag("D100", plcGroup: "", pollRateMs: 750); // UI sends same numeric rate
        Assert.True(store.TryUpdate(edited, out _));

        TagMapping stored = store.GetSnapshot().Mappings.First(m => m.ItemId == "D100");
        Assert.Equal(string.Empty, stored.PlcGroup);
        Assert.Equal(0, stored.PollRateMs);
    }

    [Fact]
    public void TryUpdate_AssigningGroup_KeepsNumericField_AsStored()
    {
        MappingStore store = CreateStore();
        store.SetAll(new[] { Tag("D100") });

        TagMapping edited = Tag("D100", plcGroup: "Fast", pollRateMs: 0);
        Assert.True(store.TryUpdate(edited, out _));
        Assert.Equal("Fast", store.GetSnapshot().Mappings.First(m => m.ItemId == "D100").PlcGroup);
    }
}
```

Adjust `GetSnapshot()` destructuring to the store's actual snapshot API used by `MappingGroupTests` (it returns `(IReadOnlyList<TagMapping>, long)` — destructure accordingly).

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupMappingStoreTests -v q --nologo`
Expected: FAIL — `ReassignPlcGroup` missing; unassign test fails (PollRateMs stays 750).

- [ ] **Step 3: Write minimal implementation**

(a) After `ClearDaGroup` (~line 287):

```csharp
    /// <summary>
    /// Moves every mapping of one source off a named PLC group back onto the source default
    /// (empty PlcGroup) and zeroes PollRateMs so no stale numeric override survives the
    /// "group rate wins" model (spec §4). Used when a PLC group is deleted. One lock pass,
    /// ONE Persist and ONE Changed event per call. Returns count moved.
    /// </summary>
    public int ReassignPlcGroup(string sourceId, string groupName)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(groupName))
        {
            return 0;
        }

        int moved;
        lock (sync_)
        {
            moved = 0;
            for (int i = 0; i < mappings_.Count; i++)
            {
                TagMapping m = mappings_[i];
                if (!string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals((m.PlcGroup ?? string.Empty).Trim(), groupName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                TagMapping copy = Normalize(m); copy.PlcGroup = string.Empty; copy.PollRateMs = 0; mappings_[i] = copy;
                moved++;
            }

            if (moved > 0) { version_++; Persist(); }
        }

        if (moved > 0) Changed?.Invoke(version_);
        return moved;
    }
```

(b) In `TryUpdate`, replace the plain `mappings_[index] = normalized;` (line ~108) with:

```csharp
            // Group-rate-wins hygiene (spec §4): unassigning a PLC group drops any stale
            // numeric rate override, mirroring ClearDaGroup/ReassignPlcGroup semantics.
            if (string.IsNullOrWhiteSpace(normalized.PlcGroup)
                && !string.IsNullOrWhiteSpace(mappings_[index].PlcGroup))
            {
                normalized.PollRateMs = 0;
            }

            mappings_[index] = normalized;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupMappingStoreTests -v q --nologo`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/MappingStore.cs tests/OpcBridge.LoadTest/PlcGroupMappingStoreTests.cs
git commit -m "feat(plc-groups): ReassignPlcGroup batch move and TryUpdate detach hygiene"
```

---

### Task 6: Cache — effective-rate resolution in `SourceMappingCache`

**Files:**
- Modify: `src/OpcBridge.App/BridgeWorker.cs` — `SourceMappingCache` (lines 1864–2007): `Build` overloads, resolver field, `ResolveEffectiveRate`, rewrite `GetDistinctRates`/`GetSourceReadMappingsByRate`
- Test: `tests/OpcBridge.LoadTest/PlcGroupRateResolutionTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces: `SourceMappingCache.Build(IReadOnlyList<TagMapping>, IReadOnlyList<InterlinkRule>)` keeps working (resolver defaults to empty); new overload `Build(mappings, rules, Func<string, IReadOnlyList<PlcGroupSettings>>? plcGroupsResolver)`. Rate precedence everywhere: assigned defined group → clamped group rate; else `PollRateMs > 0` → `PollRateMs`; else `defaultRate` argument (query-time, preserving live default-rate edits — spec §5). Callers unchanged. Task 7 wires the real resolver at the BridgeWorker Build call site.

- [ ] **Step 1: Write the failing test**

Create `tests/OpcBridge.LoadTest/PlcGroupRateResolutionTests.cs`:

```csharp
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Effective-rate resolution precedence (spec §5): defined group wins over per-tag PollRateMs;
/// unknown group names fall back; legacy per-tag and default paths untouched; resolution happens
/// at QUERY time so a live resolver swap is visible without rebuilding the cache.
/// </summary>
public sealed class PlcGroupRateResolutionTests
{
    private static TagMapping Tag(string itemId, string plcGroup = "", int pollRateMs = 0)
        => new() { SourceId = "mx1", ItemId = itemId, UaNodeId = $"ns=2;s={itemId}", PlcGroup = plcGroup, PollRateMs = pollRateMs };

    private static readonly IReadOnlyList<PlcGroupSettings> Groups = new[]
    {
        new PlcGroupSettings("Fast", 250),
        new PlcGroupSettings("Slow", 5000)
    };

    private static SourceMappingCache Build(
        IReadOnlyList<TagMapping> mappings,
        Func<string, IReadOnlyList<PlcGroupSettings>>? resolver = null)
        => SourceMappingCache.Build(mappings, Array.Empty<OpcBridge.App.InterlinkRule>(), resolver ?? (_ => Array.Empty<PlcGroupSettings>()));

    [Fact]
    public void DistinctRates_IncludesGroupRates_AndExcludesSupersededTagRates()
    {
        SourceMappingCache cache = Build(new[]
        {
            Tag("D100", "Fast", 9999),  // group wins -> 250 (9999 never appears)
            Tag("D101", "Slow"),        // -> 5000
            Tag("D102", pollRateMs: 1000), // legacy per-tag -> 1000
            Tag("M0")                   // default -> 2000
        });

        IReadOnlyList<int> rates = cache.GetDistinctRates("mx1", 2000);
        Assert.Equal(new[] { 250, 1000, 2000, 5000 }, rates.Order().ToArray());
    }

    [Fact]
    public void ByRate_GroupedTagsLandUnderTheirGroupRate_NotTheirNumericRate()
    {
        SourceMappingCache cache = Build(new[] { Tag("D100", "Fast", 9999) });

        Assert.Single(cache.GetSourceReadMappingsByRate("mx1", 250, 2000));
        Assert.Empty(cache.GetSourceReadMappingsByRate("mx1", 9999, 2000));
    }

    [Fact]
    public void UnknownGroupName_FallsBack_LikeUnassigned()
    {
        SourceMappingCache cache = Build(new[] { Tag("D100", "Ghost", 750) });
        Assert.Equal(new[] { 750 }, cache.GetDistinctRates("mx1", 2000).Order().ToArray());
        Assert.Single(cache.GetSourceReadMappingsByRate("mx1", 750, 2000));
    }

    [Fact]
    public void QueryTimeResolution_ResolverSwap_VisibleWithoutRebuild()
    {
        IReadOnlyList<PlcGroupSettings> initial = new[] { new PlcGroupSettings("Fast", 250) };
        IReadOnlyList<PlcGroupSettings> updated = new[] { new PlcGroupSettings("Fast", 400) };
        IReadOnlyList<PlcGroupSettings>? current = initial;

        SourceMappingCache cache = Build(new[] { Tag("D100", "Fast") }, _ => current!);

        Assert.Contains(250, cache.GetDistinctRates("mx1", 2000));
        current = updated;                          // settings snapshot moved underneath
        Assert.Contains(400, cache.GetDistinctRates("mx1", 2000)); // no rebuild needed
        Assert.DoesNotContain(250, cache.GetDistinctRates("mx1", 2000));
    }

    [Fact]
    public void OtherSources_Unaffected_ByMxGroups()
    {
        SourceMappingCache cache = Build(new[]
        {
            new TagMapping { SourceId = "da1", ItemId = "Item.A", UaNodeId = "x" },
            Tag("D100", "Fast")
        }, _ => Groups);

        Assert.Equal(new[] { 2000 }, cache.GetDistinctRates("da1", 2000));
    }
}
```

Check the `InterlinkRule` namespace/shape at its definition and adjust the fully-qualified reference if needed.

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupRateResolutionTests -v q --nologo`
Expected: FAIL — no `Build` overload taking a resolver.

- [ ] **Step 3: Write minimal implementation**

In `SourceMappingCache` (BridgeWorker.cs):

(a) Add field + ctor param:

```csharp
        private readonly Func<string, IReadOnlyList<PlcGroupSettings>> _plcGroupsResolver;
```

thread it through both private ctors; the existing public `Build(mappings)` / `Build(mappings, rules)` overloads delegate with `_ => Array.Empty<PlcGroupSettings>()`.

(b) New overload:

```csharp
        public static SourceMappingCache Build(
            IReadOnlyList<TagMapping> mappings,
            IReadOnlyList<InterlinkRule> rules,
            Func<string, IReadOnlyList<PlcGroupSettings>>? plcGroupsResolver)
        {
            // existing body; pass (plcGroupsResolver ?? (_ => Array.Empty<PlcGroupSettings>())) to the ctor
        }
```

(c) Resolution helper + rewritten queries (replace bodies at lines ~1981–2007):

```csharp
        private int ResolveEffectiveRate(TagMapping mapping, string sourceId, int defaultRate)
        {
            string requested = (mapping.PlcGroup ?? string.Empty).Trim();
            if (requested.Length > 0)
            {
                foreach (PlcGroupSettings group in _plcGroupsResolver(sourceId))
                {
                    if (string.Equals(group.Name.Trim(), requested, StringComparison.OrdinalIgnoreCase))
                    {
                        return Math.Max(100, group.UpdateRateMs);
                    }
                }
            }

            return mapping.PollRateMs > 0 ? mapping.PollRateMs : defaultRate;
        }

        public IReadOnlyList<int> GetDistinctRates(string sourceId, int defaultRate)
        {
            if (!mappings_by_source_.TryGetValue(sourceId, out SourceMappingSet? mappings))
            {
                return [defaultRate];
            }

            HashSet<int> rates = new();
            for (int i = 0; i < mappings.SourceRead.Count; i++)
            {
                rates.Add(ResolveEffectiveRate(mappings.SourceRead[i], sourceId, defaultRate));
            }

            return rates.Count > 0 ? rates.ToArray() : new[] { defaultRate };
        }

        public IReadOnlyList<TagMapping> GetSourceReadMappingsByRate(string sourceId, int rate, int defaultRate)
        {
            if (!mappings_by_source_.TryGetValue(sourceId, out SourceMappingSet? mappings))
            {
                return EmptyMappings;
            }

            return mappings.SourceRead
                .Where(m => ResolveEffectiveRate(m, sourceId, defaultRate) == rate)
                .ToArray();
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupRateResolutionTests -v q --nologo`
Expected: PASS (5 tests). Full suite must stay green — old `Build` callers compile against unchanged overloads.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/BridgeWorker.cs tests/OpcBridge.LoadTest/PlcGroupRateResolutionTests.cs
git commit -m "feat(plc-groups): query-time effective-rate resolution in SourceMappingCache"
```

---

### Task 7: Worker wiring — live resolver + restart-on-group-edit trigger

**Files:**
- Modify: `src/OpcBridge.App/BridgeWorker.cs` — Build call site (~line 224); `ReconfigureSessionsAsync` MX branch after the UA-def block (~line 926–952); static seam near `SourceConnectionEquals` (~line 1140s)
- Test: extend `tests/OpcBridge.LoadTest/PlcGroupSettingsTests.cs` + `tests/OpcBridge.LoadTest/SourceConnectionEqualsTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 6.
- Produces: internal static `bool ShouldRestartPollersForPlcGroups(DaSourceRuntimeSettings existing, DaSourceRuntimeSettings candidate)` (true iff `!existing.PlcGroupsEqual(candidate)`); the Build call site passes `sourceId => da_settings_.GetSnapshot().GetSource(sourceId)?.PlcGroupsList ?? Array.Empty<PlcGroupSettings>()`. Behavioral contract (spec §5): group create/delete/re-rate adds ONLY that source to the `changed` set; `SourceConnectionEquals` stays untouched (session never reopened).

- [ ] **Step 1: Write the failing test**

Append to `PlcGroupSettingsTests.cs`:

```csharp
[Fact]
public void ShouldRestartPollersForPlcGroups_DefinitionChange_True_ElseFalse()
{
    DaSourceRuntimeSettings applied = MxSource(new[] { new PlcGroupSettings("Fast", 250) });

    Assert.True(BridgeWorker.ShouldRestartPollersForPlcGroups(applied, MxSource(new[] { new PlcGroupSettings("Fast", 400) })));
    Assert.True(BridgeWorker.ShouldRestartPollersForPlcGroups(applied, MxSource(Array.Empty<PlcGroupSettings>())));
    Assert.True(BridgeWorker.ShouldRestartPollersForPlcGroups(MxSource(null), MxSource(new[] { new PlcGroupSettings("New", 100) })));

    // Unrelated settings churn must NOT trigger a restart.
    Assert.False(BridgeWorker.ShouldRestartPollersForPlcGroups(applied, MxSource(new[] { new PlcGroupSettings("Fast", 250) })));
}

[Fact]
public void ConnectionEquality_IgnoresPlcGroupChanges()
{
    DaSourceRuntimeSettings applied = MxSource(new[] { new PlcGroupSettings("Fast", 250) });
    DaSourceRuntimeSettings regrouped = MxSource(new[] { new PlcGroupSettings("Slow", 5000) });

    // SourceConnectionEquals must remain true across ANY group edit (spec §5: no session reopen).
    Assert.True(applied.SourceConnectionEquals(regrouped));
}
```

(`SourceConnectionEquals` is BridgeWorker's internal comparer — if it is private, mark the comparison helper `internal` and enable `InternalsVisibleTo` if the test project already has it; `SourceConnectionEqualsTests.cs` shows the established access pattern — follow it.)

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter "PlcGroupSettingsTests|SourceConnectionEqualsTests" -v q --nologo`
Expected: FAIL — `ShouldRestartPollersForPlcGroups` missing.

- [ ] **Step 3: Write minimal implementation**

(a) Static seam (place near `SourceConnectionEquals`, BridgeWorker.cs ~line 1140s):

```csharp
    /// <summary>True when a source's PLC group definitions changed — those sources need their
    /// pollers restarted (rate buckets moved), but never a session rebuild (spec §5).</summary>
    internal static bool ShouldRestartPollersForPlcGroups(
        DaSourceRuntimeSettings existing,
        DaSourceRuntimeSettings candidate)
        => !existing.PlcGroupsEqual(candidate);
```

(b) Build call site (~line 224) — pass the live resolver:

```csharp
                            cacheHolder.Cache = SourceMappingCache.Build(
                                mappings,
                                rules,
                                sourceId => da_settings_.GetSnapshot()
                                    .GetSource(sourceId)?.PlcGroupsList ?? Array.Empty<PlcGroupSettings>());
```

Keep the older 2-arg overload for any remaining callers (they default to no groups).

(c) In `ReconfigureSessionsAsync`, immediately AFTER the UA-def reconcile block (ends line ~952), inside the same `if (sessions.TryGetValue(...) && !force && SourceConnectionEquals(...))` branch, add:

```csharp
                // PLC group definition changes need a POLLER restart only: rate buckets are
                // bridge-side timers over the existing COM session (spec §5). Resolver-based
                // cache resolution picks up the new definitions without a rebuild.
                if (ShouldRestartPollersForPlcGroups(existing.Source, source))
                {
                    changed.Add(source.SourceId);
                    sessions[source.SourceId] = new SourceSession(source, existing.Client)
                    {
                        PollerCts = existing.PollerCts
                    };
                }
```

(The caller at line ~368–380 already routes every `changed` id through `RestartPollersForSourcesAsync`; stop/start is idempotent per `{sourceId}:{rate}` key, so double-triggers alongside the mappingsChanged path are harmless.)

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter "PlcGroupSettingsTests|SourceConnectionEqualsTests" -v q --nologo`
Expected: PASS. Then full suite: `"$HOME/.dotnet/dotnet" test OpcBridge.sln -v q --nologo` — expect 558 + new tests, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/BridgeWorker.cs tests/OpcBridge.LoadTest/PlcGroupSettingsTests.cs tests/OpcBridge.LoadTest/SourceConnectionEqualsTests.cs
git commit -m "feat(plc-groups): live resolver wiring and restart-on-group-edit trigger without session rebuild"
```

---

### Task 8: HTTP API — `/api/plc/groups`, `/remove`, GET; mapping pass-through

**Files:**
- Create: `src/OpcBridge.App/PlcGroupRequests.cs`
- Modify: `src/OpcBridge.App/Program.cs` — endpoints after the UA subscription endpoints (~line 1813); `MappingTagDto` in `MappingRequests.cs` (append `string? PlcGroup = null` LAST); request mapper at line ~2707 (`PlcGroup = tag.PlcGroup ?? string.Empty`)
- Test: `tests/OpcBridge.LoadTest/PlcGroupApiTests.cs`

**Interfaces:**
- Consumes: Tasks 4, 5.
- Produces (HTTP contract, spec §6):
  - `POST /api/plc/groups` body `{sourceId, name, updateRateMs}` → `200 {ok:true, version}` | `400 {error}`
  - `POST /api/plc/groups/remove` body `{sourceId, name}` → `200 {ok:true, version, movedMappings}` | `400 {error}` (calls `RemovePlcGroup` then `store.ReassignPlcGroup`)
  - `GET /api/plc/groups?sourceId=` → `{sources:[{sourceId, displayName, defaultUpdateRateMs, effectiveRates:[int], groups:[{name, updateRateMs, memberCount}]}]}` filtered to MxComponent sources
  - `MappingTagDto.PlcGroup` flows into add/bulk-add/update; `GET /api/mappings` emits `plcGroup` automatically via the serializer.

- [ ] **Step 1: Write the failing test**

Create `tests/OpcBridge.LoadTest/PlcGroupApiTests.cs` — follow the WebApplicationFactory/client pattern used by `BridgeAppApiTests.cs` (same collection fixture; copy its app bootstrap and `POST` JSON helper):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// /api/plc/groups contract: upsert/remove happy paths, every 400 branch, and the GET payload
/// shape (definitions + member counts + effective distinct rates, MX sources only — spec §6).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class PlcGroupApiTests
{
    // Implementer: reuse BridgeAppApiTests' factory field/accessor and its seed-source helper.
    // Tests assume seeded sources: "mx1" (MxComponent) and "da1" (OpcDa) — extend the fixture's
    // seed exactly like BridgeAppApiTests seeds additional sources.

    private readonly InterlinkApiAppFixture _fx; // rename to the actual fixture type used by BridgeAppApiTests

    public PlcGroupApiTests(InterlinkApiAppFixture fx) => _fx = fx;

    private HttpClient Client => _fx.Client; // match BridgeAppApiTests' access pattern

    [Fact]
    public async Task Upsert_Remove_RoundTrip_ReportsMovedMappings()
    {
        // seed a tag assigned to the group first (via /api/mappings/add with plcGroup)
        // ... see implementer note below ...

        HttpResponseMessage up = await Client.PostAsJsonAsync("/api/plc/groups",
            new { sourceId = "mx1", name = "Fast", updateRateMs = 1 }); // 1 -> clamped to 100 server-side
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        HttpResponseMessage rm = await Client.PostAsJsonAsync("/api/plc/groups/remove",
            new { sourceId = "mx1", name = "Fast" });
        Assert.Equal(HttpStatusCode.OK, rm.StatusCode);
        string body = await rm.Content.ReadAsStringAsync();
        Assert.Contains("\"movedMappings\":", body);
    }

    [Fact]
    public async Task Upsert_NonMxSource_Returns400()
    {
        HttpResponseMessage resp = await Client.PostAsJsonAsync("/api/plc/groups",
            new { sourceId = "da1", name = "Fast", updateRateMs = 250 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("MX Component", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upsert_BlankName_Returns400()
    {
        HttpResponseMessage resp = await Client.PostAsJsonAsync("/api/plc/groups",
            new { sourceId = "mx1", name = "  ", updateRateMs = 250 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Get_ListsOnlyMxSources_WithMemberCounts()
    {
        HttpResponseMessage resp = await Client.GetAsync("/api/plc/groups");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("mx1", body);
        Assert.DoesNotContain("\"sourceId\":\"da1\"", body);
    }
}
```

**Implementer notes (binding):**
- Replace `InterlinkApiAppFixture` with the REAL fixture type/name from `BridgeAppApiTests.cs` (open it first; copy its field names, seeding, and JSON helper verbatim where possible).
- For `Upsert_Remove_RoundTrip_ReportsMovedMappings`, before the upsert POST, add one mapping bound to the group using the same JSON shape `BridgeAppApiTests` uses for `/api/mappings/add`, including `"plcGroup": "Fast"`, `"pollRateMs": 750`; assert `movedMappings >= 1` by parsing the response JSON instead of the substring check.
- Assert the clamp: after upsert with `updateRateMs = 1`, `GET /api/plc/groups?sourceId=mx1` shows `updateRateMs: 100` for "Fast".

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupApiTests -v q --nologo`
Expected: FAIL — 404 on `/api/plc/groups`.

- [ ] **Step 3: Write minimal implementation**

(a) Create `src/OpcBridge.App/PlcGroupRequests.cs`:

```csharp
namespace OpcBridge.App;

public sealed record PlcGroupUpsertRequest(string SourceId, string Name, int UpdateRateMs);

public sealed record PlcGroupRemoveRequest(string SourceId, string Name);
```

(b) In `Program.cs` after the `/api/ua/subscriptions/remove` endpoint (~line 1813):

```csharp
app.MapPost("/api/plc/groups", (PlcGroupUpsertRequest request, DaRuntimeSettings settings) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "sourceId is required." });
    }

    try
    {
        DaRuntimeSettingsSnapshot snapshot = settings.UpsertPlcGroup(request.SourceId, request.Name, request.UpdateRateMs);
        return Results.Ok(new { ok = true, version = snapshot.Version });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/plc/groups/remove", (PlcGroupRemoveRequest request, DaRuntimeSettings settings, MappingStore store) =>
{
    try
    {
        DaRuntimeSettingsSnapshot snapshot = settings.RemovePlcGroup(request.SourceId, request.Name);
        int movedMappings = store.ReassignPlcGroup(request.SourceId, request.Name);
        return Results.Ok(new { ok = true, version = snapshot.Version, movedMappings });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/plc/groups", (DaRuntimeSettings settings, MappingStore store, string? sourceId) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    (IReadOnlyList<TagMapping> mappings, _) = store.GetSnapshot();

    var sources = snapshot.Sources
        .Where(s => string.Equals(s.SourceType, SourceTypes.MxComponent, StringComparison.OrdinalIgnoreCase))
        .Where(s => string.IsNullOrWhiteSpace(sourceId)
            || string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
        .Select(s =>
        {
            List<TagMapping> sourceMappings = mappings
                .Where(m => string.Equals(m.SourceId, s.SourceId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Effective distinct rates per spec §6: group rate wins, else per-tag, else bridge default.
            HashSet<int> effectiveRates = new();
            foreach (TagMapping m in sourceMappings)
            {
                string requested = (m.PlcGroup ?? string.Empty).Trim();
                int rate = m.PollRateMs;
                if (requested.Length > 0)
                {
                    PlcGroupSettings? def = s.PlcGroupsList.FirstOrDefault(g =>
                        string.Equals(g.Name.Trim(), requested, StringComparison.OrdinalIgnoreCase));
                    if (def is not null)
                    {
                        rate = Math.Max(100, def.UpdateRateMs);
                    }
                }

                effectiveRates.Add(rate > 0 ? rate : snapshot.UpdateRateMs);
            }

            return new
            {
                sourceId = s.SourceId,
                displayName = s.DisplayName,
                defaultUpdateRateMs = snapshot.UpdateRateMs,
                effectiveRates = effectiveRates.Order().ToArray(),
                groups = s.PlcGroupsList
                    .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        name = g.Name,
                        updateRateMs = g.UpdateRateMs,
                        memberCount = sourceMappings.Count(m =>
                            string.Equals((m.PlcGroup ?? string.Empty).Trim(), g.Name, StringComparison.OrdinalIgnoreCase))
                    })
                    .ToArray()
            };
        })
        .ToArray();

    return Results.Json(new { sources });
});
```

(c) `MappingRequests.cs`: append `string? PlcGroup = null` as the LAST parameter of `MappingTagDto`.

(d) Request mapper (~line 2707): add `PlcGroup = tag.PlcGroup ?? string.Empty` after `Subscription = ...`.

(e) Check `/api/mappings/update`'s handler (line ~1266): confirm it constructs the updated `TagMapping` through the same mapper — if it maps fields individually rather than via the shared mapper at 2690, add the `PlcGroup` line there too (grep `Subscription = ` within the update handler to find every construction site).

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter PlcGroupApiTests -v q --nologo`
Expected: PASS (4+ tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/PlcGroupRequests.cs src/OpcBridge.App/Program.cs src/OpcBridge.App/MappingRequests.cs tests/OpcBridge.LoadTest/PlcGroupApiTests.cs
git commit -m "feat(plc-groups): /api/plc/groups endpoints and mapping plcGroup pass-through"
```

---

### Task 9: Effective-rate display — `DashboardValues` lookup extension

**Files:**
- Modify: `src/OpcBridge.App/DashboardValues.cs` — `ResolveEffectiveRate` (lines 63–83) and `BuildUpdateRateLookup` overloads (lines 37–61)
- Modify: `src/OpcBridge.App/Program.cs` — caller at lines ~378–387
- Test: extend `tests/OpcBridge.LoadTest/DashboardValuesTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `BuildUpdateRateLookup(mappings, sourceDefaultRates, uaSubscriptionsBySource, plcGroupsBySource)` (new optional 4th param, `Func<string, IReadOnlyList<PlcGroupSettings>>?` default null — same live-resolver pattern as Task 6). Precedence in `ResolveEffectiveRate`: UA sub (UA sources) OR PLC group (MX sources) → its clamped rate; else per-tag; else source default. Task 10's tab reuses GET-computed counts, not this path; this task fixes tag-table/facelate rate displays.

- [ ] **Step 1: Write the failing test**

Append to `tests/OpcBridge.LoadTest/DashboardValuesTests.cs` (match its existing `[Fact]` style and helpers):

```csharp
[Fact]
public void BuildUpdateRateLookup_PlcGroupWinsOverPerTagRate_UnknownFallsThrough()
{
    var mappings = new[]
    {
        new TagMapping { SourceId = "mx1", ItemId = "D100", PlcGroup = "Fast", PollRateMs = 999 },
        new TagMapping { SourceId = "mx1", ItemId = "D101", PlcGroup = "Ghost", PollRateMs = 750 },
        new TagMapping { SourceId = "mx1", ItemId = "D102", PollRateMs = 0 }
    };
    var sourceRates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mx1"] = 2000 };
    var mxGroups = new Dictionary<string, IReadOnlyList<PlcGroupSettings>>(StringComparer.OrdinalIgnoreCase)
    {
        ["mx1"] = new[] { new PlcGroupSettings("Fast", 250) }
    };

    Dictionary<string, int> lookup = DashboardValues.BuildUpdateRateLookup(
        mappings, sourceRates, DashboardValuesTestsHelpers.EmptyUaSubs(), mxGroups);

    Assert.Equal(250, DashboardValues.LookupUpdateRate(lookup, "mx1", "D100")); // group wins
    Assert.Equal(750, DashboardValues.LookupUpdateRate(lookup, "mx1", "D101")); // unknown -> per-tag
    Assert.Equal(2000, DashboardValues.LookupUpdateRate(lookup, "mx1", "D102")); // default
}
```

If `DashboardValuesTests` has no shared "empty UA subscriptions" helper, pass the same empty-dictionary expression the existing tests use for the 3-arg overload's third argument (open the file and copy its convention; alternatively call the new overload with `null` UA map if a nullable overload exists after your edit — prefer matching file-local style).

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter DashboardValuesTests -v q --nologo`
Expected: FAIL — no 4-parameter overload.

- [ ] **Step 3: Write minimal implementation**

(a) New overload + threaded parameter (keep existing signatures intact):

```csharp
    public static Dictionary<string, int> BuildUpdateRateLookup(
        IReadOnlyList<TagMapping> mappings,
        IReadOnlyDictionary<string, int> sourceDefaultRates,
        IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionSettings>> uaSubscriptionsBySource,
        Func<string, IReadOnlyList<PlcGroupSettings>>? plcGroupsResolver = null)
    {
        Dictionary<string, int> lookup = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            uaSubscriptionsBySource.TryGetValue(mapping.SourceId,
                out IReadOnlyList<UaSubscriptionSettings>? subs);
            IReadOnlyList<PlcGroupSettings> groups =
                plcGroupsResolver?.Invoke(mapping.SourceId) ?? Array.Empty<PlcGroupSettings>();
            int rate = ResolveEffectiveRate(mapping, sourceDefaultRates, subs, groups);
            lookup[BridgeState.NormalizeKey(mapping.SourceId, mapping.ItemId)] = rate;
        }

        return lookup;
    }
```

(b) Extend `ResolveEffectiveRate` with a 4th parameter `IReadOnlyList<PlcGroupSettings>? plcGroups` and, BEFORE the UA-sub block (MX sources never have UA subs, so ordering is safe either way — group-first matches spec §5 wording):

```csharp
        string requestedGroup = (mapping.PlcGroup ?? string.Empty).Trim();
        if (requestedGroup.Length > 0 && plcGroups is not null)
        {
            for (int i = 0; i < plcGroups.Count; i++)
            {
                if (string.Equals(plcGroups[i].Name.Trim(), requestedGroup, StringComparison.OrdinalIgnoreCase))
                {
                    return Math.Max(100, plcGroups[i].UpdateRateMs);
                }
            }
        }
```

(c) Caller in Program.cs (~line 387): pass the resolver

```csharp
    Dictionary<string, int> updateRateByKey = DashboardValues.BuildUpdateRateLookup(
        mappings, sourceRates, uaSubscriptionsBySource,
        sourceId => daSnapshot.GetSource(sourceId)?.PlcGroupsList ?? Array.Empty<PlcGroupSettings>());
```

(match the surrounding variable name for the snapshot — open lines 378–387 first and reuse exactly what's there).

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter DashboardValuesTests -v q --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DashboardValues.cs src/OpcBridge.App/Program.cs tests/OpcBridge.LoadTest/DashboardValuesTests.cs
git commit -m "feat(plc-groups): effective-rate display honors PLC group membership"
```

---

### Task 10: Dashboard — PLC Groups tab

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` — nav button (Sources nav-group, line ~554–563, after the MX Component button); route map (~line 3068); view div (after `view-ua-subs`, ~line 925+); JS functions (after the UA subs JS block ending ~line 5340); header comment block (lines 26–29 area)
- Test: extend `tests/OpcBridge.LoadTest/DashboardPageTests.cs`

**Interfaces:**
- Consumes: Task 8 endpoints (`GET/POST /api/plc/groups[/remove]`).
- Produces: tab `data-tab="plc-groups"`, route `connectivity/plc-groups`, view `view-plc-groups`, JS globals `loadPlcGroups()`, `renderPlcGroupsForSource(src)`, `openPlcGroupAdd(sourceId)`, `openPlcGroupEdit(sourceId, name, rate)`, `deletePlcGroup(sourceId, name, memberCount)`, `plcGroupModalSave()`. Modal element ids `plcGroupModal`, `plcGroupName`, `plcGroupRate`, `plcGroupModalSaveBtn`, `plcGroupSourcePicker`. Task 11 links from these ids.

- [ ] **Step 1: Write the failing test**

Append to `tests/OpcBridge.LoadTest/DashboardPageTests.cs` (follow its string-content assertion style):

```csharp
[Fact]
public void Html_ContainsPlcGroupsTab_AndRoute()
{
    Assert.Contains("data-route=\"connectivity/plc-groups\"", DashboardPage.Html);
    Assert.Contains("id=\"view-plc-groups\"", DashboardPage.Html);
    Assert.Contains(">PLC Groups</button>", DashboardPage.Html);
}

[Fact]
public void Html_PlcGroupsTab_WiresCrudFunctions()
{
    Assert.Contains("function loadPlcGroups(", DashboardPage.Html);
    Assert.Contains("function plcGroupModalSave(", DashboardPage.Html);
    Assert.Contains("function deletePlcGroup(", DashboardPage.Html);
    Assert.Contains("/api/plc/groups/remove", DashboardPage.Html);
    Assert.Contains("/api/plc/groups", DashboardPage.Html);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter DashboardPageTests -v q --nologo`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

Work inside the `DashboardPage.Html` raw string literal. Mirror the UA Subs tab (`view-ua-subs` at line 925, JS at 5176–5340) structurally — same card/table classes (`dag-card` grid, `modal-f` footer), same `esc()`/`el()`/`navigate()` helpers.

(a) Nav button — insert AFTER the MX Component button (line ~562):

```html
    <button class="tabbtn" data-tab="plc-groups" data-route="connectivity/plc-groups" onclick="navigate('connectivity/plc-groups')">PLC Groups</button>
```

(b) Route map (~line 3068) — add entry `'connectivity/plc-groups': 'plc-groups',` beside the others, and hook the tab-activation loader where `activeTab === 'opc-da-groups'` is handled (line ~3133):

```javascript
  if (activeTab === 'plc-groups') {
    await loadPlcGroups().catch(e => el('plcMsg').textContent = '✗ ' + e.message);
  }
```

(c) View section (place after `view-ua-subs` closes):

```html
<div class="view" id="view-plc-groups">
  <div class="tabbar-h"><h2>PLC Groups</h2></div>
  <p class="hint">Named polling groups for MX Component sources. Each group polls its member tags at its own update rate; tags without a group ride the source default rate. MX Component has no push model — groups are bridge-side timers sharing the PLC link (Programming Manual sh081085 §5.2).</p>
  <div class="row" style="display:flex;gap:8px;align-items:center;margin-bottom:10px">
    <select id="plcGroupSourcePicker" onchange="renderPlcGroupsAll()" style="max-width:280px"></select>
    <span id="plcMsg" class="msg"></span>
    <button class="btn ghost" type="button" style="margin-left:auto" onclick="openPlcGroupAdd(document.getElementById('plcGroupSourcePicker').value)">+ Add Group</button>
  </div>
  <div id="plcGroupsList" class="dag-grid"></div>
</div>

<div class="modal-backdrop" id="plcGroupModal" style="display:none">
  <div class="modal">
    <h3 id="plcGroupModalTitle">Add PLC Group</h3>
    <label class="fl">Name<input id="plcGroupName" maxlength="64" placeholder="Fast"/></label>
    <label class="fl">Update rate (ms, min 100)<input id="plcGroupRate" type="number" min="100" step="50" value="1000"/></label>
    <div class="modal-f">
      <button class="btn ghost" type="button" onclick="closePlcGroupModal()">Cancel</button>
      <button class="btn" type="button" id="plcGroupModalSaveBtn" onclick="plcGroupModalSave()">Save</button>
    </div>
  </div>
</div>
```

Reuse the exact modal/backdrop CSS classes the UA modal uses (inspect around line 956) — do not invent new classes.

(d) JavaScript (after the UA subs JS functions):

```javascript
let plcGroupsCache = [];
async function loadPlcGroups() {
    const r = await fetch('/api/plc/groups');
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const data = await r.json();
    plcGroupsCache = data.sources || [];
    renderPlcGroupSourcePicker();
    renderPlcGroupsAll();
}
function renderPlcGroupSourcePicker() {
    const sel = el('plcGroupSourcePicker'); if (!sel) return;
    sel.innerHTML = plcGroupsCache.map(s => '<option value="' + esc(s.sourceId) + '">' + esc(s.displayName || s.sourceId) + '</option>').join('');
}
function renderPlcGroupsAll() {
    const sid = el('plcGroupSourcePicker')?.value;
    const src = plcGroupsCache.find(s => s.sourceId === sid);
    const host = el('plcGroupsList'); if (!host) return;
    if (!src) { host.innerHTML = '<p class="hint">No MX Component sources configured.</p>'; return; }
    const rows = (src.groups || []).map(g => {
        const gid = esc(src.sourceId), gn = esc(g.name).replace(/'/g, "\\'");
        return '<div class="dag-card"><div class="dag-card-h"><b>' + esc(g.name) + '</b>' +
            '<span class="badge">' + g.updateRateMs + ' ms</span></div>' +
            '<div class="dag-meta">' + g.memberCount + ' tag' + (g.memberCount === 1 ? '' : 's') +
            ' · effective rates: ' + (src.effectiveRates || []).join(', ') + ' ms</div>' +
            '<div class="modal-f"><button class="btn ghost" type="button" onclick="openPlcGroupEdit(\'' + gid + '\', \'' + gn + '\', ' + g.updateRateMs + ')">Edit</button>' +
            '<button class="btn ghost" type="button" onclick="deletePlcGroup(\'' + gid + '\', \'' + gn + '\', ' + g.memberCount + ')">Delete</button></div></div>';
    }).join('');
    host.innerHTML = rows || '<p class="hint">No groups yet — click + Add Group.</p>';
}
function openPlcGroupAdd(sourceId) {
    el('plcGroupModalTitle').textContent = 'Add PLC Group';
    el('plcGroupName').value = ''; el('plcGroupRate').value = 1000;
    el('plcGroupName').dataset.editFrom = ''; el('plcGroupName').dataset.sourceId = sourceId || '';
    const b = el('plcGroupModalSaveBtn'); if (b) b.disabled = false;
    el('plcGroupModal').style.display = 'flex';
}
function openPlcGroupEdit(sourceId, name, rate) {
    el('plcGroupModalTitle').textContent = 'Edit PLC Group';
    el('plcGroupName').value = name; el('plcGroupRate').value = rate;
    el('plcGroupName').dataset.editFrom = name; el('plcGroupName').dataset.sourceId = sourceId;
    const b = el('plcGroupModalSaveBtn'); if (b) b.disabled = false;
    el('plcGroupModal').style.display = 'flex';
}
function closePlcGroupModal() { el('plcGroupModal').style.display = 'none'; }
async function plcGroupModalSave() {
    const b = el('plcGroupModalSaveBtn'); if (b) b.disabled = true;
    try {
        const sourceId = el('plcGroupName').dataset.sourceId;
        const name = el('plcGroupName').value.trim();
        const rate = parseInt(el('plcGroupRate').value, 10) || 100;
        if (!name) throw new Error('Name is required.');
        if (rate < 100) throw new Error('Update rate must be at least 100 ms.');
        const r = await fetch('/api/plc/groups', { method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sourceId, name, updateRateMs: rate }) });
        if (!r.ok) throw new Error((await r.json()).error || ('HTTP ' + r.status));
        closePlcGroupModal();
        await loadPlcGroups();
    } catch (e) { el('plcMsg').textContent = '✗ ' + e.message; }
    finally { const bb = el('plcGroupModalSaveBtn'); if (bb) bb.disabled = false; }
}
async function deletePlcGroup(sourceId, name, memberCount) {
    if (memberCount > 0 && !confirm(memberCount + ' tag(s) will move to the source default rate. Delete "' + name + '"?')) return;
    try {
        const r = await fetch('/api/plc/groups/remove', { method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sourceId, name }) });
        if (!r.ok) throw new Error((await r.json()).error || ('HTTP ' + r.status));
        await loadPlcGroups();
    } catch (e) { el('plcMsg').textContent = '✗ ' + e.message; }
}
```

Adapt `el()`/`esc()` to the file's actual helper names (they exist — the UA JS uses them). Match the UA tab's modal show/hide mechanics exactly (some modals toggle classes instead of inline styles — copy whichever `closeUaSubModal()` does).

(e) Update the file-header comment block (lines ~26–29) with one line documenting the new tab/function names, matching the existing annotation style.

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter DashboardPageTests -v q --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs tests/OpcBridge.LoadTest/DashboardPageTests.cs
git commit -m "feat(plc-groups): dashboard PLC Groups tab with CRUD modals"
```

---

### Task 11: Faceplate — PLC Group dropdown for MX-source tags

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` — faceplate field row (~line 1316, next to `fpSubscriptionField`); option-builder + save plumbing (~lines 2998–3010, 4985–5010, and the faceplate save collector where `subscription` is included)
- Test: extend `tests/OpcBridge.LoadTest/DashboardPageTests.cs`

**Interfaces:**
- Consumes: Task 8 (`MappingTagDto.PlcGroup`), Task 10 (`loadPlcGroups` cache).
- Produces: faceplate select `id="fpPlcGroup"` inside `id="fpPlcGroupField"`, populated with *Default* + the tag source's groups (visible only when the faceplate's source is MxComponent); saving includes `plcGroup` in the update payload; while grouped, the rate input displays the effective rate read-only with hint "set by group '<name>'".

- [ ] **Step 1: Write the failing test**

Append to `DashboardPageTests.cs`:

```csharp
[Fact]
public void Html_Faceplate_HasPlcGroupField_AndBuilder()
{
    Assert.Contains("id=\"fpPlcGroupField\"", DashboardPage.Html);
    Assert.Contains("id=\"fpPlcGroup\"", DashboardPage.Html);
    Assert.Contains("function fpPlcGroupOptions(", DashboardPage.Html);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter DashboardPageTests -v q --nologo`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

(a) Field markup — insert next to `fpSubscriptionField` (line ~1316):

```html
                <div class="field" id="fpPlcGroupField" style="display:none"><label class="fl">PLC Group</label><select id="fpPlcGroup"></select><span class="msg" id="fpPlcGroupHint"></span></div>
```

(b) Option builder (near `fpSubscriptionOptions`, line ~4985):

```javascript
function fpPlcGroupOptions(selected) {
    const sid = window.__fpSourceId || '';
    const src = (typeof plcGroupsCache !== 'undefined' ? plcGroupsCache : []).find(s => s.sourceId === sid);
    const opts = ['<option value="">Default</option>'];
    if (src) for (const g of (src.groups || [])) {
        opts.push('<option value="' + esc(g.name) + '"' + (String(selected || '').toLowerCase() === String(g.name).toLowerCase() ? ' selected' : '') + '>' + esc(g.name) + ' (' + g.updateRateMs + ' ms)</option>');
    }
    else if (selected) opts.push('<option value="' + esc(selected) + '" selected>' + esc(selected) + '</option>');
    return opts.join('');
}
```

(c) Visibility + refresh — mirror how `fpSubscriptionField` toggles on source type (lines ~2998–3010): where the faceplate opens and the source type is checked, show `fpPlcGroupField` when the source is MxComponent (`window.__fpSourceId` set alongside), and refresh options via `el('fpPlcGroup').innerHTML = fpPlcGroupOptions(currentTagPlcGroup)` including a `loadPlcGroups().then(...)` re-population like the UA flow. Where the faceplate SAVE collects fields (find where `subscription` is added to the update payload), add `plcGroup: el('fpPlcGroup').value`. When the saved value transitions to empty, the server zeroes `PollRateMs` (Task 5 rule) — after save, refresh the displayed rate.

(d) Read-only effective-rate hint — where the rate input renders, if the selected group is non-empty: set hint text `"set by group '<name>'"` and `disabled = true` on the rate input; restore on Default. Copy `updateFpRateEnabled()`'s pattern (header comment line 29 references it).

- [ ] **Step 4: Run test to verify it passes**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter DashboardPageTests -v q --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs tests/OpcBridge.LoadTest/DashboardPageTests.cs
git commit -m "feat(plc-groups): faceplate PLC Group dropdown with effective-rate hint"
```

---

### Task 12: Docs and full-suite verification

**Files:**
- Modify: `src/OpcBridge.App/HelpContent.cs` — add a "PLC Groups" subsection near the DA "Subscriptions & Deadband" material (line ~323) and an MX Component section entry (find `## MX Component` heading; if absent, place under Drivers)
- Test: extend `tests/OpcBridge.LoadTest/HelpContentTests.cs` only if it asserts heading presence patterns (open it first; add one assertion consistent with its style)

**Interfaces:**
- Consumes: everything above.
- Produces: user-facing help text; verified-green suite.

- [ ] **Step 1: Add the failing assertion (if HelpContentTests asserts headings)**

Follow the file's existing pattern; e.g.:

```csharp
[Fact]
public void HelpContent_DocumentsPlcGroups()
{
    Assert.Contains("PLC Groups", HelpContent.Text);
    Assert.Contains("/api/plc/groups", HelpContent.Text);
}
```

(Open `HelpContentTests.cs` and match its actual accessor — `HelpContent.Text` may be named differently.)

- [ ] **Step 2: Run to verify failure mode**

Run: `"$HOME/.dotnet/dotnet" test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter HelpContentTests -v q --nologo`
Expected: FAIL (or, if the file has no suitable pattern, skip straight to writing docs and rely on Step 4's full-suite run — note which path you took in the commit body).

- [ ] **Step 3: Write the help content**

In `HelpContent.cs`, add (matching surrounding markdown-in-string style):

```markdown
## PLC Groups (MX Component)

Named polling groups give an MX Component source multiple update rates:

- **Manage:** Sources → PLC Groups — pick a source, add/edit/delete groups (name + rate, min 100 ms, up to 16 per source).
- **Assign:** tag faceplate → PLC Group dropdown. A grouped tag polls at its GROUP's rate ("group rate wins"); removing a tag from a group clears any per-tag numeric rate.
- **Delete:** deleting a group moves its tags back to the source default rate automatically.
- **How it works:** MX Component reads are synchronous (sh081085 manual — EntryDeviceStatus is alarm monitoring only, ≤20 points, 1–3600 s), so each group is a bridge-side poll loop over the shared logical-station session. All buckets of a source share one link; a slow bucket waits at most one fast batch behind it.

Config keys: `sources.json` → source `PlcGroups`; `mappings.json` → tag `plcGroup` ("" = default bucket). API: `POST /api/plc/groups`, `POST /api/plc/groups/remove`, `GET /api/plc/groups`.
```

- [ ] **Step 4: Full-suite verification (evidence before claims)**

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
cd /mnt/c/Users/xlibr/Documents/OpcDaToUaBridge/.worktrees/feature/mx-component-group
"$HOME/.dotnet/dotnet" test OpcBridge.sln -v q --nologo
```

Expected: **all green** (baseline 558 + the ~20 new tests from Tasks 1–11), 0 failed. Paste the tail of the output into the PR/commit-notes.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/HelpContent.cs tests/OpcBridge.LoadTest/HelpContentTests.cs
git commit -m "docs(plc-groups): help content for PLC Groups management and semantics"
```

---

## Self-Review Notes (completed during plan writing)

- **Spec coverage:** §4 data model → Tasks 1–3; §4 MappingStore semantics → Task 5; §5 resolution → Task 6, worker trigger → Task 7; §6 API + display → Tasks 8–9; §7 UI → Tasks 10–11; §8 testing distributed per task + Task 12 full run; §9 back-compat asserted by Task 2 legacy-construction test and Task 6 overload preservation. GET endpoint effective-rates requirement → Task 8(c). No gaps found.
- **Placeholder scan:** Two intentional "copy the neighbor fixture" instructions exist (Tasks 4 & 5 store fixtures, Task 8 fixture name) — each names the EXACT source file/pattern to copy and binds the test contracts, which is reproducible content rather than a TBD. Everything else carries literal code.
- **Type consistency:** `PlcGroupSettings(Name, UpdateRateMs)`, `TagMapping.PlcGroup`, `DaSourceRuntimeSettings.PlcGroups/PlcGroupsList/PlcGroupsEqual`, `NormalizePlcGroups`, `MaxPlcGroupsPerSource`, `UpsertPlcGroup/RemovePlcGroup`, `ReassignPlcGroup`, `ShouldRestartPollersForPlcGroups`, `PlcGroupUpsertRequest/PlcGroupRemoveRequest` — cross-checked against every usage site above.
