# Avalonia SCADA HMI — Process Displays, Popup Faceplate/Trend, Multi-Bridge Design

**Branch:** `feature/avolonia-hmi-polish`  
**Date:** 2026-07-25  
**Status:** Design approved for implementation planning  
**Stack guidance:** Avalonia 11 + `ui-ux-pro-max --stack avalonia` (compiled bindings, MVVM, Fluent theme, UI-thread marshaling)

## Goal

Evolve the existing operator Avalonia HMI from a single-bridge tag list + inline faceplate into a multi-client SCADA surface:

1. **Process displays** rendered from JSON page documents (shared widget model).
2. **Popup faceplate** per tag (not an inline side panel).
3. **Popup trend** opened from each faceplate.
4. **Standalone Design/Builder** app for authoring displays.
5. **Central display store** on a primary bridge so all operator PCs load the same pages.
6. **Multi-bridge** live data: one HMI talks to multiple `OpcBridge.App` instances.

Configuration of DA sources, mappings, MQTT, and Influx remains in the web dashboard. The desktop apps stay free of OPC DA COM / UA SDK dependencies.

## Problem

Today (`OpcBridge.Hmi` v1 + trends):

- Single base URL, single SignalR hub.
- Main UI is a tag list + **inline** faceplate panel.
- No process graphic / display document model.
- No multi-operator shared page store.
- No engineering Design app separate from Runtime.
- Live values already work well via REST snapshot + SignalR deltas, but broadcast flush is a hard-coded 100 ms.

Factory use needs multiple bridges (lines/areas), many operator clients, engineer-authored pages, and SCADA-style popups—without turning every operator install into an editor.

## Decisions (locked)

| Topic | Choice |
|---|---|
| Architecture | Shared widget model + JSON `DisplayDocument` (Approach 1) |
| Operator shell | Hybrid: process display primary; tag browser/tools available |
| Design/Builder | **Standalone** `OpcBridge.Hmi.Designer` (not Design mode on every client) |
| Display storage | **Central store on primary bridge** (`displayStoreUrl`) |
| Store hosting | Every `OpcBridge.App` build includes display APIs; ops point clients at one primary |
| Multi-bridge | Local config maps `bridgeId` → `baseUrl`; bindings store `bridgeId` |
| Faceplate | Avalonia `Window` popup; one per `(bridgeId, sourceId, daItemId)` |
| Trend | Avalonia `Window` popup; opened from faceplate; uses existing `/api/hmi/trends` on **that tag’s bridge** |
| Widgets v1 | `label`, `numeric`, `qualityLamp`, `boolIndicator`, `pushButton` |
| Live refresh | Event-driven + coalesce; **`Hmi:BroadcastFlushMs` configurable 50–1000, default 100** |
| Auth | LAN trust (same as current HMI API); document follow-up |
| Concurrency | Display document optimistic `version`; 409 on conflict; no multi-user locks |

## Non-goals (v1)

- Freehand P&ID / SVG import / Skia free-draw canvas as the core model.
- Design mode embedded in every operator Runtime.
- Automatic primary failover / clustered display store.
- Auth tokens on display or HMI APIs.
- Offline Designer file drafts / auto-merge of conflicts.
- Alarm summary, recipes, multi-monitor kiosk packaging.
- Rich widgets (gauge, tank, sparkline tile)—post-v1.
- Sub-50 ms / motion-control style graphics rates.

## Existing system (baseline)

| Piece | Role |
|---|---|
| `OpcBridge.App` | Kestrel :8080, DA→UA bridge, dashboard, `BridgeState`, `WriteQueue` |
| `GET /api/hmi/tags`, `POST /api/hmi/write` | HMI snapshot + gated write |
| `GET /api/hmi/trends` | Influx Flux proxy (HTTP 200 + empty/`error` when unavailable) |
| SignalR `/hmi` | `values` deltas + `mappingsChanged` |
| `HmiBroadcastService` | Coalesces `BridgeState.ValueUpdated` into SignalR batches (**today: 100 ms timer**) |
| `OpcBridge.Client` | Shared DTOs + client types |
| `OpcBridge.Hmi` | Avalonia 11 Runtime: connect, tag list, inline faceplate, sparkline, write |
| DA `UpdateRateMs` | Per-source poll/subscription rate; floor for value freshness |

HMI must continue to depend only on HTTP/SignalR + `OpcBridge.Client` (and new shared HMI model lib)—never DA/UA/COM.

---

## §1 Architecture

### Topology

```
┌────────────── primary bridge (display store + live) ──────────────┐
│  OpcBridge.App A :8080                                              │
│  tags / write / trends / SignalR                                    │
│  + GET/PUT/DELETE /api/hmi/displays*                                │
│  + displays/{id}.json on disk                                       │
└─────────────────────────────▲───────────────────────────────────────┘
                              │ pages
┌────────────── peer bridge (live only) ──────────────────────────────┐
│  OpcBridge.App B :8080 — tags / write / trends / SignalR            │
└─────────────────────────────▲───────────────────────────────────────┘
                              │ live data
              ┌───────────────┴────────────────┐
              │                                │
     OpcBridge.Hmi (Runtime)          OpcBridge.Hmi.Designer
     many operator PCs                engineering station(s)
     no Save UI                       palette + PUT/DELETE
```

| Role | Process | Responsibilities |
|---|---|---|
| Runtime | `OpcBridge.Hmi` | Connect N bridges; load pages from primary; render display; popup faceplate/trend; writes |
| Designer | `OpcBridge.Hmi.Designer` | Author pages; Save to primary; tag picker needs bridge connection(s) |
| Primary | One `OpcBridge.App` | Owns display JSON; also a normal live bridge |
| Peer | Other `OpcBridge.App` | Live data only (still ships display API so any host can be designated primary) |

### Solution layout

```
src/OpcBridge.Client/           # wire DTOs including displays API
src/OpcBridge.Hmi.Core/         # DisplayDocument models, binding keys, pure logic (no Avalonia)
src/OpcBridge.Hmi/              # Runtime shell, DisplaySurface, Faceplate, Trend, widgets views
src/OpcBridge.Hmi.Designer/     # Design shell: palette, selection, properties, Save
src/OpcBridge.App/              # DisplayStore service + endpoints; HmiBroadcastFlushMs
```

If project count must stay minimal for the first PR, `Hmi.Core` may start as folders inside Client/Hmi—but **two executables** (Runtime + Designer) are required.

### Client config (local per install)

```json
{
  "displayStoreUrl": "http://192.168.20.10:8080",
  "bridges": [
    { "id": "line1", "baseUrl": "http://192.168.20.10:8080", "enabled": true },
    { "id": "line2", "baseUrl": "http://192.168.20.11:8080", "enabled": true }
  ],
  "startupDisplayId": "plant-overview"
}
```

- `bridgeId` in documents is stable; URLs live only in local config.
- `displayStoreUrl` typically equals the primary bridge base URL.
- Single-bridge sites use one entry in `bridges` (compat with today’s mental model).

### Live data path

```
Bridge A SignalR ──┐
Bridge B SignalR ──┼──► MultiBridgeTagCache
                   │      key = (bridgeId, sourceId, daItemId)
                   └──► Widgets / Faceplate / Trend

Write  → POST {bridge.baseUrl}/api/hmi/write
Trend  → GET  {bridge.baseUrl}/api/hmi/trends?...
Pages  → GET  {displayStoreUrl}/api/hmi/displays/{id}
```

No second live bus. No HMI→Influx direct access.

### Avalonia constraints (enforced)

- Avalonia 11 XAML namespace; **compiled bindings** + `x:DataType` everywhere.
- CommunityToolkit.Mvvm; logic in ViewModels/services, not code-behind.
- Views as `UserControl`; faceplate/trend as real `Window`s.
- `Dispatcher.UIThread` for all hub → UI updates.
- Fluent theme + industrial dark `ThemeVariant`; prefer `DynamicResource` over hard-coded chrome.
- Virtualized tag lists; quality colors via converters/`FuncValueConverter`.
- ViewModels unit-testable without display server; Headless optional later.

---

## §2 Components & interactions

### Runtime shell (`OpcBridge.Hmi`)

- Toolbar: display store + bridges connect status, display picker, connection state.
- Main: **DisplaySurface** (read-only document).
- Optional collapsible **tag browser** (filter across bridges).
- Widget click or tag double-click → **FaceplateWindow**.
- No palette, no drag handles, no Save.

### Designer shell (`OpcBridge.Hmi.Designer`)

- File: New / Open / Save / Save As (Save As = new id).
- Palette: v1 widget types.
- DesignSurface: select, move, resize; optional snap later.
- Properties: bounds, type props, binding picker (`bridgeId` + source/item from connected caches).
- Live preview when bridges connected (same cache as Runtime).
- Save → `PUT` primary with optimistic version.

### Shared surface

| Mode | Input | Chrome |
|---|---|---|
| Runtime | Click → faceplate | None |
| Design | Select / move / resize | Adorners / property panel |

Same widget `UserControl`s; design host toggles hit-testing and adorners via `IsDesignMode` (or equivalent), not a second renderer.

### Widget set (v1)

| type | Binding | Runtime interaction |
|---|---|---|
| `label` | none | — |
| `numeric` | required | Click → faceplate |
| `qualityLamp` | required | Click → faceplate |
| `boolIndicator` | required | Click → faceplate |
| `pushButton` | required (writeable) | Write `props.writeValue` via that bridge; optional confirm |

Unknown `type`: placeholder control; do not fail whole page load.

### Faceplate popup

- `Window` identity: `(bridgeId, sourceId, daItemId)`; re-open focuses existing.
- Shows bridge id, names, type, live value/quality/time.
- Write editor when mapping `writeable`.
- Optional mini sparkline (1h) using existing trends API.
- **Open trend** command → TrendWindow.
- Status/error line for write and load failures.

### Trend popup

- Opened primarily from faceplate (required path).
- Range presets: 1h / 8h / 24h (default 1h).
- Poll/refresh while open (~30 s); dispose timers on close.
- Uses existing trends contract (200 + `points` / `error`).
- History path is independent of live 50–1000 ms value path.

### MultiBridgeTagCache

- One REST + SignalR client per enabled bridge.
- `ApplySnapshot` / `ApplyDeltas` scoped by `bridgeId`.
- Subscribe API for widgets and popups.
- Always marshal notifications to UI thread at the shell boundary.

### Services

| Service | Runtime | Designer |
|---|---|---|
| `DisplayStoreClient` | list + get | list + get + put + delete |
| `BridgeConnectionManager` | N bridges | N bridges |
| `MultiBridgeTagCache` | yes | preview |
| Faceplate / Trend window owner | yes | optional |

---

## §3 Data model, APIs, persistence & versioning

### Identity

| Concept | Key |
|---|---|
| Bridge (config) | `bridgeId` |
| Live tag | `(bridgeId, sourceId, daItemId)` |
| Display | `id` slug `[a-zA-Z0-9_-]+` |
| Widget | `id` unique within document |
| Popup | same as live tag key |

### Display document schema (schemaVersion = 1)

```json
{
  "schemaVersion": 1,
  "id": "plant-overview",
  "name": "Plant Overview",
  "version": 3,
  "updatedUtc": "2026-07-25T12:00:00Z",
  "width": 1920,
  "height": 1080,
  "widgets": [
    {
      "id": "a1b2c3",
      "type": "numeric",
      "x": 40,
      "y": 80,
      "w": 160,
      "h": 48,
      "z": 0,
      "props": { "label": "Tank Level", "format": "0.0", "unit": "%" },
      "binding": {
        "bridgeId": "line1",
        "sourceId": "default",
        "daItemId": "Tank.Level"
      }
    }
  ]
}
```

**Rules**

- `schemaVersion`: format compatibility.
- `version`: monotonic edit counter for optimistic concurrency (not semver).
- `binding` null only for pure decoration (`label`); Runtime shows Unbound otherwise.
- Absolute top-left coordinates in design pixels; v1 Runtime may 1:1 or simple uniform scale.
- Unknown props ignored; unknown types → placeholder.
- No embedded binaries in v1.
- Server does **not** validate `bridgeId` against its own DA sources (peers are client-side).

### Widget props (v1)

| type | props |
|---|---|
| `label` | `text`, `fontSize` |
| `numeric` | `label`, `format`, `unit` |
| `qualityLamp` | `label` |
| `boolIndicator` | `label`, `onText`, `offText` |
| `pushButton` | `text`, `writeValue`, `confirm` |

Prefer typed props per widget type in code; JSON object under `props`.

### Display store API (on every App; used via `displayStoreUrl`)

#### `GET /api/hmi/displays`

```json
{
  "items": [
    {
      "id": "plant-overview",
      "name": "Plant Overview",
      "version": 3,
      "updatedUtc": "2026-07-25T12:00:00Z",
      "widgetCount": 12
    }
  ]
}
```

#### `GET /api/hmi/displays/{id}`

- 200 full document; 404 unknown.

#### `PUT /api/hmi/displays/{id}`

- Body: full document; route `id` is authoritative (mismatch → 400).
- Create: no existing file → store with `version = 1`.
- Update: body `version` must equal current → success bumps to `current + 1`, sets `updatedUtc`.
- Conflict: **409** with `currentVersion` (and optionally current doc).
- 400: bad id, unsupported `schemaVersion`, duplicate widget ids, invalid bounds.
- Id allowlist: `[a-zA-Z0-9_-]+` (path traversal safe).

#### `DELETE /api/hmi/displays/{id}`

- 204 success; 404 missing.

**Persistence**

- Directory configurable; default `displays/{id}.json` beside mappings-style data.
- Atomic write (temp + replace).
- Preserve `displays/` on deploy the same way as `mappings.json` / `pki/`.

### Existing per-bridge HMI APIs (unchanged shapes)

| API | Use |
|---|---|
| `GET /api/hmi/tags` | Snapshot → cache |
| `POST /api/hmi/write` | Faceplate + pushButton |
| `GET /api/hmi/trends` | Trend + sparkline |
| SignalR `/hmi` | Deltas + mappingsChanged |

Bridge selection is by **URL** from local config, not by new body fields.

### Client algorithms

**Runtime load:** config → connect bridges → list displays → GET document → build widgets → resolve bindings → subscribe.

**Designer save:** local validate → PUT with `version` → on 200 replace local doc; on 409 offer reload server copy (no merge).

### Not stored on bridge

Operator window layout, open popups, local bridge URL list, Designer selection/clipboard, trend zoom UI state.

---

## §4 Live refresh rate

### Pipeline

```
DA UpdateRateMs (per source)
  → BridgeState.ValueUpdated
  → HmiBroadcastService pending map (latest per tag)
  → flush timer → SignalR `values`
  → HMI cache → UI bindings
```

### Policy (v1)

| Item | Value |
|---|---|
| Coalesce / broadcast flush | **`Hmi:BroadcastFlushMs`**, clamp **50–1000**, **default 100** |
| Max live HMI update rate | ≈ **1000 / flushMs** Hz per tag (e.g. 10 Hz at 100 ms, 20 Hz at 50 ms) |
| Hard floor | Never fresher than that tag’s **DA `UpdateRateMs`** (and actual DA/COM cycle time) |
| Mechanism | Event-driven; not a fixed canvas FPS loop |
| Trend history | Separate; ~15–30 s poll; not the live value path |

**Operator target:** end-to-end live value latency **≤ 100–200 ms** when DA and flush are configured accordingly.

**Non-goal:** sub-50 ms or 60 fps process animation. OPC DA + multi-client SignalR is a SCADA bus, not motion control.

Implementation note: replace hard-coded `TimeSpan.FromMilliseconds(100)` in `HmiBroadcastService` with configured flush; change timer period on options reload if cheap, else apply on service start.

---

## §5 Error handling

| Condition | Behavior |
|---|---|
| Display store down | Runtime: banner + keep last page; live tags still work. Designer: Save/Open blocked with reason |
| Display 404 | Empty surface + message |
| Version conflict | Designer 409 → reload or cancel |
| Invalid / future schemaVersion | Refuse load with clear message |
| Unknown widget type | Placeholder |
| Unknown `bridgeId` | Unbound / “Bridge not configured” |
| Peer bridge down | That bridge’s widgets bad/stale; others OK |
| SignalR drop | Existing auto-reconnect + REST snapshot refresh per bridge |
| Write / trend errors | Faceplate / Trend status text; no crash |
| Store host empty dir | Empty list (valid new primary) |

Invariant: degrade in place; do not exit the app on bridge/store faults.

---

## §6 Security posture

| Topic | v1 | Follow-up |
|---|---|---|
| Auth on HMI + displays APIs | None (trusted LAN) | Token / basic auth |
| Who can mutate displays | Soft: only Designer binary exposes Save | Server-side auth on PUT/DELETE |
| Concurrent designers | Optimistic version only | Locks / audit metadata |
| Label content | Plain text only | — |
| Display id | Allowlisted charset | — |

Display PUT is plant-wide UI control: treat primary host as sensitive even on LAN.

---

## §7 Testing

**Bridge (Docker `dotnet test`)**

- DisplayStore: create, get, list, version bump, 409, delete, invalid id, bad schemaVersion.
- Broadcast flush respects configured ms (unit/timer test where practical).
- Existing tags/write/trends tests remain green.

**Shared models**

- JSON round-trip; binding key equality; props defaults.

**ViewModels / services (no UI)**

- MultiBridgeTagCache snapshot/delta merge.
- Unbound / unknown bridge handling.
- Faceplate window-key reuse.
- Trend URL + range building.
- Designer save conflict state machine.

**Manual / later Headless**

- Multi-bridge page, kill one bridge.
- Faceplate → Open trend.
- Designer save → second Runtime reload.

**Build gate**

- `dotnet build -c Release` 0 warnings/0 errors in Docker SDK 8.
- New projects in solution; JS check unchanged for web dashboard.

---

## §8 Implementation slices

| Slice | Deliverable |
|---|---|
| **S0** | Display DTOs + App DisplayStore + tests + deploy preserve `displays/` |
| **S1** | Multi-bridge config + `MultiBridgeTagCache` in Runtime |
| **S2** | Popup FaceplateWindow + TrendWindow (from tag browser first) |
| **S3** | Runtime DisplaySurface + v1 widgets (read-only load) |
| **S4** | `OpcBridge.Hmi.Designer` authoring + Save |
| **S5** | Hybrid shell polish, startup display, theme, `Hmi:BroadcastFlushMs` wiring/docs |

**S2 before full graphics** delivers popup faceplate/trend early and reuses them from widgets in S3.

---

## §9 Deployment

| Piece | Notes |
|---|---|
| Bridges | Existing win-x86 publish; preserve `displays/` with mappings/pki |
| Runtime HMI | Operator PCs; local config with store + bridges |
| Designer | Engineering PCs only |
| Primary designation | Ops set `displayStoreUrl`; no code fork between primary/peer builds |

---

## Success criteria

1. Multiple bridges configured; widgets/faceplate/trend target the correct bridge.
2. Display JSON on primary; all Runtime clients load the same page.
3. Designer is a separate app; Runtime cannot Save displays.
4. Faceplate is a popup; **Open trend** opens a popup trend for that tag.
5. Optimistic `version` yields 409 on conflict (no silent overwrite).
6. Live values respect `Hmi:BroadcastFlushMs` (default 100 ms; min 50 ms).
7. Single-bridge config still works (one bridge entry).
8. Solution build/tests green for non-UI parts; HMI projects compile.

## Open follow-ups (not blocking v1)

- Auth on mutating display APIs.
- Primary failover.
- Offline Designer drafts.
- Richer widgets; canvas scale / multi-monitor.
- Alarm banner.
- Trend “live tail” from SignalR (optional).
- Avalonia.Headless CI for critical UI smoke.

## Relationship to prior specs

- Extends `2026-07-23-desktop-hmi-design.md` (operator HMI + API).
- Reuses `2026-07-24-hmi-trends-proxy-design.md` for trend data.
- Does not replace web dashboard config workflows.
- Explicitly supersedes v1 “inline faceplate only” and “single base URL only” for the SCADA path; keep temporary compat while S1–S2 land.
