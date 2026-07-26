# Connectivity → OPC DA Tab Design

- **Date:** 2026-07-27
- **Branch:** `feature/ui-ux-modify` (worktree `.worktrees/feature-ui-ux-modify`)
- **Status:** Approved design (§1–§4), pending implementation plan
- **Primary surface:** Web dashboard in `src/OpcBridge.App/DashboardPage.cs`
- **Parent IA:** `docs/superpowers/specs/2026-07-25-dashboard-ia-ux-redesign-design.md`

## 1. Goal

Split **Connectivity** so daily source *status* and DA *configuration* are not the same page.

Today `Connectivity → Sources` owns both the saved-connection list and the full DA config form (identity, host/ProgID, credentials, default rate, subscriptions, discover, backup). Integrators hunting for “OPC DA settings” land on a crowded Sources page.

**Success means:**

1. Sidebar under Connectivity reads: **Sources → OPC DA → Diagnostics**.
2. **Sources** is status-list only (health, identity summary, Select, + Add Source wizard).
3. **OPC DA** owns all DA configuration UI that currently lives on Sources.
4. Existing form control IDs and APIs stay stable (no backend redesign).
5. Select on Sources deep-links into OPC DA with that source loaded.

## 2. Decisions locked in brainstorming

| Decision | Choice |
|---|---|
| What moves to OPC DA | **All DA configuration** (form, rate, subscriptions, discover, backup, edit toolbar) |
| What Sources becomes | **Status list only** |
| Implementation approach | **A** — new tab + move form DOM; keep element IDs |
| Tab label | **OPC DA** |
| Add Source wizard entry | Primary on **Sources**; secondary also on **OPC DA** |
| `data-tab` for Sources | Keep **`connection`** this pass (minimize JS churn) |
| Saved Connections list on OPC DA | **Keep** (alongside Selected dropdown) |

## 3. Non-goals

- Backend / API changes (`/api/da/*`, export/import contracts)
- Diagnostics page redesign
- UA endpoint / MQTT / Influx / HMI changes
- Renaming `data-tab="connection"` → `sources` (deferred)
- Full visual redesign or new design tokens
- Collapsing Sources + OPC DA into one scroll page

## 4. Information architecture

### 4.1 Shell (Connectivity only)

```
CONNECTIVITY
  Sources       connectivity/sources      status list
  OPC DA        connectivity/opc-da       all DA config
  Diagnostics   connectivity/diagnostics  unchanged
```

Other groups (Tags, IoT, Historian, Ops, Help) unchanged.

### 4.2 Route / tab / view map

| Nav label | Route | `data-tab` | View id | Role |
|---|---|---|---|---|
| Sources | `connectivity/sources` | `connection` | `view-connection` (slimmed) | Status list |
| OPC DA | `connectivity/opc-da` | `opc-da` | `view-opc-da` (new) | Full DA config |
| Diagnostics | `connectivity/diagnostics` | `diagnostics` | `view-diagnostics` | Unchanged |

### 4.3 Legacy hashes

| Hash | Resolves to |
|---|---|
| `#connection` | Sources list (`connectivity/sources`) |
| `#connectivity/sources` | Sources list |
| `#opc-da` / `#connectivity/opc-da` | OPC DA config |
| Existing other legacy tabs | Unchanged from IA redesign |

Bookmarks that previously opened the full form under `#connection` now open the list; CTAs and Select push users to OPC DA for editing.

### 4.4 Default landing

Unchanged: **Ops → Monitor**.

## 5. Sources page (status-only)

### 5.1 Keeps

- Page title / box header **Sources**
- **+ Add Source** → existing `openAddSourceWizard()`
- First-run empty banner when `sources.length === 0`
- Status list container **`#sourcesStatusList`** (new id — do not reuse `#sourcesList`)
- List rows: display name, sourceId, host, ProgID, rate, connection state badge, last error snippet
- **Select** action per row

### 5.2 Removes from this view

- Identity fields (`cfgSourceId`, `cfgDisplayName`, …)
- Server address (`cfgProgId`, `cfgHost`)
- Credentials
- Default update rate + Apply
- DA subscriptions checkbox
- Discover Servers panel
- Saved Connections side list (list is the main content now)
- Backup & Restore
- Save / Reset / New / Remove toolbar (edit lives on OPC DA)

Implementation may leave hidden stubs only if a one-line JS reference would break; prefer **no** orphaned visible controls on Sources.

### 5.3 Select behavior

1. Set selected source in client state (same selection model used by the form today).
2. `navigate('connectivity/opc-da')`.
3. OPC DA view shows that source in Selected + populates form fields (existing load/select path).

List rendering: keep `renderSourcesList()` (or equivalent) writing the **OPC DA** side list to `#sourcesList`. Add a slim status renderer for `#sourcesStatusList` on Sources (can share row HTML helper; Select on status list always navigates to OPC DA).

### 5.4 Empty / first-run

- No sources: banner + **+ Add Source** on Sources (wizard).
- After save, optional “Map tags?” prompt unchanged.
- Soft banners elsewhere that say “Add Source” continue to point at `connectivity/sources`.

## 6. OPC DA page (all config)

### 6.1 Layout

Reuse current `conn-layout`:

- **Main:** selected source + full edit form + toolbar + **+ Add Source**
- **Side:** Discover Servers, Saved Connections list, Backup & Restore

Content is the body currently under `view-connection` (minus the status-only pieces that stay on Sources).

### 6.2 Controls (stable IDs)

Keep existing IDs so save/load/wizard JS stays largely intact:

- Selection: `selectedSource`
- Identity / server / credentials: `cfgSourceId`, `cfgDisplayName`, `cfgProgId`, `cfgHost`, `cfgUser`, `cfgPass`, `cfgDomain`
- Rate / subs: `cfgUpdateRate`, `cfgApplyRate`, `rateMessage`, `cfgUseSubscriptions`, `subMessage`
- Toolbar: `cfgApply`, `cfgReset`, `cfgNew`, `cfgRemove`, `cfgMessage`
- Discover: `btnReloadServers`, `msgServers`, `listServers`
- Lists: `sourcesList`, `pSourcesSide`
- Backup: `btnExportConfig`, `btnImportConfig`, `importConfigFile`, `configMessage`
- Wizard: existing `addSourceWizard` / `wz*` IDs (modal can remain document-level, not nested exclusively in one view)

### 6.3 APIs

No new endpoints. Continue to use:

- `GET/POST /api/da/sources`, remove, per-source update rate
- Global update rate / use-subscriptions endpoints already wired
- `POST /api/da/servers` (discover)
- Config export/import

### 6.4 Load timing

When `navigate` activates `opc-da` (or `showTab('opc-da')`), run the same config refresh path currently tied to entering Sources/connection (load sources into form, sync rate/subs checkboxes).

## 7. Help, banners, freeze contract

### 7.1 Help

Update Connectivity line to:

- **Connectivity** — Sources (status, add source), **OPC DA** (connection config, rate, subscriptions, discover, backup), Diagnostics

### 7.2 Freeze / test-asserted contract

Add (do not rename existing influx/mqtt contracts):

- `data-tab="opc-da"`
- `id="view-opc-da"`
- route `connectivity/opc-da`
- nav label text **OPC DA** under Connectivity group, immediately below Sources
- `id="sourcesStatusList"` on Sources status page

Preserve:

- `data-tab="connection"` for Sources list
- All `cfg*` / wizard / export IDs listed in §6.2
- Add Source wizard behavior and APIs

### 7.3 Banners

| Banner intent | Target route |
|---|---|
| No sources / Add Source | `connectivity/sources` |
| Configure / edit DA connection | `connectivity/opc-da` |

## 8. Implementation sketch (not a plan)

1. Insert sidebar button after Sources: `data-tab="opc-da"` `data-route="connectivity/opc-da"`.
2. Add `view-opc-da`; move form + side panels from `view-connection`.
3. Slim `view-connection` to list + Add Source + Select.
4. Extend route map + legacy hash table.
5. Wire Select → navigate OPC DA; ensure tab-enter loads config.
6. Update HelpContent + any first-run copy.
7. Smoke in Docker from worktree (`opcbridge:local`, port 18080).

Detailed task breakdown belongs in `docs/superpowers/plans/` after this spec is reviewed.

## 9. Risks

| Risk | Mitigation |
|---|---|
| JS still assumes form nodes exist on Sources tab | Keep IDs globally unique in DOM; only one instance of each `cfg*` node under `view-opc-da` |
| Select without navigation leaves stale form | Always `navigate('connectivity/opc-da')` after selection from Sources |
| Legacy `#connection` users expect the form | Document in Help; list page offers clear path to OPC DA |
| Duplicate Saved lists (Sources main + OPC DA side) | Accepted for this pass; side list on OPC DA aids in-page switching while editing |

## 10. Out of scope follow-ups

- Rename `data-tab="connection"` → `sources` / `view-sources`
- Deduplicate Saved list vs Sources-only list
- Extract dashboard JS modules from `DashboardPage.cs`
- Per-source vs global rate UX redesign

## 11. Approval

Brainstorming approvals (2026-07-27):

- §1 Navigation & routes — approved (keep `data-tab=connection` for Sources)
- §2 Sources status-only — approved (Add Source remains on Sources)
- §3 OPC DA config page — approved (keep Saved list on OPC DA)
- §4 Routing/help/tests + full design — approved to write this spec
