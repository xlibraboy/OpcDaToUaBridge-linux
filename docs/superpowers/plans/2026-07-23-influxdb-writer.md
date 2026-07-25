# InfluxDB Historical Writer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Continuously log opt-in mapped tag values into InfluxDB 2.x/3.x (URL + Org + Bucket + Token) so historical data is available to clients later.

**Architecture:** New `OpcBridge.Influx` project mirrors MQTT: `IInfluxWriter` + official `InfluxDB.Client` write API; `BridgeWorker` enqueues from `BridgeState.ValueUpdated` into a bounded `Channel` drained by a background write task; `InfluxRuntimeSettings` persists `influx.json`; dashboard MQTT-style panel + faceplate `InfluxEnabled` toggle.

**Tech Stack:** .NET 8, `InfluxDB.Client` 5.1.0, ASP.NET Core minimal API, existing dashboard HTML/JS in `DashboardPage.cs`, xUnit in `OpcBridge.LoadTest`.

## Global Constraints

- Work only in worktree: `/home/autoinst578/OpcDaToUaBridge/.worktrees/feature-influxdb-access` on branch `feature/influxdb-access` (rebased onto `main` @ `f7a973d`).
- Spec: `docs/superpowers/specs/2026-07-23-influxdb-writer-design.md`.
- `OpcBridge.Influx` → `OpcBridge.Core` only (never Da/Ua/Mqtt).
- Write trigger: every `BridgeState.ValueUpdated` for tags with `InfluxEnabled == true`.
- Point schema: measurement `options.Measurement` (default `opc_tags`); tags `source_id`, `da_item_id`, optional `display_name`; fields `value` (typed), `quality` (int), `is_good` (bool); timestamp `BridgeValue.TimestampUtc`.
- Failure isolation: Influx outage never blocks DA poll, UA, or MQTT. Channel capacity 1024, `DropOldest`.
- Docker-only SDK builds; gate 0 warnings / 0 errors.
- Keep existing tests green; add new tests in `tests/OpcBridge.LoadTest/`.
- Conventional commits: `feat(influx): ...`.
- Out of scope: Flux query API, charts, Influx 1.x, batching, per-tag measurement override.

**Build/test commands (from worktree root):**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build OpcDaToUaBridge.sln -c Release
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release --nologo
```

---

## File map

| Path | Action | Responsibility |
|---|---|---|
| `src/OpcBridge.Core/InfluxOptions.cs` | Create | Config contract |
| `src/OpcBridge.Core/TagMapping.cs` | Modify | `InfluxEnabled` |
| `src/OpcBridge.Influx/*` | Create | Writer project |
| `OpcDaToUaBridge.sln` | Modify | Add project |
| `src/OpcBridge.App/OpcBridge.App.csproj` | Modify | ProjectReference |
| `src/OpcBridge.App/InfluxRuntimeSettings.cs` | Create | Persist + counters |
| `src/OpcBridge.App/MappingStore.cs` | Modify | Preserve `InfluxEnabled` |
| `src/OpcBridge.App/MappingRequests.cs` | Modify | DTO field |
| `src/OpcBridge.App/BridgeWorker.cs` | Modify | Channel + drain |
| `src/OpcBridge.App/Program.cs` | Modify | DI + REST + mapping fields |
| `src/OpcBridge.App/appsettings.json` | Modify | `Influx:` section |
| `src/OpcBridge.App/DashboardPage.cs` | Modify | Tab + faceplate + JS |
| `src/OpcBridge.App/HelpContent.cs` | Modify | Docs section |
| `tests/OpcBridge.LoadTest/InfluxWriterTests.cs` | Create | Unit tests |
| `tests/OpcBridge.LoadTest/InfluxApiTests.cs` | Create | API smoke |
| `tests/OpcBridge.LoadTest/ConnectedTagsTests.cs` | Modify | BridgeWorker ctor args |

---

### Task 1: Core contracts + MappingStore + DTO

**Files:**
- Create: `src/OpcBridge.Core/InfluxOptions.cs`
- Modify: `src/OpcBridge.Core/TagMapping.cs`
- Modify: `src/OpcBridge.App/MappingStore.cs` (`Normalize` return object)
- Modify: `src/OpcBridge.App/MappingRequests.cs` (`MappingTagDto`)
- Test: `tests/OpcBridge.LoadTest/InfluxWriterTests.cs` (first tests)

**Interfaces:**
- Produces: `InfluxOptions`, `TagMapping.InfluxEnabled`, DTO/store round-trip

- [ ] **Step 1: Write failing tests**

Create `tests/OpcBridge.LoadTest/InfluxWriterTests.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class InfluxWriterTests
{
    [Fact]
    public void InfluxOptions_Defaults_AreSafe()
    {
        InfluxOptions options = new();
        Assert.False(options.Enabled);
        Assert.Equal("http://localhost:8086", options.Url);
        Assert.Equal(string.Empty, options.Org);
        Assert.Equal(string.Empty, options.Bucket);
        Assert.Null(options.Token);
        Assert.Equal("opc_tags", options.Measurement);
        Assert.Equal(5000, options.TimeoutMs);
        Assert.True(options.VerifySsl);
    }

    [Fact]
    public void TagMapping_InfluxEnabled_JsonRoundTrip()
    {
        TagMapping tag = new()
        {
            SourceId = "default",
            DaItemId = "Random.Int1",
            InfluxEnabled = true
        };

        string json = JsonSerializer.Serialize(tag);
        TagMapping? roundTrip = JsonSerializer.Deserialize<TagMapping>(json);
        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.InfluxEnabled);
    }

    [Fact]
    public void MappingStore_Preserves_InfluxEnabled()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "mappings.json");
        if (File.Exists(path)) File.Delete(path);

        MappingStore store = new(Options.Create(new BridgeOptions()));
        store.Add(
        [
            new TagMapping
            {
                SourceId = "default",
                DaItemId = "tag.a",
                DisplayName = "A",
                InfluxEnabled = true
            }
        ]);

        (IReadOnlyList<TagMapping> snapshot, _) = store.GetSnapshot();
        TagMapping mapping = Assert.Single(snapshot.Where(m => m.DaItemId == "tag.a"));
        Assert.True(mapping.InfluxEnabled);
    }
}
```

- [ ] **Step 2: Run tests — expect fail**

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release --nologo --filter FullyQualifiedName~InfluxWriterTests
```
Expected: compile/type errors for missing `InfluxOptions` / `InfluxEnabled`.

- [ ] **Step 3: Implement Core + store + DTO**

Create `src/OpcBridge.Core/InfluxOptions.cs`:
```csharp
namespace OpcBridge.Core;

public sealed class InfluxOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://localhost:8086";
    public string Org { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string Measurement { get; set; } = "opc_tags";
    public int TimeoutMs { get; set; } = 5000;
    public bool VerifySsl { get; set; } = true;
}
```

In `TagMapping.cs`, after `MqttTopic`:
```csharp
    public bool InfluxEnabled { get; set; }
```

In `MappingStore.Normalize` return object, after MQTT fields:
```csharp
            InfluxEnabled = tag.InfluxEnabled
```

In `MappingTagDto` record, add trailing parameter:
```csharp
    bool? InfluxEnabled = null);
```
(keep existing params; append before closing `);`)

- [ ] **Step 4: Run tests — expect pass**

Same docker test filter as Step 2. Expected: 3 passed.

- [ ] **Step 5: Commit**
```bash
git add src/OpcBridge.Core/InfluxOptions.cs src/OpcBridge.Core/TagMapping.cs \
  src/OpcBridge.App/MappingStore.cs src/OpcBridge.App/MappingRequests.cs \
  tests/OpcBridge.LoadTest/InfluxWriterTests.cs
git commit -m "feat(influx): add InfluxOptions and per-tag InfluxEnabled"
```

---

### Task 2: OpcBridge.Influx project + point builder + writer

**Files:**
- Create: `src/OpcBridge.Influx/OpcBridge.Influx.csproj`
- Create: `src/OpcBridge.Influx/IInfluxWriter.cs`
- Create: `src/OpcBridge.Influx/InfluxPointBuilder.cs`
- Create: `src/OpcBridge.Influx/InfluxWriter.cs`
- Modify: `OpcDaToUaBridge.sln`
- Modify: `src/OpcBridge.App/OpcBridge.App.csproj`
- Modify: `tests/OpcBridge.LoadTest/InfluxWriterTests.cs` (point typing tests)

**Interfaces:**
- Produces:
```csharp
public enum InfluxConnectionState { Disconnected, Connecting, Connected, Faulted }

public interface IInfluxWriter : IAsyncDisposable
{
    InfluxConnectionState State { get; }
    event Action<InfluxConnectionState>? StateChanged;
    Task ConnectAsync(InfluxOptions options, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task WritePointAsync(BridgeValue value, string? displayName, CancellationToken ct);
}

public static class InfluxPointBuilder
{
    // Pure helper for tests — builds measurement/tags/fields/timestamp data without network.
    public static InfluxPointModel Build(InfluxOptions options, BridgeValue value, string? displayName);
}

public sealed record InfluxPointModel(
    string Measurement,
    IReadOnlyDictionary<string, string> Tags,
    object? ValueField,
    string ValueFieldKind, // "bool" | "long" | "double" | "string" | "null"
    int Quality,
    bool IsGood,
    DateTime TimestampUtc);
```

- [ ] **Step 1: Write failing point-builder tests**

Append to `InfluxWriterTests.cs`:
```csharp
using OpcBridge.Influx;

[Theory]
[InlineData(true, "bool")]
[InlineData((long)42, "long")]
[InlineData(3.5, "double")]
[InlineData("hi", "string")]
public void InfluxPointBuilder_Types_ValueField(object raw, string kind)
{
    InfluxOptions options = new() { Measurement = "opc_tags" };
    BridgeValue value = new("src", "item.1", raw, DateTime.UtcNow, 192, true);
    InfluxPointModel point = InfluxPointBuilder.Build(options, value, "Name");
    Assert.Equal("opc_tags", point.Measurement);
    Assert.Equal("src", point.Tags["source_id"]);
    Assert.Equal("item.1", point.Tags["da_item_id"]);
    Assert.Equal("Name", point.Tags["display_name"]);
    Assert.Equal(kind, point.ValueFieldKind);
    Assert.Equal(192, point.Quality);
    Assert.True(point.IsGood);
}

[Fact]
public void InfluxPointBuilder_Omits_EmptyDisplayName()
{
    InfluxOptions options = new();
    BridgeValue value = new("src", "item.1", 1L, DateTime.UtcNow, 0, false);
    InfluxPointModel point = InfluxPointBuilder.Build(options, value, "  ");
    Assert.False(point.Tags.ContainsKey("display_name"));
}
```

- [ ] **Step 2: Run tests — expect fail** (missing project/types)

- [ ] **Step 3: Scaffold project**

`src/OpcBridge.Influx/OpcBridge.Influx.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpcBridge.Core\OpcBridge.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="InfluxDB.Client" Version="5.1.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="OpcBridge.LoadTest" />
  </ItemGroup>
</Project>
```

Add project to solution (use unique GUID):
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet sln OpcDaToUaBridge.sln add src/OpcBridge.Influx/OpcBridge.Influx.csproj
```

Add App reference:
```xml
    <ProjectReference Include="..\OpcBridge.Influx\OpcBridge.Influx.csproj" />
```

- [ ] **Step 4: Implement builder + interface + writer**

`InfluxPointBuilder.cs` — pure conversion (int/byte/short → long; float → double; null → kind `"null"`).

`IInfluxWriter.cs` — as in Interfaces block above.

`InfluxWriter.cs` key behavior:
- `ConnectAsync`: validate Url/Org/Bucket/Token non-empty; set Connecting; create `InfluxDBClient` via `InfluxDBClientFactory.Create` / `InfluxDBClientOptions.Builder` with Url, Token, Org, Timeout, and SSL verification from `VerifySsl`; probe by creating write API; set Connected or Faulted; raise `StateChanged`.
- Hold current `InfluxOptions` for measurement name.
- `WritePointAsync`: no-op if not Connected; build point with `PointData.Measurement(...).Tag(...).Field(...).Timestamp(..., WritePrecision.Ns)` using `InfluxPointBuilder` kinds; write via `WriteApiAsync.WritePointAsync(point, bucket, org)`.
- `DisconnectAsync` / `DisposeAsync`: dispose client; state Disconnected.
- Never log Token.

Use `InfluxDB.Client` 5.x APIs (`InfluxDBClient`, `WriteApiAsync`, `PointData`). If a symbol name differs slightly in 5.1.0, match the package’s public API; do not invent wrappers beyond this file.

- [ ] **Step 5: Build + tests**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release --nologo --filter FullyQualifiedName~InfluxWriterTests
```
Expected: all InfluxWriterTests pass; 0w/0e.

- [ ] **Step 6: Commit**
```bash
git add src/OpcBridge.Influx OpcDaToUaBridge.sln src/OpcBridge.App/OpcBridge.App.csproj \
  tests/OpcBridge.LoadTest/InfluxWriterTests.cs
git commit -m "feat(influx): add OpcBridge.Influx writer and point builder"
```

---

### Task 3: InfluxRuntimeSettings + appsettings

**Files:**
- Create: `src/OpcBridge.App/InfluxRuntimeSettings.cs`
- Modify: `src/OpcBridge.App/appsettings.json`
- Test: append runtime-settings tests in `InfluxWriterTests.cs`

**Interfaces:**
- Produces: `InfluxRuntimeSettings` + `InfluxRuntimeSnapshot` (mirror `MqttRuntimeSettings` / `MqttRuntimeSnapshot` but written-only counters)

```csharp
public sealed record InfluxRuntimeSnapshot(
    string State,
    string? LastError,
    long WrittenCount,
    double WrittenRate,
    InfluxOptions Options);
```

Methods: `GetOptions()`, `GetSnapshot()`, `UpsertOptions(InfluxOptions)`, `SetState(string, string? lastError = null)`, `ResetCounters()`, `IncrementWritten()`. Persist path `influx.json` under `AppContext.BaseDirectory`. Seed from `IOptions<InfluxOptions>` then override with disk file if present.

- [ ] **Step 1: Failing test for persist round-trip**

```csharp
[Fact]
public void InfluxRuntimeSettings_Persists_ToDisk()
{
    string path = Path.Combine(AppContext.BaseDirectory, "influx.json");
    if (File.Exists(path)) File.Delete(path);

    InfluxRuntimeSettings settings = new(Options.Create(new InfluxOptions()));
    settings.UpsertOptions(new InfluxOptions
    {
        Enabled = true,
        Url = "http://127.0.0.1:8086",
        Org = "factory",
        Bucket = "tags",
        Token = "secret",
        Measurement = "opc_tags"
    });

    Assert.True(File.Exists(path));
    InfluxRuntimeSettings reloaded = new(Options.Create(new InfluxOptions()));
    InfluxOptions opts = reloaded.GetOptions();
    Assert.True(opts.Enabled);
    Assert.Equal("factory", opts.Org);
    Assert.Equal("tags", opts.Bucket);
    Assert.Equal("secret", opts.Token);
}
```

- [ ] **Step 2: Implement `InfluxRuntimeSettings`** by adapting `MqttRuntimeSettings.cs` (drop received counters; rename published → written).

- [ ] **Step 3: Add appsettings section**
```json
  "Influx": {
    "Enabled": false,
    "Url": "http://localhost:8086",
    "Org": "",
    "Bucket": "",
    "Token": null,
    "Measurement": "opc_tags",
    "TimeoutMs": 5000,
    "VerifySsl": true
  }
```

- [ ] **Step 4: Test + commit**
```bash
git add src/OpcBridge.App/InfluxRuntimeSettings.cs src/OpcBridge.App/appsettings.json \
  tests/OpcBridge.LoadTest/InfluxWriterTests.cs
git commit -m "feat(influx): add InfluxRuntimeSettings and appsettings seed"
```

---

### Task 4: BridgeWorker channel drain + DI

**Files:**
- Modify: `src/OpcBridge.App/BridgeWorker.cs`
- Modify: `src/OpcBridge.App/Program.cs` (DI only in this task if preferred; API can be Task 5 — but ctor requires DI registration before tests construct worker)
- Modify: `tests/OpcBridge.LoadTest/ConnectedTagsTests.cs` (`CreateWorker` args)
- Test: enqueue filter test via reflection (same pattern as ConnectedTagsTests)

**Interfaces:**
- Consumes: `IInfluxWriter`, `InfluxRuntimeSettings`
- Fields:
  - `influx_writer_`, `influx_settings_`
  - `Channel<BridgeValue> influx_write_channel_` (1024, DropOldest, SingleReader=true)
  - `HashSet<string> influx_enabled_keys_`
- Startup: build enabled keys; `ValueUpdated` handler also enqueues for Influx; `Task.Run(InfluxWriteDrainAsync)`; if `Enabled`, `ConnectInfluxAsync`
- Mapping change: rebuild `influx_enabled_keys_`
- StateChanged: map to `influx_settings_.SetState`; on Connected call `ResetCounters`
- Drain: if not Enabled or State != Connected → continue without write; else `WritePointAsync` + `IncrementWritten`; catch log warning
- StopAsync: optionally `DisconnectAsync` (best-effort)

- [ ] **Step 1: Failing unit test for enable filter**

```csharp
[Fact]
public void OnBridgeValueUpdated_Enqueues_Only_InfluxEnabled()
{
    // Arrange worker like ConnectedTagsTests.CreateWorker but with FakeInfluxWriter
    // Set private influx_enabled_keys_ to contain only "default::on"
    // Invoke OnBridgeValueUpdated for on and off tags
    // Assert FakeInfluxWriter received only enabled tag via channel drain or capture channel writes
}
```

Implement a small `FakeInfluxWriter : IInfluxWriter` in the test file that records `WritePointAsync` calls. After invoking private `OnBridgeValueUpdated`, run drain briefly or read channel via reflection if needed. Simpler approach: make `OnBridgeValueUpdated` write to channel; test private field channel reader with a short drain helper on Fake that the test starts.

Minimal acceptable test:
1. Construct worker with fake writer + settings.
2. Set `influx_enabled_keys_` to `{ "default::enabled" }`.
3. Set writer state Connected via fake.
4. Call `OnBridgeValueUpdated` for enabled + disabled.
5. Manually invoke private `InfluxWriteDrainAsync` with cancellation after processing 1 item, OR expose nothing and assert channel count via reflection on `influx_write_channel_.Reader.Count` if available.

If channel count is hard, assert by running drain until timeout with fake capturing writes — only enabled should appear.

- [ ] **Step 2: Wire ctor + handlers in BridgeWorker** (mirror MQTT blocks at fields, ctor, ExecuteAsync init, mapping rebuild, OnBridgeValueUpdated dual enqueue, ConnectInfluxAsync, InfluxWriteDrainAsync).

Update `OnBridgeValueUpdated` to handle both MQTT and Influx without blocking:
```csharp
private void OnBridgeValueUpdated(BridgeValue value)
{
    string key = NormalizeKey(value.SourceId, value.DaItemId);
    if (mqtt_enabled_keys_.Contains(key))
    {
        _ = mqtt_publish_channel_.Writer.WriteAsync(value);
    }
    if (influx_enabled_keys_.Contains(key))
    {
        _ = influx_write_channel_.Writer.WriteAsync(value);
    }
}
```

- [ ] **Step 3: DI in Program.cs**
```csharp
builder.Services.Configure<InfluxOptions>(builder.Configuration.GetSection("Influx"));
builder.Services.AddSingleton<IInfluxWriter, InfluxWriter>();
builder.Services.AddSingleton<InfluxRuntimeSettings>();
```
Add `using OpcBridge.Influx;`.

- [ ] **Step 4: Fix ConnectedTagsTests.CreateWorker** to pass `new InfluxWriter(...)` or a no-op fake + `new InfluxRuntimeSettings(Options.Create(new InfluxOptions()))`.

- [ ] **Step 5: Full test suite**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release --nologo
```
Expected: all pass (prior baseline + new).

- [ ] **Step 6: Commit**
```bash
git add src/OpcBridge.App/BridgeWorker.cs src/OpcBridge.App/Program.cs \
  tests/OpcBridge.LoadTest/ConnectedTagsTests.cs tests/OpcBridge.LoadTest/InfluxWriterTests.cs
git commit -m "feat(influx): wire BridgeWorker Influx write channel and DI"
```

---

### Task 5: REST API + mapping field plumbing

**Files:**
- Modify: `src/OpcBridge.App/Program.cs`
- Create: `tests/OpcBridge.LoadTest/InfluxApiTests.cs`

**Endpoints (mirror MQTT):**
- `GET /api/influx/config` → options fields (camelCase JSON)
- `POST /api/influx/config` body `InfluxConfigRequest`
- `POST /api/influx/connect` / `disconnect`
- `GET /api/influx/status` → state, lastError, writtenCount, writtenRate, enabled

Mapping add/update/bulk-add: set `InfluxEnabled = tag.InfluxEnabled ?? false`.

```csharp
record InfluxConfigRequest(
    bool Enabled,
    string? Url,
    string? Org,
    string? Bucket,
    string? Token,
    string? Measurement,
    int? TimeoutMs,
    bool VerifySsl);
```

- [ ] **Step 1: API smoke test**
```csharp
[Collection(nameof(DaLinkApiAppCollection))]
public sealed class InfluxApiTests
{
    [Fact]
    public async Task InfluxStatus_Returns_Disconnected_ByDefault()
    {
        await using var handle = await TestAppHandle.StartAsync(dir =>
        {
            // same appsettings pattern as BridgeAppApiTests, plus Influx: { Enabled=false, ... }
        });
        using var status = await handle.GetJsonAsync("/api/influx/status");
        Assert.Equal("Disconnected", status.RootElement.GetProperty("state").GetString());
        Assert.False(status.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task InfluxConfig_Post_Persists_EnabledFlag()
    {
        // POST config Enabled=true Url/Org/Bucket/Token, GET config, assert enabled true and fields
    }
}
```

Include `Influx` section in any `TestAppHandle` appsettings writers used by these tests.

- [ ] **Step 2: Implement endpoints** after MQTT block in `Program.cs`.

- [ ] **Step 3: Mapping InfluxEnabled assignment** in add/bulk-add/update (three places).

- [ ] **Step 4: Test + commit**
```bash
git add src/OpcBridge.App/Program.cs tests/OpcBridge.LoadTest/InfluxApiTests.cs
git commit -m "feat(influx): add REST config/connect/status endpoints"
```

---

### Task 6: Dashboard UI + Help

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs`
- Modify: `src/OpcBridge.App/HelpContent.cs`
- Optional: `tests/OpcBridge.LoadTest/DashboardPageTests.cs` / `HelpContentTests.cs` if they assert tab names

**UI requirements:**
1. Tab bar button `InfluxDB` next to MQTT (`data-tab="influx"`).
2. View `#view-influx` mirroring MQTT layout (no traffic monitor required):
   - Config: Auto-connect/Enabled, URL, Org, Bucket, Token (password), Measurement, TimeoutMs optional, VerifySsl checkbox, Save Config
   - Live Connection: Connect / Disconnect
   - Status: State, Written, Written rate, last error
3. Faceplate: add checkbox `fpInfluxEnabled` in MQTT pane (or new small row under MQTT) labeled Influx log.
4. JS: `loadInfluxConfig`, `loadInfluxStatus`, `saveInflux`, `connectInflux`, `disconnectInflux`; call from `showTab('influx')` and poll with MQTT status if a shared timer exists.
5. `updateMapping` payload includes `influxEnabled: el('fpInfluxEnabled').checked`.
6. `openFaceplate` loads checkbox from mapping.
7. Optional mapping list badge `Influx` when enabled (like MQTT pill).

**HelpContent:** new section after MQTT:
```
# InfluxDB (Historical Logging)
- External InfluxDB 2.x/3.x server required
- Configure URL, Org, Bucket, Token on InfluxDB tab; Save + Connect
- Enable per tag via faceplate Influx checkbox
- Points: measurement opc_tags (configurable), tags source_id/da_item_id/display_name, fields value/quality/is_good
- Outage does not stop the bridge
```

- [ ] **Step 1: Implement HTML/JS/Help**

- [ ] **Step 2: Syntax check dashboard JS if extracted; otherwise build app**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build src/OpcBridge.App/OpcBridge.App.csproj -c Release
```
If Help/Dashboard tests exist for section strings, update expectations.

- [ ] **Step 3: Full test suite**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release --nologo
```

- [ ] **Step 4: Commit**
```bash
git add src/OpcBridge.App/DashboardPage.cs src/OpcBridge.App/HelpContent.cs \
  tests/OpcBridge.LoadTest
git commit -m "feat(influx): add InfluxDB dashboard panel and faceplate toggle"
```

---

### Task 7: Final verification + context note

**Files:**
- Modify: `context.md` (one line under architecture: Influx optional sink exists; HMI charts still deferred)

- [ ] **Step 1: Full solution build 0w/0e**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build OpcDaToUaBridge.sln -c Release
```

- [ ] **Step 2: Full tests**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release --nologo
```

- [ ] **Step 3: Spec acceptance checklist (code-level)**
1. Config/connect/status API present
2. Only `InfluxEnabled` tags enqueue
3. Point builder types + tags correct
4. Channel DropOldest + write errors non-fatal
5. `influx.json` persist
6. Dashboard + faceplate + help present
7. Baseline + new tests green

- [ ] **Step 4: Commit context if changed**
```bash
git add context.md
git commit -m "docs(influx): note Influx writer in context.md"
```

Manual Windows runtime (operator, not CI): real Influx 2.x connect + one enabled tag → points in bucket.

---

## Spec coverage (self-review)

| Spec item | Task |
|---|---|
| `InfluxOptions` + defaults | 1, 3 |
| `TagMapping.InfluxEnabled` | 1, 5, 6 |
| `OpcBridge.Influx` + `IInfluxWriter` + client | 2 |
| Point schema | 2 |
| Runtime settings / `influx.json` | 3 |
| BridgeWorker channel drain | 4 |
| REST API | 5 |
| Mapping API field | 1, 5 |
| Dashboard + faceplate | 6 |
| Help | 6 |
| Failure isolation | 2, 4 |
| Tests | 1–5, 7 |
| Out of scope respected | no query/charts/1.x tasks |

## Placeholder scan

No TBD/TODO left. Package version pinned to **5.1.0**. Worktree path and rebase base documented.
