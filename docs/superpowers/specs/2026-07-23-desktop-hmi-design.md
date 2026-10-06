# Desktop Operator HMI + Web Host Client API Design

## Goal

Add a LAN-facing real-time client surface on the existing bridge Web Host, and a separate Avalonia desktop Operator HMI that shows live mapped tags, faceplates, and gated writes. Configuration stays in the existing web dashboard. Historical Influx trends are deferred to a later phase.

## Problem

The factory architecture diagram expects:

1. A Web Host that broadcasts live tags and accepts write commands for PLC/DA.
2. A Desktop PC SCADA app (Avalonia) on the operator LAN.
3. Later: Android on the same API; Influx for historical trends.

Today the bridge already runs ASP.NET Core on `http://0.0.0.0:8080` with REST polling (`/api/values`, `/api/dashboard`) and a browser dashboard, but:

- There is no WebSocket/SignalR live stream for external clients.
- There is no dedicated HMI write REST endpoint (writes go through the UA write path / internal `WriteQueue`).
- There is no Avalonia (or other) desktop client project.
- Influx write/query is not present on this branch (`main` / `feature/desktop-hmi`).

Building the HMI inside the bridge process would couple a 24/7 factory host to a restartable UI process and block a clean Android path. A fully separate repo would slow shared DTO evolution for a single team.

## Scope

### In scope (v1)

- Upgrade existing `OpcBridge.App` Web Host with:
  - SignalR hub for live tag value deltas.
  - REST snapshot + write endpoints under `/api/hmi/*`.
- New Avalonia desktop project `src/OpcBridge.Hmi/` (separate process).
- Shared client DTOs in new project `src/OpcBridge.Client/`.
- Operator UI: connect dialog, live tag list, faceplate, write when allowed.
- Reuse existing write pipeline (`WriteQueue` → per-source DA write), gated by mapping write rules.
- Keep HTTP port **8080** (no second listener on 5000).

### Out of scope (v1)

- Process mimic / SVG plant graphics.
- Engineering/config UI in Avalonia (mappings, DA browse, MQTT, sources).
- Android client.
- InfluxDB logging and trend charts (explicit **v1.1**).
- Auth / tokens on the HMI API (LAN trust for v1; document as follow-up).
- Merging HMI into the bridge executable.
- Changing DA→UA core engine behavior except exposing write for HMI.

### Deferred (v1.1 — trends)

- Bridge opt-in per-tag write to InfluxDB (org/bucket as configured on host).
- Bridge proxy `GET /api/trends?...` (Flux query; token stays on factory host).
- Faceplate sparkline / history chart in HMI.
- HMI must not talk to Influx directly in the preferred design (token sprawl).

## Existing System

| Piece | Location | Role |
|---|---|---|
| Bridge process | `src/OpcBridge.App` | Kestrel host, dashboard, REST, `BridgeWorker` |
| Live cache | `BridgeState` | In-memory values + status for dashboard/API |
| Mappings | `MappingStore` | `(SourceId, DaItemId)` keys, `Writeable` / `AccessRights` |
| Writes | `WriteQueue` + `BridgeWorker.ApplyUaWriteAsync` | UA client write path into DA |
| HTTP port | `8080` | Dashboard, health, discovery probes |
| UA server | `OpcBridge.Ua` | Mirror for UA clients; not the HMI transport |

Reference graph today: `App → {Core, Da, Ua, Mqtt}`. HMI must not reference Da/Ua/COM.

UA writeability today: `BridgeNodeManager` registers `OnWriteValue` only when `mapping.Writeable` is true. HMI must use the same flag.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Architecture | Approach A: Web Host API + separate Avalonia HMI | Matches diagram; crash isolation; Android-ready API |
| Repo layout | Same solution, new projects | Shared DTOs without monorepo friction of two repos |
| Process split | Two executables | Factory host ≠ operator UI |
| Live transport | SignalR on existing Kestrel | Built-in .NET; better than raw WS boilerplate; REST remains for snapshot/write |
| Port | Keep 8080 | Already used by health/discovery; avoid dual-port confusion |
| Desktop role | Operator HMI only | Config stays web dashboard |
| Main screen | Live tag list + faceplate | Fastest useful SCADA surface; reuses mapping model |
| Trends | After live v1 | Influx not on this branch; don't block shell |
| Write gate | `TagMapping.Writeable == true` (same as UA `OnWriteValue`) | One permission model; `AccessRights` remains for DA-link/forwarding semantics |

## Proposed Design

### 1. Process and solution layout

```
Factory host (Windows)
  OpcBridge.App  (existing)
    DA COM → BridgeWorker → BridgeState + UA
    Kestrel :8080
      REST (existing dashboard + /api/hmi/*)
      SignalR hub /hmi

Operator PC
  OpcBridge.Hmi  (new Avalonia)
    Connect → Tag grid → Faceplate → Write
```

Solution additions:

```
src/OpcBridge.Client/   # wire DTOs shared by App serializers and HMI client
src/OpcBridge.Hmi/      # Avalonia 11, net8.0, Windows primary
```

Rules:

- HMI depends on `OpcBridge.Client` only (plus Avalonia packages). Never `OpcBridge.Da`, `OpcBridge.Ua`, or COM.
- App references `OpcBridge.Client` for shared wire contracts.
- Core remains free of UI and SignalR packages.

### 2. Web Host client API

All on existing base URL `http://0.0.0.0:8080`.

#### `GET /api/hmi/tags`

Returns the operator snapshot of enabled mappings joined with last known values from `BridgeState`.

Per-tag fields (camelCase JSON):

- `sourceId`, `daItemId`, `displayName`, `dataType`
- `value`, `timestampUtc`, `daQuality`, `isGood`
- `writeable` (bool: `mapping.Writeable`; true only when HMI/UA may write)

Used on connect and after every SignalR reconnect. Snapshot is REST-only; the hub does not replace it.

#### `POST /api/hmi/write`

Request:

```json
{ "sourceId": "default", "daItemId": "Random.Int1", "value": 42 }
```

Behavior:

1. Resolve mapping by `(sourceId, daItemId)` (case-insensitive, same as `MappingStore`).
2. Reject if missing, disabled (`Enabled == false`), or `Writeable == false`.
3. Convert `value` using mapping `DataType` (reuse existing conversion helpers where present).
4. Call the same write seam as UA (`BridgeWorker.ApplyUaWriteAsync` / `WriteQueue`); 5s timeout already used by the UA path.
5. Return:

```json
{ "ok": true }
```

or

```json
{ "ok": false, "error": "Tag is read-only" }
```

Do not invent a second DA write pipeline.

#### SignalR hub `/hmi`

- Clients connect to hub path `/hmi`.
- **Snapshot is REST-only:** on connect and after every reconnect the HMI MUST call `GET /api/hmi/tags`.
- Hub publishes **deltas only** via event name `values`, each item: `sourceId`, `daItemId`, `value`, `timestampUtc`, `daQuality`, `isGood`.
- When `MappingStore` version changes, hub publishes `mappingsChanged` with `{ "version": <int> }`; HMI MUST re-fetch `GET /api/hmi/tags`.
- Batch or coalesce rapid updates per tag if needed; the latest value for each tag must still be delivered.
- Source of deltas: `BridgeState` update path (`SetValue` / `UpdateDaRead`).

#### Unchanged

- Existing dashboard HTML/JS and config APIs.
- `/health`, `/api/status`, `/api/dashboard`, discovery on 8080.
- UA endpoint and DA COM behavior.

### 3. Desktop HMI (Avalonia)

Stack: Avalonia 11, .NET 8, separate process, Windows primary for v1.

Screens:

1. **Connect** — base URL field (default `http://<host>:8080`), Connect / Disconnect, connection state indicator.
2. **Tag list** — grid of tags from snapshot + live updates: source, display name / item id, value, quality, timestamp; text filter; row selection.
3. **Faceplate** — selected tag detail: live value, quality, timestamp, data type; write editor + Write button only when `writeable`; last write result/error.

Client modules:

- `BridgeApiClient` — REST `GET /api/hmi/tags`, `POST /api/hmi/write`.
- `HmiHubClient` — SignalR receive → update in-memory tag dictionary → marshal to UI thread.
- View-models for connect, grid, faceplate (testable without full UI where practical).

No mapping editor, no DA browse, no Influx client in v1.

### 4. Data flow

```
DA read → BridgeState ─┬─► GET /api/hmi/tags (snapshot)
                       └─► SignalR /hmi (deltas)

Operator write → POST /api/hmi/write
                 → write gate (mapping.Writeable)
                 → WriteQueue → DA IOPCSyncIO.Write
                 → response ok/error to faceplate
```

### 5. Error handling

| Condition | Behavior |
|---|---|
| Wrong URL / bridge down | No bridge reachable: connect fails; status Disconnected; grid empty. With several bridges, an unreachable one shows Failed with its error and is retried on a backoff while the healthy ones keep running |
| SignalR disconnect | Link state goes Reconnecting at once (15 s client server-timeout, retries 0/1/2/5 s, then every 5 s); status strip, bridge rows and the aggregate follow live. On rejoin the REST snapshot is refetched with retries until it lands, then values are fresh — no Connect needed |
| Write rejected (read-only / unknown tag) | Faceplate shows error; do not clear live value |
| Write DA failure | Faceplate shows error from bridge |
| Mapping removed | Tag disappears on next snapshot / after `mappingsChanged` |
| Bridge stopping | Hub disconnect; the bridge reads Reconnecting/Failed with the error, and a closed hub is rebuilt on the retry schedule |

v1 assumes trusted LAN: no auth. Spec for later: shared token or basic auth on `/api/hmi/*` and hub.

### 6. Testing

- **Bridge (Docker / existing LoadTest project):**
  - Write gate: unmapped, read-only (`Writeable == false`), writable success path (mock DA where possible).
  - Snapshot shape of `/api/hmi/tags`.
  - Prefer automated hub smoke if feasible without Windows COM.
- **HMI:**
  - View-model / client unit tests for snapshot merge and write result handling.
  - Manual UI verification on Windows operator machine against deployed bridge.

### 7. Deployment

- Bridge: existing publish path on factory host; no change to scheduled-task model beyond new assemblies from App.
- HMI: separate `dotnet publish` for win-x64 (or framework-dependent); installed on operator PCs; configured with bridge base URL only.
- Do not copy HMI into the bridge publish folder as the primary layout (optional side-by-side is fine).

## Non-goals recap

- Port 5000 dual listener.
- In-process Avalonia inside `OpcBridge.App`.
- Direct HMI → Influx for v1/v1.1 preferred design (use bridge proxy).
- Replacing the web dashboard.

## Success criteria (v1)

1. From an operator PC, Avalonia HMI connects to `http://<bridge>:8080`.
2. Tag list shows mapped tags with live values updating without full-page REST poll loops (SignalR deltas).
3. Faceplate shows quality/time and allows write only when mapping is writable.
4. Successful write reaches DA through existing `WriteQueue` path; failures surface in the faceplate.
5. Bridge web dashboard and UA/DA engine continue to work unchanged for existing operators.
6. Build remains Docker-based for the solution; HMI project is included in solution build.

## Open follow-ups (not blocking v1)

- Auth on HMI API.
- Influx writer + `/api/trends` + faceplate chart (v1.1).
- Android client reusing `/api/hmi/*` and `/hmi`.
- Multi-monitor / kiosk packaging for Avalonia.
