# HMI Trends via Bridge Influx Proxy Design

- **Date:** 2026-07-24
- **Branch:** `feature/hmi-trends` (worktree `.worktrees/feature-hmi-trends`, forked from `main` @ `f7a973d`)
- **Status:** Approved design (brainstorming); pending implementation plan
- **Related:** HMI v1 design `docs/superpowers/specs/2026-07-23-desktop-hmi-design.md`; Influx writer on `feature/influxdb-access` (`docs/superpowers/specs/2026-07-23-influxdb-writer-design.md`)

## 1. Goal

Deliver **v1.1 trends** for the Avalonia Operator HMI: historical points for a selected tag shown on the faceplate.

- **Storage:** InfluxDB (time-series), written by the bridge writer path (other branch).
- **Access:** HMI never connects to Influx. HMI calls the **bridge** only; the bridge runs Flux (or equivalent) with the host-held token and returns JSON points.

This is item **3** from the HMI phased rollout: trends via **bridge proxy**, not direct HMI → Influx.

## 2. Problem

HMI v1 ships live tag list + faceplate + write over REST/SignalR on port 8080. Operators also need short history (sparkline / mini chart) without:

- Putting Influx URL/org/bucket/**token** on every operator PC
- Teaching the HMI Influx schema
- Blocking Android later on a different history path

The factory diagram stores history in Influx; the client surface remains the bridge Web Host.

## 3. Scope

### In scope (this branch)

- Bridge **query proxy** endpoint under existing Kestrel `:8080`
- Shared Client DTOs for trend series
- Avalonia faceplate **history chart** (default last 1 hour) loaded via bridge REST only
- Graceful empty/error when Influx is down, unconfigured, or has no points
- Stub-friendly query seam so this branch builds/tests without requiring the writer branch to merge first
- Docs: `context.md` API bullets when implementing

### Out of scope (this branch)

- Influx **writer** (`OpcBridge.Influx` write path, `InfluxEnabled`, dashboard Influx Connection panel) — owned by `feature/influxdb-access`
- HMI → Influx direct connection
- Multi-Influx, multi-bridge
- Full historian UI, multi-pen charts, export CSV
- Dashboard web trend charts (may reuse the same API later)
- Auth on HMI/trends API
- SignalR streaming of historical points

### Dependency on writer branch

| Branch | Owns |
|---|---|
| `feature/influxdb-access` | Write: opt-in tags → Influx measurement (default `opc_tags`), tags `source_id` / `da_item_id`, fields `value` / `quality` / `is_good` |
| **`feature/hmi-trends`** | Read: proxy + HMI chart |

**Integration order:** implement proxy + HMI against a query interface (real Flux when writer/settings exist; empty/stub otherwise). Rebase onto writer branch or `main` after writer merges; smoke real data on host.

## 4. Decisions

| Topic | Decision |
|---|---|
| Where history is stored | **InfluxDB server** |
| How HMI gets history | **Bridge proxy only** (`GET /api/hmi/trends`) |
| Influx token location | **Bridge only** (`influx.json` / runtime settings from writer design) |
| Chart placement | Faceplate of selected tag |
| Default time range | **Last 1 hour** (`from = now-1h`, `to = now`) |
| Max points | Default **500**, clamp **10..2000**; downsample server-side if needed |
| HTTP when Influx unavailable | **200** with `points: []` and non-null `error` string (HMI always parses JSON) |
| Bad request params | **400** with error message |
| Mapping required for query? | **No** — allow history if tag was logged before unmapped; empty if no data |
| Live values | Unchanged SignalR/REST; chart is pull-based |
| Chart refresh | On tag select + optional **30s** timer while tag selected |
| Chart library | Lightweight Avalonia drawing (polyline/`Path`); avoid heavy chart package unless necessary |
| Parallel with writer | Separate branch/worktree; no dual-edit of writer files |

## 5. Architecture

```
                    ┌─────────────────────────────────────┐
                    │  InfluxDB (:8086)                   │
                    │  bucket e.g. bridge_trends          │
                    └──────────────▲──────────────────────┘
                                   │
              write (other branch) │  Flux query + token
                                   │
┌──────────────────────────────────┴──────────────────────┐
│  OpcBridge.App  :8080                                   │
│  Live: /api/hmi/tags, /api/hmi/write, SignalR /hmi      │
│  NEW:  GET /api/hmi/trends  → IInfluxTrendQuery         │
└──────────────────────────────────▲──────────────────────┘
                                   │ REST only
                                   │
                        ┌──────────┴──────────┐
                        │  OpcBridge.Hmi      │
                        │  BaseUrl only       │
                        │  Faceplate chart    │
                        └─────────────────────┘
```

**Rules:**

- HMI depends on `OpcBridge.Client` + HTTP only — never Influx SDK, never Da/Ua/COM.
- App uses Influx credentials only server-side.
- Point schema for queries **matches writer design**: measurement configurable (default `opc_tags`); tags `source_id`, `da_item_id`; fields `value`, `quality`, `is_good`; timestamp UTC.

## 6. API

### `GET /api/hmi/trends`

**Query parameters:**

| Name | Required | Default | Notes |
|---|---|---|---|
| `sourceId` | yes | — | Non-empty |
| `daItemId` | yes | — | Non-empty |
| `from` | no | now − 1h | ISO-8601 UTC |
| `to` | no | now | ISO-8601 UTC; must be ≥ `from` |
| `maxPoints` | no | 500 | Clamped to 10..2000 |

**Response body** (`HmiTrendResponse`, camelCase):

```json
{
  "sourceId": "default",
  "daItemId": "Random.Int1",
  "fromUtc": "2026-07-24T02:00:00Z",
  "toUtc": "2026-07-24T03:00:00Z",
  "points": [
    { "t": "2026-07-24T02:00:01Z", "v": 42.5, "q": 192, "good": true }
  ],
  "truncated": false,
  "error": null
}
```

| Field | Meaning |
|---|---|
| `points[].t` | Sample time UTC |
| `points[].v` | Numeric or JSON value (prefer number for chart; strings may skip plot or show as gap) |
| `points[].q` | DA quality if known |
| `points[].good` | Good flag if known |
| `truncated` | True if series was thinned to `maxPoints` |
| `error` | Human-readable reason when empty due to config/outage; null on success |

**Status codes:**

- `200` — success or soft failure (Influx unavailable → empty points + `error`)
- `400` — missing/invalid params
- `500` — unexpected server failure (message in body if safe)

### Client DTOs (`OpcBridge.Client`)

- `HmiTrendPoint` — `DateTime T`, `object? V`, `int? Q`, `bool? Good`
- `HmiTrendResponse` — as above

### Query seam

Introduce `IInfluxTrendQuery` (name exact in plan):

```csharp
Task<HmiTrendResponse> QueryAsync(
    string sourceId,
    string daItemId,
    DateTime fromUtc,
    DateTime toUtc,
    int maxPoints,
    CancellationToken ct);
```

- **Preferred home after writer merge:** `OpcBridge.Influx` implementation using official client + Flux aligned to writer measurement/tags/fields.
- **This branch before merge:** interface + null/stub implementation returning empty series with clear `error`, registered in DI so App and tests compile.

Do **not** re-implement the full writer on this branch.

## 7. HMI faceplate

Keep connect / tag list / live faceplate / write.

**Add:**

1. When `SelectedTag` changes (and after successful connect if a tag is already selected): call `BridgeApiClient.GetTrendsAsync(...)`.
2. Default window: last **1 hour**.
3. Render sparkline under faceplate fields (value, quality, timestamp, write).
4. Status under chart: `No history`, `Influx not available`, or load error text from `error`.
5. Optional 30s refresh timer while a tag remains selected.
6. Non-numeric series: show message; do not crash.

**No** Influx URL/token fields in HMI UI.

## 8. Data flow

**Write (other branch — not implemented here):**

```
DA/Manual → BridgeState.SetValue → Influx channel (if InfluxEnabled)
  → measurement opc_tags, tags source_id/da_item_id, fields value/quality/is_good
  → InfluxDB
```

**Read (this branch):**

```
Select tag → GET /api/hmi/trends → Flux (bridge token) → InfluxDB
  → HmiTrendResponse → faceplate chart
```

## 9. Error handling

| Condition | Bridge | HMI |
|---|---|---|
| Influx disabled / no token / connect faulted | 200, empty points, `error` set | Chart empty + message; live OK |
| No samples for tag/window | 200, empty points, `error` null or “No history” | “No history” |
| Invalid `from`/`to` | 400 | Status message |
| Bridge down | n/a | Existing disconnect behavior |
| Query timeout | Soft error in body or 200+error | Message; do not hang UI forever |

Influx/query failures must **not** stop DA, UA, MQTT, or live HMI stream.

## 10. Testing

**Automated (Docker):**

- DTO serialization smoke
- API: missing `sourceId`/`daItemId` → 400
- API: stub query → 200 with known points
- API: stub “unavailable” → 200 empty + `error`
- Client/view-model: parse response and expose series for binding

**Manual (after writer + Influx on host):**

1. Enable Influx write for a tag; wait for points.
2. HMI faceplate for that tag shows history for last hour.
3. Stop Influx: live tags still work; chart shows unavailable/empty.
4. Confirm no Influx token in HMI process/config.

## 11. Deploy notes

- Bridge publish remains win-x86 framework-dependent as today.
- Preserve `influx.json` on cutover (writer deploy concern; document for joint release).
- HMI install path on host (`…\hmi\`) unchanged; only needs updated client bits when chart ships.
- Port stays **8080**; no second listener.

## 12. Success criteria

1. HMI never configures or stores Influx credentials.
2. `GET /api/hmi/trends` returns points for a logged tag when Influx has data and bridge can query.
3. Faceplate shows a 1h sparkline (or clear empty/error state) for the selected tag.
4. Influx outage does not break live HMI or bridge core.
5. Solution builds 0w/0e; new/updated tests green in Docker.
6. No writer reimplementation on this branch; merge/rebase with `feature/influxdb-access` for production history.

## 13. Open follow-ups (not blocking)

- Range chips (15m / 1h / 6h / 24h)
- Dashboard reuse of same API
- Auth on `/api/hmi/*`
- Aggregate multi-tag trends page
