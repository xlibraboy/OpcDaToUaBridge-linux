# OPC UA Source Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add OPC UA as an inbound source type so the bridge connects to external UA servers (Siemens/Kepware), maps NodeIds at high scale via subscriptions, write-throughs writes, and re-publishes on the existing outbound UA/MQTT/Influx path.

**Architecture:** Approach A — extend `sources.json` / `DaSourceRuntimeSettings` with `SourceType` + UA fields; implement `OpcUaSourceClient` behind the existing `IDaClient` (+ subscription) seam; factory branches in `DaClientFactory`; `BridgeWorker` sessions/write queue/mappings reused. Outbound `UaServerHost` stays the bridge’s UA server face. Hot path = mapped tags only, MonitoredItems batched for ≥20k mappings/source.

**Tech Stack:** .NET 8, `OPCFoundation.NetStandard.Opc.Ua` 1.5.378.145 (client + server), ASP.NET minimal APIs, dashboard HTML/JS in `DashboardPage.cs`, xUnit under `tests/OpcBridge.LoadTest`.

**Spec:** `docs/superpowers/specs/2026-07-27-opc-ua-source-design.md`

## Global Constraints

- Worktree: `/home/iwan/Development/Projects/OpcDaToUaBridge-linux/.worktrees/feature-opc-ua-source` on branch `feature/opc-ua-source`.
- Build gate: 0 Warning(s), 0 Error(s) via Docker SDK 8.0 `dotnet build OpcDaToUaBridge.sln` and/or `docker build -f Dockerfile.local -t opcbridge:local .`.
- Conventional commits: `feat(opc-ua): …`, `feat(dashboard): …`, `test(opc-ua): …`, `docs(opc-ua): …`.
- Do **not** rename product, mass-rename `DaItemId` → `ItemId`, or rebrand `/api/da/*` in v1.
- For UA sources, `DaItemId` stores the **NodeId string** used for Read/Write/Monitor; outbound default NodeId remains `ns=2;s={SourceId}/{DaItemId}` (sanitize outbound only if needed).
- Client PKI root: `pki/ua-client/` (separate from server `pki/`).
- Self-endpoint guard: refuse UA source `EndpointUrl` that targets this process’s UA server.
- Capacity: design for ≥20 000 mapped tags/source; MonitoredItem create/delete batches of 500–1000; notifications applied in batches; never poll unmapped nodes.
- Security v1: `None`, `Sign`, `SignAndEncrypt` with policies `None` / `Basic256Sha256`.
- Linux: UA client path must compile and run; DA remains Windows-only COM.
- Existing DA sources and outbound UA server behavior must keep working.
- YAGNI: no HistoryRead, A&C, methods, subtree import UI, full cert manager UI, DA Links to UA.

---

## File map

| File | Responsibility |
|---|---|
| `src/OpcBridge.Core/SourceTypes.cs` | Constants `OpcDa`, `OpcUa` |
| `src/OpcBridge.Core/UaQualityMapper.cs` | Map UA status code → DA-like quality + `IsGood` |
| `src/OpcBridge.Da/IDaClient.cs` | Add optional subscription surface (event or `ISubscribableSourceClient`) |
| `src/OpcBridge.Da/ISubscribableSourceClient.cs` | `event Action<IReadOnlyList<BridgeValue>>? ValuesReceived` (new) |
| `src/OpcBridge.App/DaRuntimeSettings.cs` | Extend `DaSourceRuntimeSettings`, DTO persist/load, normalize, defaults |
| `src/OpcBridge.App/DaServerConfigRequest.cs` | Polymorphic upsert request fields |
| `src/OpcBridge.App/DaClientFactory.cs` | Branch on `SourceType` |
| `src/OpcBridge.App/UaEndpointGuard.cs` | Self-URL detection vs `UaServerOptions.EndpointUrl` |
| `src/OpcBridge.App/BridgeWorker.cs` | Connect skip rules per type; subscribe via interface; mapping reconcile hook for UA |
| `src/OpcBridge.App/BridgeState.cs` | Status snapshot may include `sourceType` / endpoint summary fields |
| `src/OpcBridge.App/MappingStore.cs` / mapping API | Enforce `MaxMappedTags` per UA source on add |
| `src/OpcBridge.App/Program.cs` | Extend `/api/da/sources*`; add `/api/ua/test-connection`, `/api/ua/browse` |
| `src/OpcBridge.Ua/OpcUaSourceClientOptions.cs` | Client options from runtime settings |
| `src/OpcBridge.Ua/OpcUaSourceClient.cs` | Session, read, write, subscribe reconcile, dispose |
| `src/OpcBridge.Ua/OpcUaBrowseService.cs` | Paged browse + test connection helpers |
| `src/OpcBridge.App/DashboardPage.cs` | Sources status (if restored), OPC UA tab, wizard type step, badges, UA browse |
| `src/OpcBridge.App/HelpContent.cs` | Document UA source vs UA server endpoint |
| `context.md` | Architecture note for UA inbound source |
| `tests/OpcBridge.LoadTest/*` | Unit/API tests for model, guard, factory, quality, APIs |

---

### Task 1: Source model — `SourceType` + UA fields + migration

**Files:**
- Create: `src/OpcBridge.Core/SourceTypes.cs`
- Modify: `src/OpcBridge.App/DaRuntimeSettings.cs` (`DaSourceRuntimeSettings`, `SourceConfigDto`, `NormalizeSource`, `Persist`, `LoadFromDisk`, `BuildInitialSources`)
- Modify: `src/OpcBridge.App/DaServerConfigRequest.cs`
- Modify: every `new DaSourceRuntimeSettings(` callsite (App + tests) to compile
- Test: `tests/OpcBridge.LoadTest/UaSourceSettingsTests.cs`

**Interfaces:**
- Produces:
  - `SourceTypes.OpcDa = "OpcDa"`, `SourceTypes.OpcUa = "OpcUa"`
  - Extended record (keep name `DaSourceRuntimeSettings` in v1):

```csharp
public sealed record DaSourceRuntimeSettings(
    string SourceId,
    string DisplayName,
    string SourceType,          // OpcDa | OpcUa
    string ProgId,
    string Host,
    string? RemoteUsername,
    string? RemotePassword,
    string? RemoteDomain,
    string EndpointUrl,         // UA; empty for DA
    string SecurityMode,        // None | Sign | SignAndEncrypt
    string SecurityPolicy,      // None | Basic256Sha256
    string? UaUsername,
    string? UaPassword,
    int SessionTimeoutMs,
    int ReconnectDelayMs,
    int MaxMappedTags,
    bool UseSubscriptions,
    int UpdateRateMs)
```

  - Defaults when loading old JSON: `SourceType=OpcDa`, empty endpoint, `SecurityMode=None`, `SecurityPolicy=None`, `SessionTimeoutMs=60000`, `ReconnectDelayMs=5000`, `MaxMappedTags=50000`, `UseSubscriptions=true`
- Consumes: existing `sources.json` without `sourceType`

- [ ] **Step 1: Write failing tests**

```csharp
public sealed class UaSourceSettingsTests
{
    [Fact]
    public void LoadFromDisk_MissingSourceType_DefaultsToOpcDa()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "sources.json"), """
            {"updateRateMs":1000,"useSubscriptions":true,"sources":[
              {"sourceId":"line1","displayName":"Line 1","progId":"Matrikon.OPC.Simulation.1","host":"localhost","updateRateMs":500}
            ]}
            """);
        // Construct DaRuntimeSettings with BaseDirectory overridden OR test Load path via public API after injecting path.
        // Preferred: extract/load helper test by writing file then using runtime settings if path is AppContext.BaseDirectory —
        // use a small internal test hook or test Normalize via Upsert round-trip of DTO fields after Load.
        // Practical approach for this codebase: unit-test a public static/internal helper if added:
        // SourceConfigMigration.FromDto(dto) → DaSourceRuntimeSettings
        var source = SourceConfigMigration.FromDto(new SourceConfigDto {
            SourceId = "line1", ProgId = "X", Host = "h", UpdateRateMs = 500
        }, defaultUpdateRate: 1000);
        Assert.Equal(SourceTypes.OpcDa, source.SourceType);
        Assert.Equal("X", source.ProgId);
        Assert.Equal("", source.EndpointUrl);
        Assert.Equal(50000, source.MaxMappedTags);
    }

    [Fact]
    public void FromDto_OpcUa_RequiresEndpointFields()
    {
        var source = SourceConfigMigration.FromDto(new SourceConfigDto {
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
    }
}
```

If `SourceConfigDto` is `internal`, add `InternalsVisibleTo` for the test assembly (already used elsewhere) or make migration helper public under App.

- [ ] **Step 2: Run test — expect fail** (types missing)

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --filter FullyQualifiedName~UaSourceSettingsTests
```

- [ ] **Step 3: Implement model**

1. Add `SourceTypes.cs` in Core.
2. Extend `DaSourceRuntimeSettings` + `SourceConfigDto` with UA fields + `SourceType`.
3. Add `SourceConfigMigration.FromDto` (or fold into `NormalizeSource`) applying defaults.
4. Update `Persist`/`LoadFromDisk`/`BuildInitialSources`/`ToOptions` (DA `ToOptions` only uses DA fields).
5. Fix all compile breaks: `Program.cs` upsert, status snapshots if they construct the record, tests, `BridgeState` if needed.

`NormalizeSource` rules:
- `SourceType` empty → `OpcDa`
- Unknown type → treat as `OpcDa` **or** reject on API only (prefer normalize unknown → OpcDa for load resilience; API validates)
- UA: trim `EndpointUrl`; default security None/None; clamp `MaxMappedTags` ≥ 1 (default 50000); `SessionTimeoutMs` default 60000; `ReconnectDelayMs` default 5000
- DA: keep host default `localhost`

- [ ] **Step 4: Run tests — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.Core/SourceTypes.cs src/OpcBridge.App/DaRuntimeSettings.cs \
  src/OpcBridge.App/DaServerConfigRequest.cs tests/OpcBridge.LoadTest/UaSourceSettingsTests.cs
# plus any compile-fix files
git commit -m "feat(opc-ua): add SourceType and UA fields to source model"
```

---

### Task 2: Quality mapper + self-endpoint guard

**Files:**
- Create: `src/OpcBridge.Core/UaQualityMapper.cs`
- Create: `src/OpcBridge.App/UaEndpointGuard.cs`
- Test: `tests/OpcBridge.LoadTest/UaQualityMapperTests.cs`
- Test: `tests/OpcBridge.LoadTest/UaEndpointGuardTests.cs`

**Interfaces:**
- Produces:

```csharp
// Core
public static class UaQualityMapper
{
    // Maps UA status code bits to a DA-like quality int + IsGood.
    // Good (0) → quality 0xC0, isGood true
    // Uncertain → quality 0x40, isGood false
    // Bad → quality 0x00, isGood false
    public static (int DaQuality, bool IsGood) FromStatusCode(uint statusCode);
}

// App
public static class UaEndpointGuard
{
    // true if candidate would connect to our own UA server endpoint
    public static bool TargetsSelf(string candidateEndpointUrl, string serverEndpointUrl);
}
```

- Consumes: `UaServerOptions.EndpointUrl` (e.g. `opc.tcp://0.0.0.0:4840/OpcBridge`)

- [ ] **Step 1: Failing tests**

```csharp
public sealed class UaQualityMapperTests
{
    [Theory]
    [InlineData(0u, 0xC0, true)]          // Good
    [InlineData(0x40000000u, 0x40, false)] // Uncertain (StatusCode.Uncertain)
    [InlineData(0x80000000u, 0x00, false)] // Bad
    public void FromStatusCode_MapsClasses(uint code, int expectedQuality, bool expectedGood)
    {
        var (q, good) = UaQualityMapper.FromStatusCode(code);
        Assert.Equal(expectedQuality, q);
        Assert.Equal(expectedGood, good);
    }
}

public sealed class UaEndpointGuardTests
{
    [Theory]
    [InlineData("opc.tcp://127.0.0.1:4840/OpcBridge", "opc.tcp://0.0.0.0:4840/OpcBridge", true)]
    [InlineData("opc.tcp://localhost:4840/OpcBridge", "opc.tcp://0.0.0.0:4840/OpcBridge", true)]
    [InlineData("opc.tcp://kepware:49320", "opc.tcp://0.0.0.0:4840/OpcBridge", false)]
    [InlineData("opc.tcp://127.0.0.1:4841/OpcBridge", "opc.tcp://0.0.0.0:4840/OpcBridge", false)]
    public void TargetsSelf_DetectsLoopbackSamePortPath(string candidate, string server, bool expected)
    {
        Assert.Equal(expected, UaEndpointGuard.TargetsSelf(candidate, server));
    }
}
```

- [ ] **Step 2: Run — fail**

- [ ] **Step 3: Implement**

`UaEndpointGuard` logic:
1. Parse both as `Uri` (require `opc.tcp` scheme).
2. Compare ports (default 4840 if missing).
3. Compare paths case-insensitive (trim trailing `/`).
4. Host self if candidate host is `localhost`, `127.0.0.1`, `::1`, or equals `Dns.GetHostName()`, **and** server host is `0.0.0.0`, `+`, `*`, `localhost`, loopback, or same hostname.
5. Invalid URIs → `false` (connect validation elsewhere rejects bad URL).

- [ ] **Step 4: Run — pass**

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(opc-ua): add UaQualityMapper and self-endpoint guard"
```

---

### Task 3: Subscription seam on `IDaClient`

**Files:**
- Create: `src/OpcBridge.Da/ISubscribableSourceClient.cs`
- Modify: `src/OpcBridge.Da/OpcDaClient.cs` — implement interface (existing `OnCallbackValues` fulfills it)
- Modify: `src/OpcBridge.App/BridgeWorker.cs` — subscribe via interface, not `is OpcDaClient`
- Test: `tests/OpcBridge.LoadTest/MockDaClient.cs` if present — implement interface no-op

**Interfaces:**
- Produces:

```csharp
namespace OpcBridge.Da;

public interface ISubscribableSourceClient
{
    event Action<IReadOnlyList<BridgeValue>>? ValuesReceived;
}
```

`OpcDaClient`: implement `ISubscribableSourceClient`; `ValuesReceived` forwards same as `OnCallbackValues` (either rename event to `ValuesReceived` and keep `OnCallbackValues` as obsolete wrapper, or dual-raise — prefer single event `ValuesReceived` and update DA callback site to use it; if `OnCallbackValues` is public API used only by BridgeWorker, rename cleanly).

- Consumes: `BridgeWorker.OnSubscriptionValues`

- [ ] **Step 1: Change BridgeWorker hook**

```csharp
if (client is ISubscribableSourceClient subscribable)
{
    subscribable.ValuesReceived += values => OnSubscriptionValues(values);
}
```

- [ ] **Step 2: Implement interface on `OpcDaClient`**

- [ ] **Step 3: Build**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build OpcDaToUaBridge.sln
```

Expected: 0w 0e

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(opc-ua): generalize source subscription callback seam"
```

---

### Task 4: `OpcUaSourceClient` — options, connect, read, write, dispose

**Files:**
- Create: `src/OpcBridge.Ua/OpcUaSourceClientOptions.cs`
- Create: `src/OpcBridge.Ua/OpcUaSourceClient.cs`
- Modify: `src/OpcBridge.App/DaClientFactory.cs`
- Modify: `src/OpcBridge.App/BridgeWorker.cs` — connect preconditions by `SourceType`
- Test: `tests/OpcBridge.LoadTest/DaClientFactoryTests.cs` (factory branch; no live server)

**Interfaces:**
- Produces:

```csharp
public sealed class OpcUaSourceClientOptions
{
    public string SourceId { get; set; } = "default";
    public string DisplayName { get; set; } = "";
    public string EndpointUrl { get; set; } = "";
    public string SecurityMode { get; set; } = "None"; // None|Sign|SignAndEncrypt
    public string SecurityPolicy { get; set; } = "None"; // None|Basic256Sha256
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int UpdateRateMs { get; set; } = 1000;
    public int SessionTimeoutMs { get; set; } = 60000;
    public int ReconnectDelayMs { get; set; } = 5000;
    public bool UseSubscriptions { get; set; } = true;
    public string ApplicationName { get; set; } = "OpcDaToUaBridge.UaClient";
    public string PkiRoot { get; set; } = "pki/ua-client"; // under BaseDirectory
    public bool AutoAcceptUntrustedCertificates { get; set; } = true; // lab default
}

public sealed class OpcUaSourceClient : IDaClient, ISubscribableSourceClient
{
    public event Action<IReadOnlyList<BridgeValue>>? ValuesReceived;
    public OpcUaSourceClient(OpcUaSourceClientOptions options, ILogger? logger = null);
    public Task ConnectAsync(CancellationToken cancellationToken);
    public Task<IReadOnlyList<BridgeValue>> ReadAsync(IReadOnlyList<TagMapping> mappings, CancellationToken cancellationToken);
    public Task<bool> WriteAsync(string daItemId, object? value, CancellationToken cancellationToken);
    public bool TryGetTagMetadata(string daItemId, out short? canonicalDataType, out int? accessRights);
    public Task ReconcileMonitoredItemsAsync(IReadOnlyList<TagMapping> desiredMappings, CancellationToken cancellationToken);
    public ValueTask DisposeAsync();
}
```

- Consumes: OPC Foundation `Session`, `SessionChannel`, `EndpointDescription`, `ApplicationConfiguration`, `UaQualityMapper`, NodeId.Parse

- [ ] **Step 1: Factory tests**

```csharp
[Fact]
public void Create_OpcUa_ReturnsOpcUaSourceClient()
{
    var factory = new DaClientFactory();
    var source = /* OpcUa DaSourceRuntimeSettings with EndpointUrl */;
    var snapshot = new DaRuntimeSettingsSnapshot(1000, true, new[] { source }, 1);
    IDaClient client = factory.Create(snapshot, source);
    Assert.IsType<OpcBridge.Ua.OpcUaSourceClient>(client);
}

[Fact]
public void Create_OpcDa_ReturnsOpcDaClient()
{
    // existing-shaped DA source
    Assert.IsType<OpcDaClient>(factory.Create(snapshot, daSource));
}
```

- [ ] **Step 2: Implement options + client skeleton**

`ConnectAsync`:
1. Validate `EndpointUrl` non-empty and `opc.tcp`.
2. Build `ApplicationConfiguration` with client app type, PKI under `Path.Combine(AppContext.BaseDirectory, options.PkiRoot)`, auto-accept per options.
3. `CoreClientUtils.SelectEndpoint` / discover endpoint matching security mode+policy (map names to `MessageSecurityMode` + `SecurityPolicies.*`).
4. `Session.Create(...)` with user identity: anonymous or `UserIdentity(username, password)`.
5. Store session; on failure throw with clear message.

`ReadAsync`:
- Parse each mapping `DaItemId` as NodeId; `session.Read` in chunks (e.g. 500 nodes); map DataValues → `BridgeValue` via `UaQualityMapper` + timestamps (Source → Server → UtcNow).

`WriteAsync`:
- Write single NodeId; return true on Good status.

`TryGetTagMetadata`:
- Optional Read of DataType/AccessLevel; return false if not connected.

`DisposeAsync`:
- Close session, dispose channel, clear monitored items.

**Not in this task:** full subscription reconcile (Task 5). Stub `ReconcileMonitoredItemsAsync` as no-op or empty, and leave `UseSubscriptions` path inactive until Task 5.

- [ ] **Step 3: Factory branch + BridgeWorker preconditions**

```csharp
// DaClientFactory
public IDaClient Create(DaRuntimeSettingsSnapshot settings, DaSourceRuntimeSettings source)
{
    if (string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
    {
        return new OpcUaSourceClient(source.ToUaOptions(settings));
    }
    return new OpcDaClient(source.ToOptions(settings.UseSubscriptions));
}

// BridgeWorker.ReconfigureSessionsAsync
if (string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(source.EndpointUrl)) { /* Disconnected + error */ continue; }
    if (UaEndpointGuard.TargetsSelf(source.EndpointUrl, ua_server_.EndpointUrl /* or options */))
    {
        /* Faulted: cannot use own UA endpoint as source */ continue;
    }
}
else
{
    if (string.IsNullOrWhiteSpace(source.ProgId)) { /* existing empty ProgID path */ continue; }
}
```

Expose server endpoint URL on `UaServerHost` or inject `IOptions<UaServerOptions>` into worker if not already available.

- [ ] **Step 4: Build + unit tests pass**

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(opc-ua): add OpcUaSourceClient connect/read/write"
```

---

### Task 5: Subscriptions — reconcile MonitoredItems + notification batching

**Files:**
- Modify: `src/OpcBridge.Ua/OpcUaSourceClient.cs`
- Modify: `src/OpcBridge.App/BridgeWorker.cs` — after mapping version change / connect, call reconcile for UA clients
- Test: `tests/OpcBridge.LoadTest/OpcUaMonitoredItemReconcileTests.cs` (pure set-diff helper if extracted)

**Interfaces:**
- Produces:

```csharp
// Prefer extract pure diff for testability:
public static class MonitoredItemReconcile
{
    public static (IReadOnlyList<string> ToAdd, IReadOnlyList<string> ToRemove) Diff(
        IReadOnlyCollection<string> desiredNodeIds,
        IReadOnlyCollection<string> activeNodeIds);
}

// OpcUaSourceClient.ReconcileMonitoredItemsAsync:
// desired = enabled mappings where Mode != Manual, DaItemId non-empty
// batch add/remove 500–1000
// publishing interval ~= UpdateRateMs
// DataChange → ValuesReceived with BridgeValue batches (flush every N items or end of notification)
```

- Consumes: Task 4 session; `ISubscribableSourceClient.ValuesReceived`; `BridgeWorker.OnSubscriptionValues`

- [ ] **Step 1: Diff unit tests**

```csharp
[Fact]
public void Diff_AddsAndRemoves()
{
    var (add, remove) = MonitoredItemReconcile.Diff(
        desiredNodeIds: new[] { "ns=2;s=A", "ns=2;s=B" },
        activeNodeIds: new[] { "ns=2;s=A", "ns=2;s=C" });
    Assert.Equal(new[] { "ns=2;s=B" }, add);
    Assert.Equal(new[] { "ns=2;s=C" }, remove);
}
```

- [ ] **Step 2: Implement reconcile + subscription create**

Algorithm:
1. If `!UseSubscriptions` or session null → return (poll path remains).
2. Ensure one `Subscription` on session (publishing interval = `UpdateRateMs`).
3. Desired set from mappings.
4. Diff vs active dictionary `NodeIdString → MonitoredItem`.
5. Remove obsolete items; `ApplyChanges` in batches.
6. Add new items with sampling interval from mapping `PollRateMs` if > 0 else `UpdateRateMs`; handler packs values.
7. On subscription failure: log, set internal `subscriptionsActive_=false` so poller continues; do not throw away session.

Notification handler:
- Build `List<BridgeValue>` for the notification set; invoke `ValuesReceived` once per notification (or every 1000 items if huge).

- [ ] **Step 3: BridgeWorker wiring**

After successful connect for UA client, and whenever mapping cache version changes for that source:

```csharp
if (session.Client is OpcUaSourceClient uaClient)
{
    IReadOnlyList<TagMapping> desired = cache.GetSourceReadMappings(source.SourceId); // all rates; exclude Manual
    await uaClient.ReconcileMonitoredItemsAsync(desired, token);
}
```

Find the existing mapping-version branch in the coordinator loop (near `ua_server_.SyncMappings`) and add reconcile for each UA session.

- [ ] **Step 4: Build + tests**

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(opc-ua): reconcile MonitoredItems for mapped tags"
```

---

### Task 6: Polymorphic source APIs + MaxMappedTags

**Files:**
- Modify: `src/OpcBridge.App/Program.cs` (`GET/POST /api/da/sources`, mapping add)
- Modify: `src/OpcBridge.App/DaServerConfigRequest.cs` (optional fields with defaults)
- Modify: `src/OpcBridge.App/MappingStore.cs` **or** enforce only in API using settings snapshot
- Test: `tests/OpcBridge.LoadTest/UaSourceApiTests.cs`

**Interfaces:**
- Produces JSON shape for sources:

```json
{
  "sourceId": "kep",
  "displayName": "Kepware",
  "sourceType": "OpcUa",
  "endpointUrl": "opc.tcp://kepware:49320",
  "securityMode": "None",
  "securityPolicy": "None",
  "updateRateMs": 1000,
  "maxMappedTags": 50000,
  "useSubscriptions": true,
  "progId": "",
  "host": ""
}
```

DA responses still include progId/host; include `sourceType: "OpcDa"`.

Validation on `POST /api/da/sources`:
- Always require `SourceId`
- If `SourceType` is OpcUa (case-insensitive): require non-empty `EndpointUrl` starting with `opc.tcp://`; validate security mode/policy pair; run `UaEndpointGuard` against current UA options → 400 if self
- If OpcDa: require `ProgId` (existing expectations)

MaxMappedTags on `POST /api/mappings/add` (and bulk):
- Count existing mappings for `SourceId` + incoming new unique keys
- If source is OpcUa and count > `MaxMappedTags` → 400 `{ "error": "Source kep exceeds MaxMappedTags (50000)." }`

- [ ] **Step 1: API tests via `TestAppHandle`**

```csharp
[Fact]
public async Task PostSource_OpcUa_PersistsTypeAndEndpoint()
{
    await using var handle = await TestAppHandle.StartAsync(/* minimal appsettings */);
    using var res = await handle.PostJsonAsync("/api/da/sources", new {
        sourceId = "kep",
        displayName = "Kepware",
        sourceType = "OpcUa",
        endpointUrl = "opc.tcp://127.0.0.1:49320",
        securityMode = "None",
        securityPolicy = "None",
        updateRateMs = 1000
    });
    Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    using var list = await handle.GetJsonAsync("/api/da/sources");
    // assert sources array contains sourceType OpcUa and endpointUrl
}

[Fact]
public async Task PostSource_OpcUa_SelfEndpoint_Returns400()
{
    // endpointUrl matching appsettings Ua.EndpointUrl loopback equivalent
}
```

- [ ] **Step 2: Implement API + validation**

- [ ] **Step 3: Run filtered tests**

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(opc-ua): polymorphic source APIs and MaxMappedTags"
```

---

### Task 7: Browse + test-connection APIs

**Files:**
- Create: `src/OpcBridge.Ua/OpcUaBrowseService.cs`
- Create: `src/OpcBridge.App/UaBrowseRequests.cs` (request DTOs)
- Modify: `src/OpcBridge.App/Program.cs`
- Test: unit tests for request validation; optional integration skipped without server

**Interfaces:**
- Produces:

```csharp
// POST /api/ua/test-connection
// body: { endpointUrl, securityMode, securityPolicy, username?, password? } OR { sourceId }
// → { ok: true, serverProductName?, sessionId? } | { ok: false, error }

// POST /api/ua/browse
// body: {
//   endpointUrl?, securityMode?, securityPolicy?, username?, password?, sourceId?,
//   nodeId?: "i=85", // Objects folder default
//   maxNodes?: 200
// }
// → { nodes: [ { nodeId, displayName, nodeClass, hasChildren } ], continuationPoint?: null }
```

Browse implementation:
- Short-lived session (or reuse if sourceId connected — v1 may always open temp session for simplicity).
- `Browse` HierarchicalReferences forward; page with `maxNodes` (default 200, max 1000).
- Return Variable/Object/etc. `nodeClass` as string.
- Timeout ~15s; dispose session in `finally`.

- [ ] **Step 1: Validation tests** (missing endpoint → 400)

- [ ] **Step 2: Implement service + endpoints**

- [ ] **Step 3: Manual smoke optional** (if a UA server is available); otherwise unit-only

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(opc-ua): add test-connection and browse APIs"
```

---

### Task 8: Dashboard — OPC UA tab, Sources status, wizard type

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` (nav, views, routes, JS, freeze comment)
- Note: current IA uses **Sources as group label only** with OPC DA under it. Spec wants:

```text
CONNECTIVITY / Sources group
  Sources (status list) — restore clickable page OR use group + list page
  OPC DA
  OPC UA   ← new
  Diagnostics
```

Implement per spec: make **Sources** a real tab again (`data-tab="connection"`, route `connectivity/sources`) status-only list with **type badges**, then OPC DA, OPC UA, Diagnostics.

**Interfaces:**
- Produces:
  - `data-tab="opc-ua"`, `id="view-opc-ua"`, route `connectivity/opc-ua`
  - Controls: `uaCfgSourceId`, `uaCfgDisplayName`, `uaCfgEndpointUrl`, `uaCfgSecurityMode`, `uaCfgSecurityPolicy`, `uaCfgUser`, `uaCfgPass`, `uaCfgUpdateRate`, `uaCfgUseSubscriptions`, `uaCfgMaxMappedTags`, toolbar `uaCfgApply` / `uaCfgReset` / `uaCfgNew` / `uaCfgRemove`, `btnUaTestConnection`
  - Wizard: step 0 type select `wzSourceType` = `OpcDa` | `OpcUa`; branch fields
  - `pickSource` navigates to opc-da or opc-ua by `sourceType`
- Consumes: `/api/da/sources`, `/api/ua/test-connection`

- [ ] **Step 1: Freeze comment + nav + routes**

```html
<button class="tabbtn" data-tab="connection" data-route="connectivity/sources" onclick="navigate('connectivity/sources')">Sources</button>
<button class="tabbtn" data-tab="opc-da" data-route="connectivity/opc-da" onclick="navigate('connectivity/opc-da')">OPC DA</button>
<button class="tabbtn" data-tab="opc-ua" data-route="connectivity/opc-ua" onclick="navigate('connectivity/opc-ua')">OPC UA</button>
<button class="tabbtn" data-tab="diagnostics" ...>Diagnostics</button>
```

```javascript
const ROUTE_TO_TAB = {
  'connectivity/sources': 'connection',
  'connectivity/opc-da': 'opc-da',
  'connectivity/opc-ua': 'opc-ua',
  'connectivity/diagnostics': 'diagnostics',
  // ...
};
// LEGACY: opc-ua → connectivity/opc-ua; connection → connectivity/sources
```

- [ ] **Step 2: `view-connection` status list + `view-opc-ua` form**

Status row includes badge `DA`/`UA` and endpoint or host/ProgId.

OPC UA form mirrors OPC DA layout with `uaCfg*` ids.

- [ ] **Step 3: JS load/save/test**

```javascript
async function saveUaSource() {
  const body = {
    sourceId: el('uaCfgSourceId').value.trim(),
    displayName: el('uaCfgDisplayName').value.trim(),
    sourceType: 'OpcUa',
    endpointUrl: el('uaCfgEndpointUrl').value.trim(),
    securityMode: el('uaCfgSecurityMode').value,
    securityPolicy: el('uaCfgSecurityPolicy').value,
    uaUsername: el('uaCfgUser').value,
    uaPassword: el('uaCfgPass').value,
    updateRateMs: parseInt(el('uaCfgUpdateRate').value, 10) || 1000,
    maxMappedTags: parseInt(el('uaCfgMaxMappedTags').value, 10) || 50000,
    useSubscriptions: el('uaCfgUseSubscriptions').checked,
    progId: '',
    host: ''
  };
  const res = await fetch('/api/da/sources', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
  // handle error/ok; reload sources
}

async function testUaConnection() {
  const res = await fetch('/api/ua/test-connection', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({
    endpointUrl: el('uaCfgEndpointUrl').value.trim(),
    securityMode: el('uaCfgSecurityMode').value,
    securityPolicy: el('uaCfgSecurityPolicy').value,
    username: el('uaCfgUser').value,
    password: el('uaCfgPass').value
  })});
  // show message
}
```

Wizard: first pane type; if OpcUa show endpoint panes; POST with `sourceType`.

- [ ] **Step 4: Docker smoke** — open dashboard, confirm nav OPC UA, freeze ids present in HTML

```bash
curl -sS http://127.0.0.1:18080/ | rg -o 'data-tab="opc-ua"|id="view-opc-ua"|id="uaCfgEndpointUrl"' 
```

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(dashboard): add OPC UA source connectivity UI"
```

---

### Task 9: Tag browser UA path + mapping NodeIds

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` (browse JS for active source type)
- Modify: Help strings if browse errors mention DA-only

**Interfaces:**
- Produces: when `state.selectedSource.sourceType === 'OpcUa'`, call `POST /api/ua/browse` with `sourceId` or connection fields; map Variables via existing add-mapping API with `daItemId = nodeId`
- Consumes: Task 7 browse API; `/api/mappings/add`

- [ ] **Step 1: Branch `browseTags` / tree loader**

```javascript
async function browseActiveSource(nodeId) {
  const src = currentSource();
  const type = (src.sourceType || src.SourceType || 'OpcDa');
  if (type.toLowerCase() === 'opcua') {
    const res = await fetch('/api/ua/browse', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({
      sourceId: src.sourceId,
      endpointUrl: src.endpointUrl,
      securityMode: src.securityMode || 'None',
      securityPolicy: src.securityPolicy || 'None',
      nodeId: nodeId || 'i=85',
      maxNodes: 200
    })});
    // render nodes; Variables get "Map" action
  } else {
    // existing /api/da/tags path
  }
}
```

- [ ] **Step 2: Map action posts NodeId as `daItemId`**

- [ ] **Step 3: Smoke manually or assert HTML/JS contains `/api/ua/browse`

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(dashboard): browse and map OPC UA NodeIds"
```

---

### Task 10: Help + context.md

**Files:**
- Modify: `src/OpcBridge.App/HelpContent.cs`
- Modify: `context.md`
- Test: `HelpContentTests` if it freezes Connectivity lines — update assertions

- [ ] **Step 1: Help**

Document:
- Connectivity: Sources (status), OPC DA, **OPC UA (client sources)**, Diagnostics
- Distinction: UA **source** vs UA **server endpoint**
- Item id for UA = NodeId string
- Security modes supported
- Scale: only mapped tags subscribed

- [ ] **Step 2: context.md**

Add section under architecture:

```markdown
### OPC UA inbound sources (feature)

- SourceType OpcUa connects outbound as UA client to external servers.
- Bridge still hosts the outbound UA server mirror.
- Mapped NodeIds only; subscriptions primary; write-through supported.
```

- [ ] **Step 3: Commit**

```bash
git commit -m "docs(opc-ua): document UA sources in help and context"
```

---

### Task 11: End-to-end verification

**Files:** none required

- [ ] **Step 1: Full build**

```bash
docker run --rm -v "$PWD":/workspace -w /workspace mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'dotnet build OpcDaToUaBridge.sln && dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --no-build'
```

Expected: 0w 0e; tests green (skip live UA if none).

- [ ] **Step 2: Docker image smoke**

```bash
docker build -f Dockerfile.local -t opcbridge:local .
docker stop opcbridge 2>/dev/null || true
docker run --rm -d --name opcbridge -p 18080:8080 -p 4840:4840 opcbridge:local
curl -sS http://127.0.0.1:18080/health
curl -sS http://127.0.0.1:18080/ | rg 'opc-ua|view-opc-ua|uaCfgEndpointUrl'
```

- [ ] **Step 3: Lab checklist** (Windows host with Kepware/Siemens when available)

1. Add OPC UA source → Test connection OK  
2. Browse Objects → map 1 tag → live value on Monitor  
3. Set Writeable → write from HMI/UA client → external tag changes  
4. Map large batch (lab) → confirm no process hang; subscriptions active  
5. Existing DA source still connects  

- [ ] **Step 4: Final commit only if verification fixes needed**

---

## Spec coverage checklist

| Spec area | Task(s) |
|---|---|
| Goal / inbound client role | 4–5, 8 |
| SourceType + UA fields + sources.json migrate | 1 |
| Security None/Sign/SignAndEncrypt Basic256Sha256 | 4, 6, 8 |
| Browse + map; subtree later | 7, 9 (subtree not built) |
| Subscriptions first, poll fallback | 5, 4 ReadAsync |
| Write-through | 4 WriteAsync + existing WriteQueue |
| Scale ≥20k mapped; batching | 5 |
| Self-endpoint guard | 2, 4, 6 |
| Client PKI `pki/ua-client/` | 4 |
| APIs polymorphic + ua browse/test | 6, 7 |
| Dashboard OPC UA tab + wizard | 8 |
| MaxMappedTags | 1, 6 |
| Help / context | 10 |
| Non-goals respected | all (no history/events/subtree/rename) |

## Placeholder / consistency review

- No TBD steps; types named consistently: `SourceTypes`, `OpcUaSourceClient`, `ISubscribableSourceClient`, `UaEndpointGuard`, `UaQualityMapper`.
- `DaItemId` remains the mapping item field (NodeId string for UA).
- `/api/da/sources` kept as legacy path with extended body.
