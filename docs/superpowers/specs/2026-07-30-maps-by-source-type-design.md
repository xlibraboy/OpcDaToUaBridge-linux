# Maps by source type

- **Date:** 2026-07-30
- **Branch:** `feature/separate-mapping-tag`
- **Worktree:** `.worktrees/feature-separate-mapping-tag`
- **Status:** Approved

## Goal

Polish **Tags → Maps** so mapping is based on **source type**, not one mixed “all sources” dropdown left over from the single-DA era.

Connectivity already splits **OPC DA / OPC UA / Drivers**. Maps must match that mental model.

## Problem

Current Maps (`view-tags`, route `tags/maps`):

- One source select labeled **DA Source** filled with **every** source type.
- One tag browser that branches on type at browse time.
- One mapped list showing **all** mappings across types.
- `daSources()` still means “not UA” and therefore includes PLC drivers — wrong for true OPC DA.

Operators with multiple source kinds get a confusing, pre-multi-source workflow.

## Decisions (approved)

| Decision | Choice |
|----------|--------|
| Layout | In-page type sub-tabs under Maps (not three sidebar items, not three full views) |
| Mapped list scope | Only the active source type |
| Persistence / API | Unchanged — UI filter only |
| Approach | Approach A: one `view-tags` page + type tabs |

## UX

### Navigation

- Sidebar stays: **Tags → Maps** (`data-tab="tags"`, `data-route="tags/maps"`).
- Inside Maps, type sub-tabs:
  - **OPC DA**
  - **OPC UA**
  - **Drivers**
- Deep links (recommended):
  - `tags/maps` → last selected type, else OPC DA
  - `tags/maps/opc-da`
  - `tags/maps/opc-ua`
  - `tags/maps/drivers`
- Changing type does **not** leave the Maps page; only the type pane state changes.

### Per-type pane

For the active type:

1. **Source** dropdown — only sources of that type.
2. **Tag Browser** — existing browse behavior for that type:
   - OPC DA → `/api/da/tags` (folders / browse all)
   - OPC UA → `/api/ua/browse`
   - Drivers → existing driver path (manual address + any browse the type already supports; no new browse API in this change)
3. **Mapped list** — only mappings whose `SourceId` belongs to a source of the active type.
4. Text/sort on the mapped list remain, but operate **within** the type-filtered set.
5. Empty states:
   - No sources of this type → banner with link to the matching Connectivity page (`connectivity/opc-da`, `connectivity/opc-ua`, `connectivity/drivers`).
   - Sources exist but no mappings of this type → keep “map tags” guidance scoped to this type.

### Copy / labels

| Before | After |
|--------|-------|
| DA Source | Source |
| mixed type badges only in option text | Type is implied by the active sub-tab; option text can drop redundant `[DA]`/`[UA]` noise |
| Manual placeholder already multi-example | Keep; optionally tweak per type if cheap |
| Help: Maps as one mixed browse/map place | Help: Maps has OPC DA / OPC UA / Drivers sub-tabs |

Faceplate title/fields stay as Phase 4 multi-source labels (Item ID, Source → UA, etc.). No faceplate redesign.

### Out of scope

- No `mappings.json` / `TagMapping` schema change
- No new mapping APIs
- No DA Links redesign
- No backend browse API for drivers beyond what already exists
- No Monitor/Diagram changes (except any incidental “Map Tags” deep-link still lands on Maps)

## Behavior details

### Source type membership

| Type tab | Sources included |
|----------|------------------|
| OPC DA | `SourceType == OpcDa` (or missing/default treated as OpcDa). **Not** UA, **not** MelsecA3n, **not** S7200Ppi |
| OPC UA | `SourceType == OpcUa` |
| Drivers | `MelsecA3n` or `S7200Ppi` (same as `isDriverSource`) |

Introduce a true OPC DA helper for Maps (and prefer reusing it where Connectivity already wants pure DA):

```js
function opcDaSources() {
  return state.sources.filter(s => !isUaSource(s) && !isDriverSource(s));
}
```

Do **not** break existing Connectivity code that still uses `daSources()` without auditing callers; either:

- leave `daSources()` as-is for legacy call sites that already guard with `isDriverSource`, or
- tighten carefully and fix every caller in the same change.

Maps **must** use the pure OPC DA set for the OPC DA tab.

### Selected source

- Keep a single `state.selectedSourceId` **or** add `state.mapSelectedSourceId` if shared selection fights Connectivity forms.
- Preferred lazy path: keep `selectedSourceId`, but on Maps type switch:
  - if current selection is not in the active type set, pick the first source of that type (or clear).
  - clear tag tree / breadcrumb so the previous type’s tree does not linger.
- `mapSourceSelect` change still selects that source for browse/add.

### Mapping list filter

Extend `applyMappingView` (or a thin wrapper used by `loadMappings` / `rerenderMappings`):

1. Restrict to mappings whose source is in the active type’s source set (by `sourceId` lookup on `state.sources`).
2. Then apply existing text filter + sort.

Count text: show type-scoped count; if global total differs, optional `N / M mappings` is fine (N = type view after text filter, M = type total or global — pick one rule and stick to it in UI: **prefer N type-filtered, mention global only if useful**).

Simplest correct rule:

- `mapCount` = rows currently shown after type + text filter
- subtitle or title remains “Source → OPC UA Mappings”

### Browse buttons

- OPC DA: keep **Browse All Tags** / **Browse Folders**.
- OPC UA: keep existing UA browse entry (tree from root / trail).
- Drivers: do not invent folder browse; keep manual Item ID add prominent; if current code routes drivers through DA browse, leave behavior unless it already no-ops — document actual behavior in the plan, do not expand driver browse scope here.

### Add / remove / faceplate

Unchanged contracts:

- `data-action="add-tag"`, Map button
- `/api/mappings/add`, `/api/mappings/update`, `/api/mappings/remove`
- Faceplate ids (`fpInfluxEnabled`, etc.)

After add/remove/update, `loadMappings()` re-renders the **type-scoped** list.

## Implementation sketch (UI only)

Primary file: `src/OpcBridge.App/DashboardPage.cs`

1. **HTML** under `#view-tags`:
   - type tab row (`mapTypeTabs`) with buttons `data-map-type="opc-da|opc-ua|drivers"`
   - rename label to Source
   - keep single browser + list containers (no markup triplication)
2. **State:** `state.mapType = 'opc-da' | 'opc-ua' | 'drivers'`
3. **Helpers:** `opcDaSources()`, `mapTypeSources()`, `setMapType(type)`, `sourceMatchesMapType(source, type)`
4. **`renderSources`:** fill `mapSourceSelect` from `mapTypeSources()` only
5. **`applyMappingView` / `loadMappings`:** type filter
6. **Routing:** extend `ROUTE_TO_TAB` / `navigate` so `tags/maps/*` activates tags + sets `mapType`
7. **HelpContent.cs:** Maps described as type sub-tabs
8. **Tests:** update `DashboardPageTests` string locks for new ids/routes/helpers; keep existing browse/API string locks unless intentionally replaced

No changes required in `MappingStore`, `TagMapping`, or mapping HTTP handlers for v1 of this polish.

## Test / freeze contracts

Existing freezes that must keep working unless tests are updated in the same PR:

- `data-tab="tags"`, route family `tags/maps`
- `browseTags` + `/api/da/tags`
- `browseUaSource` + `/api/ua/browse` + `isUaSource(currentSource())`
- `data-action="add-tag"` / Map button
- faceplate influx id `fpInfluxEnabled`
- Connectivity freezes unchanged

New freezes to add:

- map type tab markers (e.g. `data-map-type="opc-da"` …)
- optional deep routes `tags/maps/opc-da|opc-ua|drivers`
- `opcDaSources` / pure DA filter if introduced

## Acceptance criteria

1. Maps shows three type sub-tabs: OPC DA, OPC UA, Drivers.
2. Source dropdown never mixes types for the active tab.
3. Mapped list shows only mappings for sources of the active type.
4. OPC DA tab never lists Melsec/S7/UA sources.
5. Browse + Map + faceplate still work per type as today.
6. No mapping API / file format change.
7. Help text matches the new workflow.
8. Dashboard page tests updated and green for the new contracts.
9. Docker smoke: open Maps, switch type tabs, source list and mapped list scope correctly.

## Non-goals

- Per-driver sub-tabs (A3N vs S7) — Drivers stays one tab
- Separate mapped-list pages per type
- Renaming product routes away from `tags/maps`
- Migrating identity off `(SourceId, ItemId)`

## Risks

| Risk | Mitigation |
|------|------------|
| Tightening `daSources()` breaks Connectivity | Prefer new `opcDaSources()` for Maps; audit before changing shared helper |
| Shared `selectedSourceId` desyncs Connectivity forms | On type switch only auto-pick when current source is wrong type; Connectivity pages still re-bind on their own render |
| Orphan mappings (source deleted) | Type filter via live `state.sources` may hide orphans; acceptable for v1, same as other source-scoped UIs |
| Test string freezes | Update tests in same change as HTML/JS |

## Follow-ups (not this change)

- True driver address browse UX if product needs it later
- Per-driver filter chips inside Drivers map tab
- Monitor “map tags” CTA deep-linking to a specific type when only one type has sources
