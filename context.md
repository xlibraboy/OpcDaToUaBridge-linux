# context.md — OpcDaToUaBridge

Instruction file for AI agents working in this repo. All facts below are verified against committed code on `main` as of 2026-07-23.

## What this project is

A bridge that mirrors OPC DA tag values into an OPC UA server, with a web dashboard for configuration and monitoring and an optional Avalonia HMI operator client. Bridge is a single Windows process at runtime (DA COM requires Windows); the HMI is a **separate process**. Edited and built on Linux, deployed to a Windows host.

- **DA side**: connects to one or more OPC DA servers via direct COM/DCOM interop (no vendor SDK).
- **UA side**: an in-process OPC UA server (OPCFoundation.NetStandard SDK) that mirrors DA reads as UA variables.
- Inbound PLC drivers: `SourceType=MelsecA3n` → `MelsecA3nClient` (1C Frame serial) via `DaClientFactory`; UI under Connectivity → Drivers.
1: - **Dashboard**: ASP.NET Core minimal API + single-page HTML dashboard for sources, mappings, browsing, live values, MQTT, InfluxDB writer config, and Diagram topology.
- **HMI**: Avalonia desktop operator client (`OpcBridge.Hmi`) connecting to bridge HTTP + SignalR on port 8080 only. Faceplate chart loads history via bridge `GET /api/hmi/trends` (Influx proxy — HMI never holds an Influx token). Bridge can log opt-in tags to InfluxDB 2.x/3.x via writer. Auth / Android remain deferred non-goals.
2: | `OpcBridge.Influx` | `net8.0` | Continuous opt-in historical writer to InfluxDB 2.x/3.x (`IInfluxWriter`). |
| `OpcBridge.App` | `net8.0` (Web SDK) | Entrypoint, HTTP API, dashboard HTML/JS (`DashboardPage`), `BridgeWorker`, `BridgeState`, `MappingStore`, `DaRuntimeSettings`, `DaClientFactory`, `DashboardLogStore`, Influx runtime settings, HMI snapshot/write/trends API + SignalR hub. References Core, Da, Ua, Mqtt, Influx, Client. |
| `OpcBridge.Client` | `net8.0` | Shared HMI/App wire DTOs and tag-cache merge helpers: `HmiTagDto`, `HmiTagsResponse`, `HmiValueDelta`, `HmiWriteRequest`/`HmiWriteResponse`, `HmiMappingsChanged`, `HmiTagCache`, `HmiTrendPoint`, `HmiTrendResponse`. No framework deps. |
| `OpcBridge.Hmi` | `net8.0` (WinExe) | Avalonia 11 operator client: connect bar, tag grid, faceplate write + sparkline (last-hour trends via bridge proxy). References Client; SignalR client for `/hmi`. Separate process from the bridge. |

## Project map

Projects under `src/`, all .NET 8, `ImplicitUsings` + `Nullable` enabled.

| Project | TFM | Role |
|---|---|---|
| `OpcBridge.Core` | `net8.0` | Cross-project contract types: `TagMapping`, `BridgeValue`, `BridgeOptions`, `QualityMapper`. No dependencies. |
| `OpcBridge.Da` | `net8.0;net8.0-windows` | DA client + browsing + server enumeration + Windows impersonation. Multi-targeted so it compiles on Linux but only runs COM on Windows. |
| `OpcBridge.Ua` | `net8.0` | UA server: `BridgeUaServer` (extends `StandardServer`), `BridgeNodeManager` (extends `CustomNodeManager2`), `UaServerHost`. Depends on `OPCFoundation.NetStandard.Opc.Ua` 1.5.378.145. |
| `OpcBridge.Mqtt` | `net8.0` | MQTT publish/subscribe helper for mapped tags. |
1: - **Dashboard**: ASP.NET Core minimal API + single-page HTML dashboard for sources, mappings, browsing, live values, MQTT, InfluxDB writer config, and Diagram topology.
- **HMI**: Avalonia desktop operator client (`OpcBridge.Hmi`) connecting to bridge HTTP + SignalR on port 8080 only. Faceplate chart loads history via bridge `GET /api/hmi/trends` (Influx proxy — HMI never holds an Influx token). Bridge can log opt-in tags to InfluxDB 2.x/3.x via writer. Auth / Android remain deferred non-goals.
2: | `OpcBridge.Influx` | `net8.0` | Continuous opt-in historical writer to InfluxDB 2.x/3.x (`IInfluxWriter`). |
| `OpcBridge.App` | `net8.0` (Web SDK) | Entrypoint, HTTP API, dashboard HTML/JS (`DashboardPage`), `BridgeWorker`, `BridgeState`, `MappingStore`, `DaRuntimeSettings`, `DaClientFactory`, `DashboardLogStore`, Influx runtime settings, HMI snapshot/write/trends API + SignalR hub. References Core, Da, Ua, Mqtt, Influx, Client. |
| `OpcBridge.Client` | `net8.0` | Shared HMI/App wire DTOs and tag-cache merge helpers: `HmiTagDto`, `HmiTagsResponse`, `HmiValueDelta`, `HmiWriteRequest`/`HmiWriteResponse`, `HmiMappingsChanged`, `HmiTagCache`, `HmiTrendPoint`, `HmiTrendResponse`. No framework deps. |
| `OpcBridge.Hmi` | `net8.0` (WinExe) | Avalonia 11 operator client: connect bar, tag grid, faceplate write + sparkline (last-hour trends via bridge proxy). References Client; SignalR client for `/hmi`. Separate process from the bridge. |

Reference graph: `App → {Core, Da, Ua, Mqtt, Influx, Client}`, `Hmi → Client`, `Da → Core`, `Ua → Core`, `Mqtt → Core`, `Influx → Core`. Core and Client depend on nothing.

## Key contracts (OpcBridge.Core)

- **`TagMapping`** — the mapping unit. Keyed by `(SourceId, DaItemId)` (case-insensitive). Fields include: `SourceId` (default `"default"`), `DaItemId`, `UaNodeId`, `DisplayName`, `Description`, `DataType`, `Enabled`, `Mode` (`"Source"` | `"Manual"`), `ManualValue`, `PollRateMs`, `DeadbandPct`, `Writeable`, `AccessRights` (`Read` / `Read-Write` / `Write`), `MqttEnabled`, `InfluxEnabled`, plus optional provider-link fields for DA→DA forwarding. `TagMode.Source` / `TagMode.Manual` are the mode constants. `AccessRights`, `Writeable`, and simulation (`Mode`/`ManualValue`) are independent concerns.
- **`BridgeValue`** — `record(SourceId, DaItemId, Value, TimestampUtc, DaQuality, IsGood)`. The normalized data unit that crosses the DA→state→UA boundary.
- **`BridgeOptions`** — `Mappings` (seed list) + `RateLimits` (`Dictionary<int,int>` mapping poll-rate-ms → max-tags-per-group).
- **`QualityMapper`** — `IsGoodDaQuality(int)` used by `OpcDaClient` to set `BridgeValue.IsGood`.

## Architecture & data flow

```
OPC DA server(s)
      │  COM/DCOM sync read (IOPCSyncIO)
      ▼
OpcDaClient (one IDaClient per source)
      │  IReadOnlyList<BridgeValue>
      ▼
BridgeWorker.RunSourcePollerAsync  ── one poller per (source, rate) group
      │
      ├──► BridgeState.UpdateDaRead / SetValue   (in-memory cache, dashboard feed)
      └──► UaServerHost.UpdateValue → BridgeNodeManager.UpdateValue  (UA node mirror)
```

The UA server is a **mirror**, not a computation path. Every value shown in the dashboard's Live Values table comes from a DA read cached in `BridgeState`; reading the UA node back yields the same value.

### Multi-source (committed, in use)

- `DaRuntimeSettings` is a live source registry: `GetSnapshot()`, `UpsertSource`, `TryRemoveSource` (refuses to remove the last source), `SetUpdateRate`, `SetSourceUpdateRate`. Snapshot is an immutable record with a monotonic `Version`.
- `BridgeWorker.ReconfigureSessionsAsync` reconciles the live source set against active `SourceSession` instances — disposes removed sources, connects added sources, faults individually on failure.
- UA nodes are namespaced by source: default `UaNodeId` is `ns=2;s={SourceId}/{DaItemId}`; `BridgeNodeManager` keys variables by `"{SourceId}::{DaItemId}"`.
- `MappingStore` enforces uniqueness on `(SourceId, DaItemId)` and persists to `mappings.json` in `AppContext.BaseDirectory`.

### Per-tag poll rates (committed, in use)

- `TagMapping.PollRateMs` (>0 overrides the source default).
- `BridgeWorker` builds one poller task per `(SourceId, distinct-rate)` group. `SourceMappingCache.GetDistinctRates` derives the set.
- `Bridge:RateLimits` in `appsettings.json` caps tags-per-rate-group; `BridgeState.UpdateRateGroup` reports `ok`/`warning`/`saturated`/`limit-exceeded` per group.

### OPC UA inbound sources (feature)

- SourceType `OpcUa` connects outbound as a UA **client** to external servers (`OpcUaSourceClient` via `DaClientFactory`; endpoint URL, security mode `None`/`Sign`/`SignAndEncrypt` with policy `None`/`Basic256Sha256`, optional UserName token).
- Bridge still hosts the outbound UA server mirror — UA sources feed the same `BridgeState` → UA node pipeline as DA sources.
- Mapped NodeIds only (UA item id = NodeId string); subscriptions primary (poll is fallback for the mapped set); write-through supported for writable mappings.

### Manual mode (committed)

When `TagMapping.Mode == "Manual"`, `BridgeWorker.ApplyManualMappings` synthesizes a `BridgeValue` from `ManualValue` (parsed by `TryConvertManualValue`) without a DA read. Supported types: BOOL, BYTE, SBYTE, INT16, UINT16, INT32, UINT32, INT64, UINT64, FLOAT, DOUBLE, DECIMAL, STRING, plus type inference.

### Failure resilience

- A failed source read enqueues the source id to `failedSourceQueue`; the coordinator loop tears down all pollers + sessions and rebuilds on the next tick. The app stays alive.
- `BridgeState.SetSourceError` marks the source `"Faulted"` and surfaces the message; aggregate `DaConnectionState` becomes `"Partial"` if some sources are connected.
- Empty `ProgId` on a source → state `"Disconnected"` with a clear error, no crash.

## DA client seam

`IDaClient` (`OpcBridge.Da`) is the pluggable boundary: `ConnectAsync`, `ReadAsync`, `IAsyncDisposable`. Implementations: `OpcDaClient` (OPC DA) and `OpcUaSourceClient` (OPC UA sources); `DaClientFactory` picks by `SourceType`.

`DaClientFactory.Create(settings, source)` returns `new OpcDaClient(source.ToOptions())`. **There is no `SimulatedDaClient` and no `Da:Mode` setting in committed code** — older notes mentioning Simulation/OpcDa runtime switching describe a pattern that is not present. Do not reintroduce it without explicit instruction.

`OpcDaClient` details:
- Direct COM interop via `IOPCServer`, `IOPCItemMgt`, `IOPCSyncIO` (declared inline as `[ComImport]` interfaces with the OPC DA GUIDs).
- One OPC DA group per poll rate, created lazily on first read at that rate.
- Sync device reads (`OPCDataSourceDevice = 2`).
- Remote DCOM with credentials → `LogonUser` (`LOGON32_LOGON_NEW_CREDENTIALS`) + `WindowsIdentity.RunImpersonated` wrapping `ConnectDirect`. See `WindowsImpersonation.cs`.
- All COM-touching methods are `[SupportedOSPlatform("windows")]`; non-Windows calls throw `PlatformNotSupportedException`. `OperatingSystem.IsWindows()` guards in `Program.cs` keep browse/enumerate endpoints from invoking COM on Linux.

## UA server

- SDK: `OPCFoundation.NetStandard.Opc.Ua` 1.5.378.145.
- Namespace URI `urn:ohmypi:opc-da-to-ua-bridge:tags` (index 2 at runtime). Root folder `OpcDaTags` under Objects.
- Endpoint `opc.tcp://0.0.0.0:4840/OpcDaToUaBridge`, security policy `None` only, `AutoAcceptUntrustedCertificates = true` (dev default — tighten for production).
- PKI directory stores: `pki/own`, `pki/trusted`, `pki/issuers`, `pki/rejected`.
- `BridgeNodeManager.SyncMappings` adds/removes UA nodes live when the mapping set version changes; `UpdateValue` writes value/timestamp/statuscode and clears change masks.
- Data types: BOOL, BYTE, INT16, INT32, INT64, FLOAT, DOUBLE, STRING map to UA `DataTypeIds`; unknown → `BaseDataType`.

## HTTP API & dashboard

ASP.NET Core minimal API, listens on `http://0.0.0.0:8080`. Dashboard is a single HTML page (`DashboardPage.FullHtml`) served as explicit UTF-8 bytes at `/`.

Endpoints (all in `Program.cs`):
- `GET /` — dashboard HTML
- `GET /api/values` — current `BridgeState` values
- `GET /api/hmi/tags` — HMI tag snapshot (mappings + current values) for the operator client
- `POST /api/hmi/write` — HMI write request; gated on mapping `Writeable`; reuses `WriteQueue` / `ApplyUaWriteAsync` path
- `GET /api/hmi/trends?sourceId=&daItemId=&from=&to=&maxPoints=` — history via bridge Influx proxy (HMI never holds Influx token). Soft-fails with empty points + `error` when Influx unavailable.
- SignalR hub `/hmi` — events `values` (batched `HmiValueDelta[]`) and `mappingsChanged` (`HmiMappingsChanged`)
- `GET /api/status` | `/api/dashboard` — bridge + UA status (dashboard also includes values)
- `GET /api/logs?limit=&level=` — `DashboardLogStore` ring buffer (500 entries)
- `GET /api/app-info` | `/api/version` — assembly info
- `GET /docs/` — static documentation site (MkDocs/Material, built into `docs-site`, opened in a new browser tab by the dashboard Docs menu)
- `GET /api/da/sources` — source registry
- `POST /api/da/sources` — upsert source; `POST /api/da/sources/remove`; `POST /api/da/sources/update-rate`; `POST /api/da/update-rate`
- `POST /api/da/servers` — enumerate OPC DA servers (Windows-only, 10s timeout)
- `POST /api/da/tags` — browse tags (Windows-only, 15s timeout)
- `GET /api/mappings`; `POST /api/mappings/add` | `/update` | `/remove`
- `GET /api/da-links` (and related write endpoints) — DA→DA provider/consumer links
- MQTT config/status/values endpoints under `/api/mqtt/*`
- Influx config/connect/status endpoints under `/api/influx/*` (opt-in per-tag `InfluxEnabled` logging)
- `GET /health` — `{ "status": "ok" }`

HMI note: the operator UI is not embedded in the dashboard; run `dotnet run --project src/OpcBridge.Hmi` against the bridge base URL (`http://<host>:8080`). Historical trends are served only through the bridge proxy (`GET /api/hmi/trends`); the HMI has no Influx config or token.

`DashboardLogStore` is also wired as an `ILoggerProvider` (`DashboardLogProvider`), so `ILogger` calls under the `OpcBridge.*` categories at Information+ appear in the dashboard's Logs panel.

### Diagram tab (dashboard JS in `DashboardPage`)

Topology views under **Diagram** (SVG canvas, live status colors):

| Sub-tab | Default rendering | Scale strategy |
|---|---|---|
| **All** | Aggregated plant overview: one row per DA source → tag-group box → UA + MQTT hubs | O(sources) nodes/trunks, not O(tags) |
| **DA→UA** | Aggregated source trunks by default; click a tag-group to expand | Expanded detail is paged (`DIAG_EXPAND_PAGE = 80` tags/page) |
| **DA-to-DA** | Aggregated source-pair trunks (provider source → consumer source); click count badge to expand | Expanded pair endpoints are paged (`DIAG_EXPAND_PAGE = 80`); expand keys `dada:{from}=>{to}` |
| **MQTT** | Aggregated per-source MQTT groups → broker; click group to expand | Expanded tags paged (`DIAG_EXPAND_PAGE = 80`); expand keys `mqtt:{sourceId}` |

**Zoom / pan (all sub-tabs):**
- Toolbar: `−` / `%` / `+` / **Fit** / **Fit W** / **Reset**
- Range **25%–300%**, step 10%
- **Ctrl+wheel** zooms toward cursor
- Drag empty canvas to pan
- Zoom persists across live re-render and sub-tab switch

**Status colors:** grey = inactive/default topology; green/yellow/red only when live/active. Animated dashed edges indicate flow.

**Tag Browser Mapped badge:** `loadMappings()` calls `refreshTagBrowserMappedBadges()` so Browse All Tags shows **Mapped** immediately after Add/Remove without a re-browse.

**Scaling principle for 1k–10k+ tags:** aggregate → expand → focus. Never draw every tag on the overview. Collapsed All/DA→UA/MQTT cost is ~2–3 nodes + 1 trunk per source; DA-to-DA cost is O(source-pairs), not O(links).

## Configuration (`appsettings.json`)

- `Da:ProgId`, `Da:Host`, `Da:UpdateRateMs` — single-source seed (becomes the `default` source on first run). Multi-source config is via the API at runtime, persisted in `mappings.json` + the in-memory `DaRuntimeSettings`.
- `Ua:ApplicationName`, `Ua:EndpointUrl`, `Ua:AutoAcceptUntrustedCertificates`.
- `Bridge:RateLimits` — rate→max-tags map.
- `Bridge:Mappings` — seed mappings (used only if `mappings.json` is absent).

Runtime state files live beside the running app in `AppContext.BaseDirectory` (`mappings.json`). Preserve these during deploy cutover or live bridge state is lost.

## Build

Two verified clean-build paths; both must stay at **0 Warning(s), 0 Error(s)**.

**Linux (dev/build machine) — Docker:**
```bash
docker run --rm -v "$PWD":/workspace -w /workspace \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet build OpcDaToUaBridge.sln
```
The Linux build is the early-warning path for cross-platform analyzer warnings (CA1416 etc.) on the Windows-targeted DA code.

**Windows host — user-profile dotnet:**
```bash
"%USERPROFILE%\AppData\Local\Microsoft\dotnet\dotnet.exe" build OpcDaToUaBridge.sln
```
The `C:\Program Files\dotnet\dotnet.exe` install lacks the ASP.NET shared framework — always use the user-profile dotnet for this app. .NET 8 SDK (8.0.422) is the verified version; no `global.json` pins it.

**Stop the running app before building on Windows** — a running `OpcBridge.App.exe` / `dotnet.exe` locks `OpcBridge.Ua.dll` and causes silent publish failures.

## Deploy to Windows

**Target host (verified):** `DESKTOP-MENOJUS` / SSH alias `xlibr-win` (`192.168.20.13`), user `xlibr`, path `C:\Users\xlibr\Documents\OpcDaToUaBridge\publish\`.

**Linux publish (framework-dependent, 32-bit COM):**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'dotnet publish src/OpcBridge.App/OpcBridge.App.csproj -c Release -r win-x86 --self-contained false -o /src/publish.tmp'
```
Package `publish.tmp` → tar.gz, SCP to host as `publish-new.tar.gz`, then run host deploy script (backs up `appsettings.json` / `mappings.json` / `pki`, clears publish, extracts, restores runtime state, re-registers task).

**Host launcher:** scheduled task `OpcDaToUaBridge` → `scripts/windows/start-published-bridge.cmd` which `pushd`s into `publish\` and runs:
`C:\Program Files (x86)\dotnet\dotnet.exe OpcBridge.App.dll`
(CWD must be the publish folder so `appsettings.json` resolves.)

**register-published-task.ps1** kills old process, re-registers AtStartup S4U task, starts it, probes `http://127.0.0.1:8080/health`.

**Deploy guards:**
- Restore host-specific `appsettings.json` (do not ship a broken `EndpointUrl` from the build machine).
- Preserve `mappings.json` and `pki/` across cutover.
- Do **not** copy test platform DLLs (`Microsoft.TestPlatform.*`, `Mono.Cecil.*`, xunit, etc.) into publish.
- Delete stale apphost / pollution before copy if the directory was previously dirtied.
- Optional: delete `publish/pki/own/cert.der` when UA hostname/SAN must regenerate.

**Git remotes:** `origin` = `OpcDaToUaBridge-linux` (HTTPS), `win` = `OpcDaToUaBridge-windows` (SSH). Push merges to both.

## Conventions

- **Zero-warning build bar.** Cross-platform analyzer warnings (CA1416) are fixed by routing Windows-only calls through `[SupportedOSPlatform("windows")]` helper methods — a runtime `OperatingSystem.IsWindows()` guard alone does not discharge the warning.
- **Direct COM over vendor SDKs.** OPC DA interop is hand-declared `[ComImport]` interfaces, not a commercial wrapper.
- **Interface seams over monoliths.** `IDaClient` is the DA seam; `BridgeValue`/`TagMapping` are the cross-project boundary types. Don't pass raw COM types across project boundaries.
- **Failure-resilient by default.** Errors are surfaced in the dashboard (per-source state, `LastError`), not fatal. A failed real-mode connect must leave the app alive and recoverable.
- **Backend-first.** Verify the backend seam (`IDaClient`, `BridgeWorker`, `BridgeState`) before wiring dashboard controls.
- **Conventional commits** (`feat:`, `fix:`, etc.). Committed code on `main` is authoritative; uncommitted changes are a known risk.
- **Tests exist** under `tests/OpcBridge.LoadTest` (xUnit). Prefer `InternalsVisibleTo` over making types public for tests. Run in Docker: `dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj`. Primary verification remains build-clean (0w/0e) + host `/health` smoke.

## Gotchas

- The mental-model notes about `Da:Mode` / `SimulatedDaClient` / `POST /api/da/mode` / "mode switching" describe a pattern **not present** in committed code. `DaClientFactory` only builds `OpcDaClient`. Trust committed code, not those notes.
- `appsettings.json` is `reloadOnChange: true`, but `DaRuntimeSettings` is a singleton seeded once at startup; runtime source changes go through the API, not by editing the file.
- `MappingStore` loads from `mappings.json` if it exists, ignoring `Bridge:Mappings` in `appsettings.json`. Delete `mappings.json` to reseed from config.
- The DA client throws if mappings change shape after connect (`EnsureGroupItemsConfigured` checks count + item-id order). The coordinator handles this by rebuilding sessions on mapping-version change.
- `OpcDaClient.DisposeAsync` releases COM objects only on Windows; on Linux it nulls references (the client never connected there).
- 32-bit COM alignment: if the Windows runtime uses 32-bit OPC DA servers, publish with `-r win-x86`; a 64-bit process cannot activate them without DCOM surrogate setup.
- **Decision #8 superseded 2026-07-01:** the UA server now supports writes for mappings with `Writeable=true`. Writes drain through a bounded channel (`WriteQueue`, capacity 1024) to `IOPCSyncIO.Write`, one consumer per source keeping COM work on that source's STA thread. Read-only mirror behavior is preserved for non-writeable mappings. `BridgeNodeManager.OnWriteValue` awaits a `TaskCompletionSource<bool>` resolved by the write queue consumer; on `false`/timeout it rejects the UA write with `BadNoCommunication`/`BadRequestTimeout`.
- **Deadband under subscriptions only.** `TagMapping.DeadbandPct` is applied as the OPC DA group's `percentDeadband` and only filters at the source via `IOPCDataCallback` callbacks. If a source falls back to polling (no `IOPCDataCallback` support), deadband has no effect — do not add client-side filtering.
- **Subscriptions are opt-in via `Da:UseSubscriptions`** (default `true`). If `IConnectionPointContainer.FindConnectionPoint` for `IOPCDataCallback` fails, `OpcDaClient` silently falls back to device reads and `OnCallbackValues` never fires.
