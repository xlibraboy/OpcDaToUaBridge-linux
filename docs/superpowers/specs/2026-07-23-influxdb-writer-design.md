# InfluxDB Historical Writer Design — OpcDaToUaBridge

- **Date:** 2026-07-23
- **Branch:** `feature/influxdb-access` (git worktree at `.worktrees/feature-influxdb-access`, forked from `main` @ `22908c1`)
- **Status:** Approved design, pending implementation plan

## 1. Goal

Add a continuous **Database Writer** that logs opt-in mapped tag values into an **InfluxDB 2.x / 3.x-compatible** server so desktop SCADA and Android clients can query historical time-series data (Flux / SQL depending on server).

This is **option 2** from the factory architecture diagram: DA → UA bridge already exists; this feature adds the InfluxDB log path only. No Flux query API and no client apps in this slice.

## 2. Decisions (from brainstorming)

| Topic | Decision |
|---|---|
| Tag scope | Opt-in per tag: `InfluxEnabled` on `TagMapping` (mirror `MqttEnabled`) |
| Server API | InfluxDB 2.x / 3.x Cloud-compatible: URL + Org + Bucket + Token |
| Write trigger | On every `BridgeState.ValueUpdated` for enabled tags |
| Architecture | Mirror MQTT: new `OpcBridge.Influx` project + channel drain |
| UI scope | Full MQTT-style: Connection panel + per-tag toggle + status counters |
| Measurement | Single configurable measurement (default `opc_tags`); no per-tag override in v1 |
| Failure model | Influx outage never blocks DA poll, UA, or MQTT |

## 3. Architecture

New project `OpcBridge.Influx` (`net8.0`). Reference graph:

```
App → {Core, Da, Ua, Mqtt, Influx}
Influx → Core          (Influx depends only on Core, never on Da/Ua/Mqtt)
Da → Core
Ua → Core
Mqtt → Core
```

`OpcBridge.App` remains the composition root: `BridgeWorker` wires `IInfluxWriter` into the existing value pipeline; `Program.cs` adds Influx REST API; `DashboardPage` adds Connection UI + faceplate toggle.

```
DA poll / subscription / Manual
        │
        ▼
BridgeState.SetValue ──ValueUpdated──► MQTT channel (existing)
                    └────────────────► Influx channel (new)
                                              │
                                              ▼
                                    IInfluxWriter.WritePointAsync
                                              │
                                              ▼
                                         InfluxDB server
```

## 4. Components

### 4.1 `OpcBridge.Core`

- **`InfluxOptions`** (new):
  - `Enabled` (bool, default false)
  - `Url` (string, default `http://localhost:8086`)
  - `Org` (string, default empty)
  - `Bucket` (string, default empty)
  - `Token` (string?, default null) — never logged
  - `Measurement` (string, default `opc_tags`)
  - `TimeoutMs` (int, default 5000)
  - `VerifySsl` (bool, default true)
- **`TagMapping`**: add `InfluxEnabled` (bool, default false). No measurement override in v1.
- No change to `BridgeValue` — it already carries `SourceId`, `DaItemId`, `Value`, `TimestampUtc`, `DaQuality`, `IsGood`.

### 4.2 `OpcBridge.Influx`

- **`IInfluxWriter`** seam:
  - `Task ConnectAsync(InfluxOptions options, CancellationToken ct)`
  - `Task DisconnectAsync(CancellationToken ct)`
  - `Task WritePointAsync(BridgeValue value, string? displayName, CancellationToken ct)`
  - `InfluxConnectionState State` (`Disconnected | Connecting | Connected | Faulted`)
  - `event Action<InfluxConnectionState>? StateChanged`
  - `IAsyncDisposable`
- **`InfluxWriter : IInfluxWriter`**: wraps official **`InfluxDB.Client`** write API (line protocol / WriteApiAsync).
  - Connect builds client from Url/Token/Org/Bucket/Timeout/VerifySsl.
  - `WritePointAsync` builds one point:
    - **measurement:** `options.Measurement`
    - **tags:** `source_id`, `da_item_id`, and `display_name` when non-empty
    - **fields:** `value` (typed bool / long / double / string from runtime type), `quality` (int), `is_good` (bool)
    - **timestamp:** `BridgeValue.TimestampUtc` (UTC; client nanosecond precision)
  - Disconnect / Dispose flushes and disposes the client cleanly.
- Connection failures set `Faulted` and raise `StateChanged`; they do not throw into pollers.

### 4.3 `OpcBridge.App`

- **`InfluxRuntimeSettings`** (singleton, mirror `MqttRuntimeSettings`):
  - Seeded from `appsettings` `Influx:` then overridden by `influx.json` in `AppContext.BaseDirectory` when present.
  - Holds options, connection state, last error, written count, written rate (1s window).
  - `GetOptions`, `GetSnapshot`, `UpsertOptions` (persist), `SetState`, `ResetCounters`, `IncrementWritten`.
  - Token is stored in `influx.json` like MQTT password in `mqtt.json` (local deploy artifact; preserve on cutover).
- **`BridgeWorker` integration:**
  - Fields: `IInfluxWriter`, `InfluxRuntimeSettings`, bounded `Channel<BridgeValue>` (1024, `DropOldest`, single reader), `HashSet` of enabled keys `sourceId::daItemId`.
  - On startup: build `influx_enabled_keys_` from active mappings with `InfluxEnabled`; subscribe once to `BridgeState.ValueUpdated`; start `InfluxWriteDrainAsync`; if `Enabled`, call `ConnectAsync`.
  - On mapping change: rebuild `influx_enabled_keys_`.
  - `OnBridgeValueUpdated` (or a sibling handler): if key in set, non-blocking write to channel.
  - Drain task: for each value, if options.Enabled and writer state Connected, resolve display name, `WritePointAsync`, `IncrementWritten`; on failure log warning and continue.
  - When disabled or disconnected: drain still runs but skips write (or drops) so the channel does not block forever.
  - Shutdown: cancel token ends drain; `DisconnectAsync` / dispose writer.
- **API (`Program.cs`):**
  - `GET /api/influx/config` — current options (Token returned as stored for edit form parity with MQTT password; never put Token in logs).
  - `POST /api/influx/config` — upsert + persist `influx.json`.
  - `POST /api/influx/connect` / `POST /api/influx/disconnect`.
  - `GET /api/influx/status` — `state`, `lastError`, `writtenCount`, `writtenRate`, `enabled`.
  - Mapping add/update/bulk-add accept and return `influxEnabled` (default false).
- **Dashboard (`DashboardPage`):**
  - **Connection tab:** InfluxDB panel — Enabled, URL, Org, Bucket, Token, Measurement, Connect/Disconnect, status + written count/rate (same visual language as MQTT panel).
  - **Tag faceplate:** `InfluxEnabled` checkbox alongside MQTT toggle; persisted via existing mapping update API.
  - Optional compact influx line on Monitor status is **not required** for v1.
- **`HelpContent`:** short section: purpose, config fields, point schema, opt-in flag, failure isolation.
- **`appsettings.json`:** add `Influx:` section with safe defaults (`Enabled: false`, local URL, empty org/bucket/token, measurement `opc_tags`).
- **Solution / csproj:** add project + App reference + package `InfluxDB.Client`.
- **Deploy:** preserve `influx.json` on publish cutover the same way as `mappings.json` / `mqtt.json` (document in Help; update deploy script only if it already enumerates preserve files).

## 5. Data flow

**Write path (every value update for opt-in tags):**
```
DA/Manual → BridgeState.SetValue → ValueUpdated
                                      │
                                      ├─ MQTT (if MqttEnabled)
                                      └─ Influx channel (if InfluxEnabled)
                                              │
                                              ▼
                                    InfluxWriter.WritePointAsync
                                      measurement=opc_tags (configurable)
                                      tags={source_id, da_item_id, display_name?}
                                      fields={value, quality, is_good}
                                      time=TimestampUtc
                                              │
                                              ▼
                                         InfluxDB bucket
```

**Config path:**
```
appsettings Influx: → InfluxRuntimeSettings (seed)
influx.json → overrides at load
POST /api/influx/config → UpsertOptions → influx.json
POST /api/influx/connect → IInfluxWriter.ConnectAsync
```

## 6. Error handling

- **Connect failure:** state `Faulted`, `lastError` set, dashboard shows error; bridge keeps Running.
- **Write failure:** log warning with source/item; do not retry the same point in a tight loop; continue drain. Channel `DropOldest` absorbs backlog under load.
- **Missing Org/Bucket/Token on connect:** fail connect with clear error message; do not throw unhandled.
- **Disabled:** no connect on startup; drain skips writes.
- **Disconnect / shutdown:** dispose client; counters retained until next successful connect (MQTT resets on connect — match that: `ResetCounters` on Connected).
- **SSL:** `VerifySsl=false` allows self-signed lab servers (dev only).
- **Secrets:** never write Token into application logs or Help samples with real secrets.

## 7. Build & conventions

- Docker-only .NET SDK builds; gate: 0 warnings, 0 errors + `node --check` on dashboard JS if touched.
- Conventional commits: `feat(influx): ...`.
- `InternalsVisibleTo("OpcBridge.LoadTest")` on new project if tests need internals.
- Persist file `influx.json` in BaseDirectory; restore on deploy cutover like other runtime state.
- Win-x86 publish remains framework-dependent; Influx client is managed .NET (no COM).

## 8. Verification

- `dotnet build` (Docker SDK 8.0) 0w/0e on solution.
- `dotnet test` — existing 35 tests still pass; new tests for:
  - `InfluxOptions` defaults
  - Mapping JSON round-trip preserves `InfluxEnabled`
  - Only `InfluxEnabled` keys enqueue (mock `IInfluxWriter` / channel behavior)
  - Point field typing (bool/long/double/string) via writer unit or pure builder helper
  - API smoke: GET/POST config, GET status (TestAppHandle pattern)
- Manual (Windows host with Influx 2.x):
  - Enable + connect → status Connected
  - Enable Influx on one mapping → points appear in bucket under measurement `opc_tags`
  - Stop Influx server → bridge stays up; status Faulted / write warnings; restart recovers on reconnect
  - Disconnect → status Disconnected; no further points

## 9. Out of scope (YAGNI)

- Flux / SQL query REST endpoints for clients
- Dashboard historical charts
- InfluxDB 1.x write API
- Batching / flush-timer writer
- Per-tag measurement or field-name overrides
- Client certificate auth
- Multi-bucket or multi-org simultaneous connections
- Deadband / on-change-only filtering (every update is intentional for v1)

## 10. Acceptance criteria

1. With `Influx.Enabled=true` and valid URL/Org/Bucket/Token, Connect succeeds and status reports Connected.
2. Only tags with `InfluxEnabled=true` produce writes; disabled tags never write.
3. Each value update for an enabled tag produces one point with correct tags/fields/timestamp.
4. Influx outage does not stop DA polling, UA serving, or MQTT.
5. Config persists across process restart via `influx.json`.
6. Dashboard Connection panel can configure, connect, disconnect, and show written rate.
7. Faceplate can toggle `InfluxEnabled` and mapping API round-trips the flag.
8. Build 0w/0e; full test suite green (baseline 35 + new tests).
