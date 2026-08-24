# context.md — OpcBridge

Instruction file for AI agents working in this repo. All facts below are verified against committed code on `main` as of 2026-08-07 (`a55e3da`).

## What this project is

A bridge that mirrors OPC DA tag values into an OPC UA server, with a web dashboard for configuration and monitoring and an optional Avalonia HMI operator client. The bridge is a **Linux-first** .NET 8 process: it supports OPC UA **inbound sources** (UA client → external servers), serial PLC drivers (Melsec A3N, S7-200 PPI), and — on Windows only — OPC DA sources via direct COM/DCOM (DA COM requires Windows). The HMI is a **separate process**.

- **DA side**: connects to OPC DA servers via direct COM/DCOM interop (no vendor SDK) — Windows-only at runtime.
- **UA side**: an in-process OPC UA server (OPCFoundation.NetStandard SDK) that mirrors source reads as UA variables.
- **UA inbound sources**: `SourceType=OpcUa` connects outbound as a UA client (`OpcUaSourceClient`) to external servers; feeds the same `BridgeState` → UA node pipeline.
- **PLC drivers**: `SourceType=MelsecA3n` → `MelsecA3nClient` (1C Frame serial), `SourceType=S7200Ppi` → `S7200Client` (PPI serial); UI under Connectivity → Drivers.
- **Dashboard**: ASP.NET Core minimal API + single-page HTML dashboard for sources, mappings, browsing, live values (with data-type column), MQTT, InfluxDB writer config, and Diagram topology.
- **HMI**: Avalonia desktop operator client (`OpcBridge.Hmi`) connecting to bridge HTTP + SignalR on port 8080 only. Faceplate chart loads history via bridge `GET /api/hmi/trends` (Influx proxy — HMI never holds an Influx token). Bridge can log opt-in tags to InfluxDB 2.x/3.x via writer. Auth / Android remain deferred non-goals.

## Project map

Projects under `src/`, all .NET 8, `ImplicitUsings` + `Nullable` enabled.

| Project | TFM | Role |
|---|---|---|
| `OpcBridge.Core` | `net8.0` | Cross-project contract types: `TagMapping`, `BridgeValue`, `BridgeOptions`, `TagMode`/`TagAccessRights` constants. No dependencies. |
| `OpcBridge.Da` | `net8.0;net8.0-windows` | DA client + browsing + server enumeration + Windows impersonation. Multi-targeted so it compiles on Linux but only runs COM on Windows. `InternalsVisibleTo OpcBridge.LoadTest` (for `DaConnectErrorClassifier`). |
| `OpcBridge.Ua` | `net8.0` | UA server: `BridgeUaServer` (extends `StandardServer`), `BridgeNodeManager` (extends `CustomNodeManager2`), `UaServerHost`, plus `OpcUaSourceClient` (the UA **inbound** source client). Depends on `OPCFoundation.NetStandard.Opc.Ua` 1.5.378.145. `InternalsVisibleTo OpcBridge.LoadTest`. |
| `OpcBridge.App` | `net8.0` (Web SDK) | Entrypoint, HTTP API, dashboard HTML/JS (`DashboardPage`), `BridgeWorker`, `BridgeState`, `MappingStore`, `DaRuntimeSettings`, `SourceClientFactory`, `WriteQueue`, `DashboardValues` (data-type inference), `DashboardLogStore`, Influx runtime settings, HMI snapshot/write/trends API + SignalR hub. References Core, Da, Ua, Mqtt, Influx, Client. |
| `OpcBridge.Mqtt` | `net8.0` | MQTT publish/subscribe helper for mapped tags. |
| `OpcBridge.Influx` | `net8.0` | Continuous opt-in historical writer to InfluxDB 2.x/3.x (`IInfluxWriter`). |
| `OpcBridge.Client` | `net8.0` | Shared HMI/App wire DTOs and tag-cache merge helpers: `HmiTagDto`, `HmiTagsResponse`, `HmiValueDelta`, `HmiWriteRequest`/`HmiWriteResponse`, `HmiMappingsChanged`, `HmiTagCache`, `HmiTrendPoint`, `HmiTrendResponse`. No framework deps. |
| `OpcBridge.Hmi` | `net8.0` (WinExe) | Avalonia 11 operator client: connect bar, tag grid, faceplate write + sparkline (last-hour trends via bridge proxy). References Client; SignalR client for `/hmi`. Separate process from the bridge. |

Reference graph: `App → {Core, Da, Ua, Mqtt, Influx, Client}`, `Hmi → Client`, `Da → Core`, `Ua → Core`, `Mqtt → Core`, `Influx → Core`. Core and Client depend on nothing.

## Key contracts (OpcBridge.Core)

- **`TagMapping`** — the mapping unit. Keyed by `(SourceId, ItemId)` (case-insensitive). Fields: `SourceId` (default `"default"`), `ItemId` (source-side item id; for UA sources a NodeId string like `ns=2;s=Tag00001`), `UaNodeId`, `DisplayName`, `Description`, `DataType`, `Enabled`, `Mode` (`"Source"` | `"Manual"`), `ManualValue`, `PollRateMs`, `DeadbandPct`, `Writeable`, `AccessRights`, `MqttEnabled`/`MqttTopic`, `InfluxEnabled`, plus optional provider-link fields (`ProviderSourceId`/`ProviderItemId`) for provider→consumer forwarding.
- **`AccessRights`** — `Read` (default), `Read-Write`, `Write` (constants in `TagAccessRights`; note the hyphen in Read-Write). The UA mirror node's `AccessLevel` is derived from it: `Read` → `CurrentRead`, `Write` → `CurrentWrite` (write-only node, reads rejected), `Read-Write` → both. `Writeable` is derived (`true` for Read-Write/Write) and controls the node's `OnWriteValue` handler. **Write-only tags are not source-read** (`SourceMappingCache.SourceRead` filter + `BuildDesiredSampling` skip them).
- **`BridgeValue`** — `record(SourceId, ItemId, Value, TimestampUtc, DaQuality, IsGood)`. The normalized data unit that crosses the source→state→UA boundary.
- **`BridgeOptions`** — `Mappings` (seed list) + `RateLimits` (`Dictionary<int,int>` mapping poll-rate-ms → max-tags-per-group).
- **`QualityMapper`** — `IsGoodDaQuality(int)` used by `OpcDaClient` to set `BridgeValue.IsGood`.

## Architecture & data flow

```
OPC UA server(s) ──UA client (OpcUaSourceClient, subscriptions)──┐
OPC DA server(s) ──COM/DCOM (OpcDaClient, Windows)──────────────┤
                                                                 ▼
                                              BridgeWorker (poller/subscription fan-in)
                                                                 │  IReadOnlyList<BridgeValue>
                                                                 ▼
                              BridgeState.UpdateDaRead / SetValue   (in-memory cache, dashboard feed)
                              UaServerHost.UpdateValue → BridgeNodeManager.UpdateValue  (UA node mirror)
```

The UA server is a **mirror**, not a computation path. Every value shown in the dashboard's Live Values table comes from a source read cached in `BridgeState`; reading the UA node back yields the same value.

### Multi-source

- `DaRuntimeSettings` is a live source registry: `GetSnapshot()`, `UpsertSource`, `TryRemoveSource` (refuses to remove the last source), `SetUpdateRate`, `SetSourceUpdateRate`. Snapshot is an immutable record with a monotonic `Version`.
- `BridgeWorker.ReconfigureSessionsAsync` reconciles the live source set against active `SourceSession` instances — disposes removed sources, connects added sources, faults individually on failure.
- UA nodes are namespaced by source: default `UaNodeId` is `ns=2;s={SourceId}/{ItemId}`; `BridgeNodeManager` keys variables by `"{SourceId}::{ItemId}"` (`BridgeState.NormalizeKey` is the same key, `internal`).
- `MappingStore` enforces uniqueness on `(SourceId, ItemId)` and persists to `mappings.json` in `AppContext.BaseDirectory`.
- **API gotchas (verified):** `POST /api/mappings/add` and `/bulk-add` are **insert-only** (silently skip existing keys). `POST /api/mappings/update` **replaces** the mapping — omitted fields reset to defaults (DataType→"Auto", Mode→"Source", AccessRights→"Read", PollRateMs→0) — always send all fields. Source deletion is `POST /api/da/sources/remove` with `{"sourceId":...}`; `DELETE /api/da/sources/{id}` does nothing.
- **Access-rights normalization (since `32ba712`):** `ToTagMapping` passes the raw `accessRights` through (empty = absent), and `MappingStore.NormalizeAccessRights` matches case- and hyphen-insensitively. So `writeable: true` alone (no `accessRights`) migrates to `Read-Write`, both `ReadWrite` and `Read-Write` spellings normalize the same (no silent downgrade to `Read`), and an explicit `accessRights` wins over `writeable`.

### Per-tag poll rates

- `TagMapping.PollRateMs` (>0 overrides the source default).
- `BridgeWorker` builds one poller task per `(SourceId, distinct-rate)` group. `SourceMappingCache.GetDistinctRates` derives the set.
- `Bridge:RateLimits` in `appsettings.json` caps tags-per-rate-group; `BridgeState.UpdateRateGroup` reports `ok`/`warning`/`saturated`/`limit-exceeded` per group.

### OPC UA inbound sources (standard, rig-verified to 100k tags / 3 sources)

- SourceType `OpcUa` connects outbound as a UA **client** to external servers (`OpcUaSourceClient` via `SourceClientFactory`; endpoint URL, security mode `None`/`Sign`/`SignAndEncrypt` with policy `None`/`Basic256Sha256`, optional UserName token).
- Mapped NodeIds only (UA item id = NodeId string); subscriptions primary (poll is fallback for the mapped set); write-through supported: UA client writes to the bridge mirror node drain through `WriteQueue` → per-source consumer → `OpcUaSourceClient.WriteAsync` → the external server.
- The `AccessRights` of a UA-source mapping gate the mirror node exactly like DA (see above); Write-only tags are not subscribed/read from the source.
- `OpcUaSourceClient` keeps a `last_desired_mappings_` list and re-reconciles monitored items on session reconnect. **All reconciles (connect, mapping-change, reconnect) are serialized through a `SemaphoreSlim`** — do not remove; concurrent reconciles left stale monitored items (a tag flipped to Write stayed subscribed).
- **Per-source update-rate changes recreate the UA session** (`UpdateRateMs` is part of `SourceConnectionEquals` for UA sources — it drives the subscription `PublishingInterval`, which is fixed at client creation). Without this, `POST /api/da/sources/update-rate` reported success while values kept arriving at the old cadence (regression-tested in `SourceConnectionEqualsTests`).
- **Failed monitored-item creates are retried on a 15s timer** (since `fix/monitored-item-retry`): a tag that does not exist at the source yet (or transiently rejected) is re-attempted automatically until it succeeds or the mapping stops being desired. Without this it stayed disconnected until the next mapping change or reconnect.
- **Named subscriptions per source:** a UA source can run multiple named subscriptions, each with its own update rate — managed via `/api/ua/subscriptions` (GET list with live status, POST upsert) and `/api/ua/subscriptions/remove`, persisted in `sources.json` under each source's `OpcUa` options (`subscriptions`: name 1–64 chars + `updateRateMs`). A mapping opts in via `TagMapping.Subscription` (empty = default bucket; unknown names tolerate-load and group into the default at runtime). Reconcile partitions desired tags with `UaSubscriptionPlan.GroupByBucket` and diffs each bucket independently — a named-bucket rate change recreates only that subscription (servers don't reliably apply live publishing-interval changes), while a source-rate change still recreates the whole session (`UpdateRateMs` stays in `SourceConnectionEquals`). Deleting a sub auto-reassigns its tags to default (`MappingStore.ReassignSubscription` → `movedMappings`). Validation: `updateRateMs <= 0` rejected at the API (400); >= 1 accepted but clamped to a 100 ms floor; max 16 named subs per source (`SourceConfigMigration.MaxUaSubscriptionsPerSource`). UI: dashboard UA Subscriptions tab (requested vs actual interval per sub) and Maps/faceplate subscription select.

### Manual mode

When `TagMapping.Mode == "Manual"`, `BridgeWorker.ApplyManualMappings` synthesizes a `BridgeValue` from `ManualValue` (parsed by `TryConvertManualValue`) without a source read. Supported types: BOOL, BYTE, SBYTE, INT16, UINT16, INT32, UINT32, INT64, UINT64, FLOAT, DOUBLE, DECIMAL, STRING, plus type inference.

### Data-type display (Live Values / Maps / faceplate)

- `DashboardValues` (`src/OpcBridge.App/DashboardValues.cs`, internal static): `BuildDataTypeLookup` (mapping-config types keyed by `NormalizeKey`), `InferDataType(object?)` (CLR → UA names: bool→Boolean, int→Int32, double→Double, string→String, DateTime→DateTime, byte[]→ByteString, etc.), `ResolveDataType(value, lookup, sourceId, itemId)`.
- **Runtime type wins**: the value object's CLR type IS the external source's real type (UA Variant/DA VARTYPE arrive typed; the bridge stores raw). Mapping `DataType` is the fallback when the value is absent/null. `/api/dashboard` projects `dataType` per value via `ResolveDataType`; the frontend shows it in Live Values, the Maps type pill, and the faceplate. The read-path hot loop (`BridgeState.SetValue`/`UpdateValue`) is untouched — inference happens only in the dashboard projection.

### Failure resilience

- A failed source read enqueues the source id to `failedSourceQueue`; the coordinator loop tears down all pollers + sessions and rebuilds on the next tick. The app stays alive. The subscription watchdog (`ScanWatchdog`) detects dead subscriptions and reconnects the source.
- **Per-item failures are handled in the DA client, not the coordinator** (see above): a single bad tag yields a BAD value + retry, never a source teardown. `connectedVersion` is invalidated on any reconfigure failure so every source (including reconnects) is re-evaluated on the next tick.
- `BridgeState.SetSourceError` marks the source `"Faulted"` and surfaces the message; aggregate `DaConnectionState` becomes `"Partial"` if some sources are connected.
- Empty `ProgId` on a DA source → state `"Disconnected"` with a clear error, no crash. The baked-in DA `default` source always fails on Linux (`PlatformNotSupportedException`, COM) — expected; remove it via the API on UA-only rigs.

## Source client seam

`ISourceClient` (`OpcBridge.Da`) is the pluggable boundary: `ConnectAsync`, `ReadAsync`, `WriteAsync`, `IAsyncDisposable` (+ `TryGetTagMetadata` for DA browsing). Implementations: `OpcDaClient` (OPC DA, Windows), `OpcUaSourceClient` (UA inbound, also `ISubscribableSourceClient`/`ISubscriptionActiveSource`), `MelsecA3nClient`, `S7200Client`.

`SourceClientFactory.Create(settings, source)` picks by `SourceType` (note: the class is `SourceClientFactory`, not `DaClientFactory`). It takes an optional `ILoggerFactory` — the UA client **must** receive a real logger (it was once created with `NullLogger`, making all its logs invisible; regression-tested). There is no `SimulatedDaClient` and no `Da:Mode` setting in committed code.

`OpcDaClient` details:
- Direct COM interop via `IOPCServer`, `IOPCItemMgt`, `IOPCSyncIO` (declared inline as `[ComImport]` interfaces with the OPC DA GUIDs).
- One OPC DA group per poll rate, created lazily on first read at that rate.
- Sync device reads (`OPCDataSourceDevice = 2`).
- Remote DCOM with credentials → `LogonUser` (`LOGON32_LOGON_NEW_CREDENTIALS`) + `WindowsIdentity.RunImpersonated` wrapping `ConnectRemote`; **without credentials the remote server is activated directly with the process identity (null `COAUTHINFO` → default credentials)** — a DCOM source with a host but no `RemoteUsername` is valid (was Win32 error 87 before). See `WindowsImpersonation.cs`.
- **Per-item failures are isolated** (since `d23ee4e`/`5d86771`): an item that fails `AddItems` (deleted tag, access denied) or a poll-fallback read is mirrored as a BAD `BridgeValue` and retried every poll cycle (`AddMissingItems`) — one bad tag can no longer abort a whole group and trigger a reconnect storm. Teardown COM calls (`RemoveItems`/`RemoveGroup`) are exception-safe so a dead server cannot crash the coordinator mid-drain and strand the source in Faulted forever.
- `OpcDaClient.Warning` event surfaces non-fatal operational warnings — subscription setup failure → polling fallback, per-item AddItems failures, item recovery; `BridgeWorker` subscribes and logs them, so operators see why a source is polling instead of receiving callbacks.
- `DaConnectErrorClassifier` (`OpcBridge.Da`, internal, unit-tested) classifies server-activation failures for the coordinator: registered-but-dead servers (RPC unavailable, crash on start) and unreachable hosts throw `SourceConnectionLostException` so the coordinator retries with backoff; local config errors (class not registered `0x80040154`, logon failure) stay terminal Faulted.
- All COM-touching methods are `[SupportedOSPlatform("windows")]`; non-Windows calls throw `PlatformNotSupportedException`. `OperatingSystem.IsWindows()` guards in `Program.cs` keep browse/enumerate endpoints from invoking COM on Linux.

## UA server

- SDK: `OPCFoundation.NetStandard.Opc.Ua` 1.5.378.145.
- Namespace URI `urn:ohmypi:opc-bridge:tags` (index 2 at runtime). Root folder `OpcDaTags` under Objects. Mirror node id: string `{sourceId}/{itemId}` in that namespace (e.g. `ns=2;s=ua-a/ns=2;s=Tag00001`).
- Endpoint `opc.tcp://0.0.0.0:4840/OpcBridge` (runtime port from `Bridge:OpcUaPort`), security policy `None` only, `AutoAcceptUntrustedCertificates = true` (dev default — tighten for production).
- PKI directory stores: `pki/own`, `pki/trusted`, `pki/issuers`, `pki/rejected`.
- `BridgeNodeManager.SyncMappings` (via `BridgeUaServer`) adds/removes nodes AND **refreshes mapping-driven attributes in place** when a mapping changes: `AccessLevel`/`UserAccessLevel` (from `AccessRights` via `ToAccessLevel`), `DataType` (via `ToDataTypeId`; a type change resets the value to a type-consistent initial), `DisplayName`/`Description`, and the `OnWriteValue` handler. Guarded by a change check so steady-state SyncMappings over 100k nodes is a near no-op. `ToAccessLevel`/`ToDataTypeId` are `internal` and unit-tested.
- `UpdateValue` writes value/timestamp/statuscode and clears change masks.
- Writes: `BridgeNodeManager.HandleWriteValue` awaits a `TaskCompletionSource<bool>` resolved by the write-queue consumer; on `false` it rejects with `BadNoCommunication`, on 5s timeout `BadRequestTimeout`. Read-only nodes reject at the server's access-level check (`BadNotWritable`); write-only nodes reject reads (`BadNotReadable`).

## Write queue (per-source routing)

`WriteQueue` (`src/OpcBridge.App/WriteQueue.cs`, internal) keeps **one bounded channel (capacity 1024, DropWrite) per source** and routes requests by `sourceId` at enqueue time. Each source has exactly one consumer task (`ProcessWriteQueueAsync`) reading only its own channel — **there is no cross-source re-enqueue**. Do not reintroduce a shared queue with re-enqueue: with N readers a single request ping-ponged tens of thousands of times before the matching consumer resolved it (measured 31k hops in isolation, 4–50k per write on the rig). Enqueue sites: UA write handler, `ForwardToConsumers` (provider→consumer links, routes to the consumer's source), `ApplyUaWriteAsync`/`TryHmiWriteAsync` (HTTP/HMI writes). Stats (`GetStats`) feed `/api/diagnostics` writeQueue block.

## HTTP API & dashboard

ASP.NET Core minimal API, listens on `http://0.0.0.0:8080` (runtime port from `Bridge:HttpPort`, auto-assigned and persisted to `appsettings.json`). Dashboard is a single HTML page (`DashboardPage.FullHtml`) served as explicit UTF-8 bytes at `/`.

Endpoints (all in `Program.cs`):
- `GET /` — dashboard HTML
- `GET /api/values` — current `BridgeState` values
- `GET /api/dashboard?limit=&sourceId=` — Live Values payload: `values[]` with `{sourceId, itemId, value, timestampUtc, daQuality, isGood, dataType, updateRate}` (dataType + updateRate resolved via `DashboardValues`; `updateRate` = effective ms per tag — per-tag `PollRateMs` wins, else the source default), `valuesTotal`, plus bridge/UA status blocks
- `GET /api/status` | `/api/diagnostics` — bridge + UA status; diagnostics includes writeQueue stats, uaBandwidth, UA sessions/subscriptions
- `GET /api/hmi/tags` — HMI tag snapshot (mappings + current values)
- `POST /api/hmi/write` — HMI write; gated on mapping access rights; reuses `WriteQueue` / `ApplyUaWriteAsync`
- `GET /api/hmi/trends?sourceId=&daItemId=&from=&to=&maxPoints=` — history via bridge Influx proxy (HMI never holds Influx token). Soft-fails with empty points + `error` when Influx unavailable.
- SignalR hub `/hmi` — events `values` (batched `HmiValueDelta[]`) and `mappingsChanged` (`HmiMappingsChanged`)
- `GET /api/logs?limit=&level=` — `DashboardLogStore` ring buffer
- `GET /api/app-info` | `/api/version` | `/api/help` — assembly info / `HelpContent.Markdown`
- `GET /api/da/sources` — source registry; `POST /api/da/sources` (upsert); `POST /api/da/sources/remove`; `POST /api/da/sources/update-rate`; `POST /api/da/update-rate`
- `POST /api/da/servers` — enumerate OPC DA servers (Windows-only, 10s timeout); `POST /api/da/tags` — browse tags (Windows-only, 15s timeout)
- `POST /api/ua/test-connection` — probe an external UA endpoint from the bridge
- `GET /api/mappings`; `POST /api/mappings/add` | `/bulk-add` | `/update` | `/remove` (see API gotchas above)
- `GET /api/da-links` (and related write endpoints) — provider/consumer links
- MQTT config/status/values endpoints under `/api/mqtt/*`
- Influx config/connect/status endpoints under `/api/influx/*` (opt-in per-tag `InfluxEnabled` logging)
- `GET /health` — `{ "status": "ok" }`

HMI note: the operator UI is not embedded in the dashboard; run `dotnet run --project src/OpcBridge.Hmi` against the bridge base URL. Historical trends are served only through the bridge proxy; the HMI has no Influx config or token.

`DashboardLogStore` is also wired as an `ILoggerProvider` (`DashboardLogProvider`), so `ILogger` calls under the `OpcBridge.*` categories at Information+ appear in the dashboard's Logs panel and docker logs. `OpcUaSourceClient` logs reconcile summaries (`subscription reconcile: desired=… active=… added=… removed=…`) — useful for verifying monitored-item behavior.

### Dashboard UI (DashboardPage.cs)

- Sidebar: Sources (OPC DA / OPC UA / Diagnostics tabs), Drivers, Maps, DA Links, MQTT, Traffic, InfluxDB, Monitor (Live Values / Logs / Diagram / Guide / About).
- Live Values: 7-column table (Source | Item ID | Value | Type | Rate | Quality | Timestamp) fed by `/api/dashboard`; `colgroup` 11/23/20/9/9/10/18%. The **Rate** column shows each tag's effective update rate (`updateRate` per value — per-tag `PollRateMs` override, else the source default; `formatMs` renders `—` for unknown).
- Maps tab defaults to the **opc-da** subtab — on UA-only rigs click `[data-map-type="opc-ua"]` to see rows.
- Mapping rows: value + badge cluster (type, deadband, rate, MQTT, Influx) clipped with a right-edge mask fade; the **status cluster (connection-state + access rights) is pinned right outside the fade** (`flex-shrink:0`) and never clipped; row height fixed 34px. Connection badges (since `fix/ui-disconnect-badges`, main `cc97b52`) are driven by **server-side signals in the `/api/dashboard` payload — never by absence from the capped 2000-value window** (that bug showed Disc on ~98% of rows after reload): `disconnected` = failed monitored items (auto-retrying) → pinned **Disc** badge; `badQuality` = full-store scan of `IsGood=false` values → pinned **Bad** badge; source `connectionState != Connected` → **Disc** on all its rows. `refresh()` re-renders Maps rows while the Maps tab is visible so badges track live state. Disabled mappings excluded.
- Faceplate: big value + meta row (type pill, quality, timestamp); no "Real value" label.
- The browser caches the dashboard page — after a container rebuild, force-reload with `?force=<timestamp>` or the old script is served to the DOM.

### Diagram tab

Topology views under **Diagram** (SVG canvas, live status colors):

| Sub-tab | Default rendering | Scale strategy |
|---|---|---|
| **All** | Aggregated plant overview: one row per source → tag-group box → UA + MQTT hubs | O(sources) nodes/trunks, not O(tags) |
| **DA→UA** | Aggregated source trunks by default; click a tag-group to expand | Expanded detail is paged (`DIAG_EXPAND_PAGE = 80` tags/page) |
| **DA-to-DA** | Aggregated source-pair trunks (provider source → consumer source); click count badge to expand | Expanded pair endpoints paged (`DIAG_EXPAND_PAGE = 80`); expand keys `dada:{from}=>{to}` |
| **MQTT** | Aggregated per-source MQTT groups → broker; click group to expand | Expanded tags paged (`DIAG_EXPAND_PAGE = 80`); expand keys `mqtt:{sourceId}` |

**Zoom / pan (all sub-tabs):** toolbar `−` / `%` / `+` / **Fit** / **Fit W** / **Reset**; range 25%–300%, step 10%; **Ctrl+wheel** zooms toward cursor; drag empty canvas to pan; zoom persists across live re-render and sub-tab switch.

**Status colors:** grey = inactive/default topology; green/yellow/red only when live/active. Animated dashed edges indicate flow.

**Tag Browser Mapped badge:** `loadMappings()` calls `refreshTagBrowserMappedBadges()` so Browse All Tags shows **Mapped** immediately after Add/Remove without a re-browse.

## Configuration (`appsettings.json`)

- `Da:ProgId`, `Da:Host`, `Da:UpdateRateMs` — single-source seed (becomes the `default` source on first run). Multi-source config is via the API at runtime, persisted in `mappings.json` + the in-memory `DaRuntimeSettings`.
- `Ua:ApplicationName`, `Ua:EndpointUrl`, `Ua:AutoAcceptUntrustedCertificates`.
- `Bridge:HttpPort` (auto-assigned + persisted), `Bridge:OpcUaPort`, `Bridge:RateLimits` (rate→max-tags map), `Bridge:ExpectedTagCount`, `Bridge:Mappings` (seed mappings, used only if `mappings.json` is absent).

Runtime state files live beside the running app in `AppContext.BaseDirectory` (`mappings.json`). Preserve these during deploy cutover or live bridge state is lost.

## Build & tests

**All dotnet builds/tests run in Docker (no local SDK):**
```bash
docker run --rm -v "$PWD/<worktree>":/src -w /src -v "$HOME/.nuget-cache":/home/build \
  -e HOME=/home/build -e DOTNET_CLI_HOME=/home/build --user "$(id -u):$(id -g)" \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj
```
`--user` keeps build artifacts iwan-owned (rootful daemon). Full suite: **358 tests** (xUnit, `tests/OpcBridge.LoadTest`), ~2 min in Docker. Since the last refresh, `DaConnectErrorClassifierTests` (5) and `AccessRightsNormalizationTests` (6) joined the suite. Known flaky: `InfluxApiTests.InfluxConfig_Post_Persists_EnabledFlag` (fails in full-suite order, passes isolated — pre-existing).

**Worktree workflow (session convention):** fixes live in `git worktree add .worktrees/<branch-slug> -b <branch> main`; one worktree per branch; full suite per branch before merge; merge to main with `--no-ff`; push to origin. **Tool path quirk:** `edit`/`write` with relative `.worktrees/...` paths sometimes land in the main checkout — always use absolute paths and verify with grep after. `.dockerignore` excludes `.worktrees/` (7+ GB) so images can be built from the main checkout.

**Windows host build:** `"%USERPROFILE%\AppData\Local\Microsoft\dotnet\dotnet.exe" build OpcBridge.sln` — the `C:\Program Files\dotnet` install lacks the ASP.NET shared framework. Stop the running app before building (it locks `OpcBridge.Ua.dll`).

## Load-test rig & harness

- **Harness** (committed, branch `test/test-load-opcua`, also pushed): `tests/loadtest/` — `OpcUaSimServer/` (net8.0 UA sim: N Double tags `Tag00001..Tag{N:00000}`, sine updates; env `SIM_NODES`, `SIM_UPDATE_MS`, `SIM_PORT`, **`SIM_WRITEABLE`** — first N nodes accept UA writes and then **freeze** so a written value persists and can be read back through a bridge; **`SIM_BAD_TAGS`** + `SIM_BAD_AFTER_MS` — fault-inject tags to `BadOutOfService` (frozen) for good→bad transition tests; **`SIM_EXTRA_TAGS`** + `SIM_EXTRA_AFTER_MS` — add tags to the address space at runtime, simulating a tag appearing later at the source), `opcuasim.Dockerfile`, `run-loadtest.sh`, `provision-type-rig.sh` (this one also on main: 3 sources + 100k bulk mappings + demo tags via the update endpoint), `rss-trend.sh/.py`.
- **Rig (current):** container `opcbridge-fix` (image `opcbridge:type8` = main `039f104`), HTTP `18082→8080`, UA `4842→4840`; env `Bridge__ExpectedTagCount=150000` + rate limits 150000. Sims: `opcua-sim-20k` (50k, 49321, currently with `SIM_WRITEABLE=10 SIM_BAD_TAGS=12 SIM_EXTRA_TAGS=99999 SIM_EXTRA_AFTER_MS=240000`), `opcua-sim-b` (30k, 49322), `opcua-sim-c` (20k, 49323), all `opcuasim:loadtest`; endpoints `opc.tcp://172.17.0.1:<port>/opcuasim/`. UA probe: freeopcua python client inside the `opcua-sim` container (probe script pattern in `/tmp/ua-probe.py` — construct NodeIds as `ua.NodeId("ua-a/ns=2;s=Tag00001", 2)`; freeopcua 0.98 mis-parses NodeId strings containing `;s=`).
- **Demo tags (ua-a):** Tag00001 Int32 manual 7, Tag00002 Boolean source, Tag00003 String manual, Tag00004 Auto manual 42, Tag00005 Double + all badges (Read), **Tag00006 Read-Write (123.45), Tag00007 Write (77.5), Tag00008 Read** — access-rights demo; **AAAZZZ nonexistent → Disc badge; Tag00012 BadOutOfService → Bad badge; Tag99999 appears at the source later → auto-reconnected by the retry timer** — disconnect-handling demo.
- Fresh container FS each recreate → full reprovision required (script exists; ~2–3.5 min). Reconnect after sim restarts is automatic (watchdog, <4s).

## Deploy to Windows

**Deploy targets:**
- **Windows host** (separate PC): `C:\Users\xlibr\Documents\OpcBridge\`
- **Windows VM `DESKTOP-BC2AU7H`** (`192.168.48.129`, VMnet subnet; also runs `Matrikon.OPC.Simulation.1` as DA source `opc-vm`): `C:\Users\Tested1\Documents\OpcBridge\` — renamed from `Documents\OpcDaToUaBridge` on 2026-08-24; scheduled task **`OpcBridge`** (renamed from `OpcDaToUaBridge` 2026-08-24; Boot+Logon triggers, InteractiveToken Hidden) runs `publish\OpcBridge.App.exe`; host deploy script `winvm-deploy.ps1` lives in that folder. SSH: use alias **`winvm-direct`** (`Tested1@192.168.48.129`) — the `winvm` alias's ProxyCommand jump host is currently broken.
- Old target `DESKTOP-MENOJUS` / SSH alias `xlibr-win` (`192.168.20.13`) is retired (unreachable).

**Known pending:** one other machine (different location) is still configured against retired MENOJUS — leave as-is for now; reconfigure later.

**Linux publish (self-contained, 32-bit COM):**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -lc 'dotnet publish src/OpcBridge.App/OpcBridge.App.csproj -c Release -r win-x86 --self-contained true -o /src/publish.tmp'
```
Package `publish.tmp` → tar.gz, SCP to host as `publish-new.tar.gz`, then run host deploy script (backs up `appsettings.json` / `mappings.json` / `pki`, clears publish, extracts, restores runtime state, re-registers task).

**Host launcher:** scheduled task `OpcBridge` → `scripts/windows/start-published-bridge.cmd` which `cd`s into `publish\` and runs `OpcBridge.App.exe` (self-contained apphost — carries its own runtime; falls back to `dotnet OpcBridge.App.dll`. CWD must be the publish folder so `appsettings.json` resolves).

**register-published-task.ps1** kills old process, re-registers AtStartup S4U task, starts it, probes `http://127.0.0.1:8080/health`.

**Deploy guards:**
- Restore host-specific `appsettings.json` (do not ship a broken `EndpointUrl` from the build machine).
- Preserve `mappings.json` and `pki/` across cutover.
- Do **not** copy test platform DLLs (`Microsoft.TestPlatform.*`, `Mono.Cecil.*`, xunit, etc.) into publish.
- Delete stale apphost / pollution before copy if the directory was previously dirtied.
- Optional: delete `publish/pki/own/cert.der` when UA hostname/SAN must regenerate.

**Git remotes:** single remote — `origin` = `OpcBridge-linux` (GitHub). The old `win` (`OpcBridge-windows`) remote is retired; push everything to `origin` only.

## Conventions

- **Zero-warning build bar.** Cross-platform analyzer warnings (CA1416) are fixed by routing Windows-only calls through `[SupportedOSPlatform("windows")]` helper methods — a runtime `OperatingSystem.IsWindows()` guard alone does not discharge the warning.
- **Direct COM over vendor SDKs.** OPC DA interop is hand-declared `[ComImport]` interfaces, not a commercial wrapper.
- **Interface seams over monoliths.** `ISourceClient` is the source seam; `BridgeValue`/`TagMapping` are the cross-project boundary types. Don't pass raw COM types across project boundaries.
- **Failure-resilient by default.** Errors are surfaced in the dashboard (per-source state, `LastError`), not fatal. A failed connect must leave the app alive and recoverable.
- **Backend-first.** Verify the backend seam (`ISourceClient`, `BridgeWorker`, `BridgeState`) before wiring dashboard controls.
- **Conventional commits** (`feat:`, `fix:`, etc.). Committed code on `main` is authoritative; uncommitted changes are a known risk.
- **Tests exist** under `tests/OpcBridge.LoadTest` (xUnit, 358 tests). Prefer `InternalsVisibleTo` over making types public for tests. Run in Docker (command above). Primary verification is the full suite + rig/browser proof after deploy.

## Gotchas

- The mental-model notes about `Da:Mode` / `SimulatedDaClient` / `POST /api/da/mode` / "mode switching" describe a pattern **not present** in committed code. Trust committed code, not those notes.
- `appsettings.json` is `reloadOnChange: true`, but `DaRuntimeSettings` is a singleton seeded once at startup; runtime source changes go through the API, not by editing the file.
- `MappingStore` loads from `mappings.json` if it exists, ignoring `Bridge:Mappings` in `appsettings.json`. Delete `mappings.json` to reseed from config.
- **Mapping API semantics:** `/add` + `/bulk-add` are insert-only (silently skip existing keys); `/update` replaces with defaults for omitted fields (send all fields); removal is `POST /api/mappings/remove`; source removal is `POST /api/da/sources/remove` (the `DELETE` route does nothing). Access-rights fields are normalized case-/hyphen-insensitively (`writeable: true` alone → `Read-Write`; explicit `accessRights` wins) — see API gotchas above.
- **Runtime mapping updates DO refresh UA node attributes** (since `fix/ua-node-sync`, main `2746864`): AccessLevel, DataType, display metadata update in place on the next SyncMappings cycle — no remove+re-add needed. Node identity (NodeId/BrowseName) is stable.
- **Write-only tags are not source-read.** They hold a write-only mirror node (reads rejected `BadNotReadable`); the dashboard shows the last value read before the flip, then freezes.
- `OpcDaClient.DisposeAsync` releases COM objects only on Windows; on Linux it nulls references (the client never connected there).
- 32-bit COM alignment: if the Windows runtime uses 32-bit OPC DA servers, publish with `-r win-x86`; a 64-bit process cannot activate them without DCOM surrogate setup.
- **Deadband under subscriptions only.** `TagMapping.DeadbandPct` is applied as the OPC DA group's `percentDeadband` and only filters at the source via `IOPCDataCallback` callbacks. If a source falls back to polling, deadband has no effect — do not add client-side filtering.
- **Subscriptions are opt-in via `Da:UseSubscriptions`** (default `true`). If `IConnectionPointContainer.FindConnectionPoint` for `IOPCDataCallback` fails, `OpcDaClient` falls back to device reads and `OnCallbackValues` never fires — but it now raises the `Warning` event (logged by `BridgeWorker`), so the fallback is no longer silent.
- **Write queue:** per-source channels, no re-enqueue (since `fix/write-queue-leak`). A shared-queue re-enqueue design must not return — it starves the matching consumer (tens of thousands of hops per write).
- **Reconcile serialization:** `OpcUaSourceClient.ReconcileMonitoredItemsAsync` is serialized via a `SemaphoreSlim` (since `fix/write-tag-subscription`). Concurrent reconciles previously left stale monitored items on Write-flipped tags.
- **UA client logging:** `SourceClientFactory` must pass a real `ILoggerFactory` — the UA client's reconcile logs are the primary tool for verifying monitored-item behavior.
- freeopcua 0.98 (python) mis-parses NodeId strings containing `;s=` (e.g. `ns=2;s=ua-a/ns=2;s=Tag00001` → identifier `Tag00001`) — construct `ua.NodeId(identifier, ns)` directly in probes.
