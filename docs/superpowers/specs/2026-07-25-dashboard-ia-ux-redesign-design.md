# Dashboard IA & UX Redesign Design

- **Date:** 2026-07-25
- **Branch:** `feature/ui-ux-modify` (worktree `.worktrees/feature-ui-ux-modify`)
- **Status:** Approved design (sections §1–§4), pending implementation plan
- **Primary surface:** Web dashboard in `src/OpcBridge.App/DashboardPage.cs`

## 1. Goal

Reorganize the web dashboard so setup and daily operations no longer compete as eleven equal top tabs. Adopt a Kepware-inspired **domain grouping + guided wizards** model while reusing existing APIs and page logic.

**Success means:**

1. No flat 11-tab bar.
2. MQTT lives under **IoT**; InfluxDB lives under **Historian**.
3. Add Source / MQTT / Historian can be completed via guided steps.
4. Operators can live in **Ops** without wading through config.
5. Integrators follow Connectivity → Tags → IoT → Historian as a linear setup path.

## 2. Users & constraints

| Audience | Need |
|---|---|
| Integrator / commissioning | Add sources, map tags, wire MQTT/Influx, validate |
| Plant operator / maintenance | Live values, health, diagnose faults |

**Decisions locked in brainstorming:**

- Primary users: **both equally**
- Fidelity: **navigation + guided wizards** (not full industrial IDE)
- Shell: **left sidebar groups + content pane**

**Non-goals:**

- Real multi-protocol driver packs / channel-device metaphor owned by this app
- Desktop HMI changes
- Backend API redesign
- Full visual redesign / new design system (keep current dark industrial CSS tokens)
- Changing MQTT or Influx write semantics
- Per-tag MQTT/Influx enable moving off the faceplate

## 3. Current problems

- Single ~3500-line `DashboardPage.cs` with flat tabs:
  `Monitor · Connection · Diagnostics · Tags · OPC DA to DA · Logs · MQTT · InfluxDB · Diagram · Help · About`
- Config, live ops, and docs share the same navigation weight.
- Product names (MQTT, InfluxDB) sit at top level instead of role names (IoT, Historian).
- First-time setup is form-first, not workflow-first (“add driver / connect broker / enable historian”).

## 4. Information architecture

### 4.1 Shell

```
┌──────────────────────────────────────────────────────────────┐
│ Brand · global status pills (UA / Sources / Tags / IoT / Hist)│
├────────────┬─────────────────────────────────────────────────┤
│ CONNECTIVITY│  Content pane                                  │
│  Sources    │  Wizard overlay/drawer when adding             │
│  Diagnostics│                                                │
│ TAGS        │                                                │
│  Maps       │                                                │
│  DA Links   │                                                │
│ IOT         │                                                │
│  MQTT       │                                                │
│  Traffic    │                                                │
│ HISTORIAN   │                                                │
│  InfluxDB   │                                                │
│ OPS         │                                                │
│  Monitor    │                                                │
│  Logs       │                                                │
│  Diagram    │                                                │
│ HELP        │                                                │
│  Guide      │                                                │
│  About      │                                                │
└────────────┴─────────────────────────────────────────────────┘
```

### 4.2 Old tab → new location

| Old tab | New location |
|---|---|
| Connection | Connectivity → Sources |
| Diagnostics (DA) | Connectivity → Diagnostics |
| Tags | Tags → Maps (browser + mappings + faceplate) |
| OPC DA to DA | Tags → DA Links |
| MQTT | IoT → MQTT (broker) + IoT → Traffic |
| InfluxDB | Historian → InfluxDB |
| Monitor | Ops → Monitor |
| Logs | Ops → Logs |
| Diagram | Ops → Diagram |
| Help | Help → Guide |
| About | Help → About |

### 4.3 Interaction rules

- Sidebar groups collapsible; last route remembered.
- Global status pills remain in the header.
- Faceplate remains a modal over Tags → Maps (not a nav item).
- Default landing: **Ops → Monitor**.
- Empty-sources gate may prompt Connectivity wizard without forcing leave of Monitor forever.
- Dense sidebar (~220px), active item accent, group labels uppercase muted.
- Transitions 150–200ms; respect `prefers-reduced-motion`.
- Text labels required; icons optional later (SVG only, no emoji icons).

## 5. Guided wizards

Wizards are overlays/drawers over the relevant page. **Existing APIs only.**

### 5.1 Add OPC DA Source — Connectivity → Sources

**Entry:** primary **+ Add Source**; empty-state CTA when `sources == 0`.

| Step | Title | UI | API |
|---|---|---|---|
| 1 | Identity | Source ID (unique, no spaces), optional display label | client validate |
| 2 | Server | Host, ProgID/CLSID; Browse servers | `POST /api/da/servers` |
| 3 | Credentials | Domain/user/pass (optional DCOM) | — |
| 4 | Defaults | Update rate, Use subscriptions | — |
| 5 | Review & Save | Summary → Save | `POST /api/da/sources` |

**Rules:**

- Progress steps visible; Back/Next; Cancel discards draft.
- Step 2 may skip browse if user pastes known ProgID.
- After save: select new source; optional “Map tags next?” → Tags → Maps with source preselected.
- Edit existing source: same fields without wizard chrome, or prefilled wizard with Source ID locked.

### 5.2 Connect MQTT — IoT → MQTT

**Entry:** Setup wizard when broker not configured / never connected; else normal form.

| Step | Title | Fields | API |
|---|---|---|---|
| 1 | Broker | URL, client ID, auto-connect | — |
| 2 | Auth & topics | user/pass, topic prefix, QoS, payload fields | — |
| 3 | Save & connect | Review → Save → optional Connect now | `POST /api/mqtt/config`, `POST /api/mqtt/connect` |

**Rules:**

- Traffic stays on **IoT → Traffic**.
- Per-tag MQTT enable stays on faceplate.
- Nav badge: Disconnected / Connecting / Connected / Faulted.

### 5.3 Enable Historian — Historian → InfluxDB

**Entry:** Setup wizard if no URL/token; else config page.

| Step | Title | Fields | API |
|---|---|---|---|
| 1 | Server | URL, Org, Bucket | — |
| 2 | Auth | Token (masked) | — |
| 3 | Save & connect | Auto-connect + Connect now | `POST /api/influx/config`, `POST /api/influx/connect` |

**Rules:**

- Page title **Historian**; product label “InfluxDB 2.x” secondary.
- Live counters (written/s, last error) remain on the page.
- Per-tag historian enable stays on faceplate (`fpInfluxEnabled`).
- Never fake connected; surface status API errors.

### 5.4 Shared wizard UX

- Modal or right drawer (~480px), not full-page hijack.
- Disable Next until step validates.
- Inline field errors (not toast-only).
- Esc = cancel; Enter = Next/Save when valid.
- Overlay only on config pages; does not block Ops when user navigates away (close on navigate).

### 5.5 Explicit non-wizard

- Tags mapping: browse + list + faceplate (exploratory).
- DA Links: picker UI.
- Diagnostics / Logs / Diagram: operational pages.

## 6. Page contents & empty states

### 6.1 Connectivity

**Sources**

- List of saved sources (status, host, tag count).
- Detail/editor for selected source (current Connection form).
- Primary CTA: **+ Add Source**.
- Row actions: Edit, Remove (confirm); connect behavior unchanged if already present.
- Empty: “No OPC DA sources yet” + Add Source.

**Diagnostics**

- DA source diagnostics + time sync from current Diagnostics tab.
- Not mixed with MQTT/Influx health.

### 6.2 Tags

**Maps** (old Tags tab)

- Source selector + browser (Browse All / Folders).
- Mapping list + Add Mapping.
- Row click → faceplate (General / MQTT / Historian toggles).
- Empty mappings: guide to browse; if no sources, point to Connectivity.

**DA Links**

- Same workflow; title “OPC DA → DA forwarding”.
- Empty: short explanation + link to Maps if needed.

### 6.3 IoT

**MQTT** — broker config + Live Connection.  
**Traffic** — traffic monitor only. Empty: “Enable MQTT on tags in faceplate” → Tags → Maps.

### 6.4 Historian

**InfluxDB** — config + Live Connection + write counters. Soft empty/error states only on this page.

### 6.5 Ops

- **Monitor** — overview pills, source status, live values (default landing).
- **Logs** — unchanged.
- **Diagram** — unchanged (All / DA→UA / DA→DA / MQTT).

### 6.6 Help

- **Guide** / **About** — existing content; update “Dashboard Tabs” section to new IA during implementation.

### 6.7 First-run banners

| Condition | Prompt |
|---|---|
| 0 sources | Banner on Monitor + Maps → Add Source |
| sources, 0 mappings | Banner on Monitor → Map tags |
| MQTT off | Soft hint on IoT only |
| Influx unconfigured | Soft hint on Historian only |

## 7. Routing

Hash routes for refresh/bookmarks:

| Route | Page |
|---|---|
| `#/ops/monitor` | Ops → Monitor (default) |
| `#/ops/logs` | Ops → Logs |
| `#/ops/diagram` | Ops → Diagram |
| `#/connectivity/sources` | Connectivity → Sources |
| `#/connectivity/diagnostics` | Connectivity → Diagnostics |
| `#/tags/maps` | Tags → Maps |
| `#/tags/links` | Tags → DA Links |
| `#/iot/mqtt` | IoT → MQTT |
| `#/iot/traffic` | IoT → Traffic |
| `#/historian/influx` | Historian → InfluxDB |
| `#/help/guide` | Help → Guide |
| `#/help/about` | Help → About |

**Contract:**

```
navigate(group/page)
  → set sidebar active item
  → show one [data-page] panel
  → location.hash = #/group/page
  → lazy refresh that page’s data (reuse existing loaders)
```

Unknown hash → `#/ops/monitor`.  
Any legacy `?tab=` / old tab ids map once via redirect table (`mqtt` → `iot/mqtt`, etc.).

## 8. Implementation shape

### 8.1 Phases

1. **IA shell** — replace flat tabbar with sidebar; wrap `showTab` as `navigate`; keep internal panel ids working.
2. **Wizards** — Source / MQTT / Historian overlays on existing save/connect functions.
3. **Empty states + help** — banners, HelpContent IA rewrite, deep-link redirects.

### 8.2 Code strategy

- Prefer incremental edits in `DashboardPage.cs` (current pattern).
- Extract CSS/JS modules only if the file becomes unmaintainable mid-work — not a prerequisite.
- No new backend endpoints for v1 of this redesign.

### 8.3 Key existing APIs (reuse)

- DA sources: `GET/POST /api/da/sources`, `POST /api/da/sources/remove`, `POST /api/da/servers`, `POST /api/da/tags`
- MQTT: `GET/POST /api/mqtt/config`, `POST /api/mqtt/connect|disconnect`, `GET /api/mqtt/status|values`
- Influx: `GET/POST /api/influx/config`, `POST /api/influx/connect|disconnect`, `GET /api/influx/status`
- Dashboard aggregate: `GET /api/dashboard`

### 8.4 Verification

- `dotnet build -c Release` in Docker → 0 warnings, 0 errors.
- Manual smoke: every nav item shows the correct panel; wizards persist via existing APIs.
- Hash refresh restores page.
- Faceplate still opens from Maps; MQTT/Influx faceplate toggles still save.
- MQTT/Influx connect/disconnect still work.
- Update help/content tests if asserted strings move.

### 8.5 Rollout

- Work only on `feature/ui-ux-modify` worktree.
- No deploy until explicitly requested.

## 9. Risks & mitigations

| Risk | Mitigation |
|---|---|
| `DashboardPage.cs` size / merge conflict | Small IA-first PR; avoid unrelated refactors |
| Broken deep links / refresh | Hash router + default fallback + legacy redirect map |
| Wizard duplicates form logic bugs | Call existing `saveSource` / `saveMqtt` / `saveInflux` paths; do not fork save logic |
| Operator loses Monitor | Default landing remains Ops → Monitor; global pills stay |
| Scope creep into visual redesign | Explicit non-goal; keep CSS tokens |

## 10. Open items resolved

| Question | Resolution |
|---|---|
| Approach | A — domain sidebar + wizards |
| MQTT placement | IoT group |
| Influx placement | Historian group |
| Add driver analogue | Add OPC DA Source wizard |
| Property-grid IDE | Out of scope |

## 11. Next step

After user reviews this spec file, invoke **writing-plans** to produce an implementation plan under `docs/superpowers/plans/`, then implement on `feature/ui-ux-modify`.
