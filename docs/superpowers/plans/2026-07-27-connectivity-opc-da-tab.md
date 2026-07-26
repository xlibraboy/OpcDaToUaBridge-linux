# Connectivity → OPC DA Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split Connectivity so Sources is status-only and a new **OPC DA** tab owns all DA configuration UI.

**Architecture:** DOM/JS-only change inside `DashboardPage.cs`. Keep existing `cfg*` element IDs under new `view-opc-da`. Sources gets a new `#sourcesStatusList`. Route `connectivity/opc-da` maps to `data-tab="opc-da"`. No backend API changes.

**Tech Stack:** .NET 8 Web app, inline HTML/CSS/JS in `DashboardPage.cs`, existing `/api/da/*` and `/api/config/*`.

**Spec:** `docs/superpowers/specs/2026-07-27-connectivity-opc-da-tab-design.md`

## Global Constraints

- Worktree: `/home/iwan/Development/Projects/OpcDaToUaBridge-linux/.worktrees/feature-ui-ux-modify` on branch `feature/ui-ux-modify`.
- Build gate: `docker build -f Dockerfile.local -t opcbridge:local .` from worktree → success; smoke `http://127.0.0.1:18080`.
- No new backend endpoints. Reuse `/api/da/sources`, `/api/da/update-rate`, `/api/da/use-subscriptions`, `/api/da/servers`, `/api/config/export|import`.
- Conventional commits: `feat(dashboard): ...`, `fix(dashboard): ...`, `docs(dashboard): ...`.
- Preserve frozen influx/mqtt/apps/DA Links DOM contracts at top of `DashboardPage.cs`.
- Keep `data-tab="connection"` for Sources list this pass.
- Form control IDs (`cfg*`, `selectedSource`, `sourcesList`, wizard `wz*`) remain unique and live under OPC DA (or document-level wizard modal).
- New Sources list id: `sourcesStatusList` (never reuse `sourcesList`).
- Host port mapping stays `18080:8080` (8080 taken by quiz-nginx).

---

## File map

| File | Responsibility |
|---|---|
| `src/OpcBridge.App/DashboardPage.cs` | Nav button, views, routes, list renderers, Select→OPC DA, showTab load hook, freeze comment |
| `src/OpcBridge.App/HelpContent.cs` | Connectivity nav copy includes OPC DA |
| `docs/superpowers/specs/2026-07-27-connectivity-opc-da-tab-design.md` | Spec (already committed) |

---

### Task 1: Nav + routes + freeze comment

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` (file header contract comment, sidebar ~L429–433, `ROUTE_TO_TAB` ~L2405–2418, `LEGACY_TAB_TO_ROUTE` ~L3978–3990)

**Interfaces:**
- Produces: route key `connectivity/opc-da` → tab `opc-da`; legacy `opc-da` → `connectivity/opc-da`
- Consumes: existing `navigate` / `showTab` / `ROUTE_TO_TAB` pattern

- [ ] **Step 1: Extend freeze comment** at top of `DashboardPage.cs`

Add lines:

```csharp
//   data-tab="opc-da", id="view-opc-da", data-route="connectivity/opc-da", text "OPC DA"
//   id="sourcesStatusList", data-tab="connection" remains Sources list
```

- [ ] **Step 2: Insert sidebar button** immediately after Sources:

```html
<button class="tabbtn" data-tab="opc-da" data-route="connectivity/opc-da" onclick="navigate('connectivity/opc-da')">OPC DA</button>
```

Order must be: Sources → OPC DA → Diagnostics.

- [ ] **Step 3: Extend `ROUTE_TO_TAB`**

```javascript
const ROUTE_TO_TAB = {
  'connectivity/sources': 'connection',
  'connectivity/opc-da': 'opc-da',
  'connectivity/diagnostics': 'diagnostics',
  // ... rest unchanged
};
```

- [ ] **Step 4: Extend `LEGACY_TAB_TO_ROUTE`**

```javascript
opc-da: 'connectivity/opc-da',
```

Keep `connection: 'connectivity/sources'`.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): add Connectivity OPC DA nav route"
```

---

### Task 2: Split views — slim Sources, add view-opc-da

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` HTML for `view-connection` (~L566–634) and insert `view-opc-da`; keep wizard modal after views (document-level)

**Interfaces:**
- Produces: `id="view-opc-da"` containing all former form/side panels; `id="view-connection"` status-only with `id="sourcesStatusList"`
- Consumes: existing CSS classes `conn-layout`, `box`, `list`, etc.

- [ ] **Step 1: Replace `view-connection` body** with status-only markup:

```html
<div class="view" id="view-connection">
  <div class="box">
    <div class="box-h">Sources <button class="btn" type="button" onclick="openAddSourceWizard()" style="margin-left:auto">+ Add Source</button></div>
    <div class="box-b">
      <div class="hint" id="sourcesStatusHint">Select a source to open OPC DA configuration.</div>
      <div class="list" id="sourcesStatusList" style="max-height:none"></div>
    </div>
  </div>
</div>
```

- [ ] **Step 2: Insert `view-opc-da`** with the previous full connection layout (Server Connection form + side Discover / Saved Connections / Backup), header adjusted:

```html
<div class="view" id="view-opc-da">
  <div class="conn-layout">
    <div class="conn-main">
      <div class="box">
        <div class="box-h">OPC DA Configuration
          <button class="btn" type="button" onclick="openAddSourceWizard()" style="margin-left:auto">+ Add Source</button>
          <span class="msg" id="cfgMessage" ...>Select a saved connection or click New.</span>
        </div>
        <!-- existing fields: selectedSource, Identity, Server Address, Credentials,
             Default Update Rate, DA Subscriptions, toolbar cfgApply/cfgReset/cfgNew/cfgRemove -->
      </div>
    </div>
    <div class="conn-side">
      <!-- Discover Servers, Saved Connections #sourcesList, Backup & Restore — same IDs -->
    </div>
  </div>
</div>
```

**Critical:** Move nodes so each `cfg*` / `selectedSource` / `sourcesList` / export ids appear **once**. Wizard `#addSourceWizard` stays outside both views (after `view-opc-da` or where it is today).

- [ ] **Step 3: Sanity-check HTML** — grep that each id appears once:

```bash
rg -o 'id="(cfgSourceId|cfgProgId|selectedSource|sourcesList|sourcesStatusList|view-opc-da|view-connection)"' src/OpcBridge.App/DashboardPage.cs | sort | uniq -c
```

Expected: each id count = 1.

- [ ] **Step 4: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): move DA config form to OPC DA view"
```

---

### Task 3: JS — dual lists, Select → OPC DA, tab load

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` functions `renderSources`, `pickSource`, `bindDynamicButtons`, `showTab`

**Interfaces:**
- Produces: `renderSourcesStatusList()` writing `#sourcesStatusList`; Select from status list calls `pickSource(id, { openOpcDa: true })`
- Consumes: `state.sources`, `state.selectedSourceId`, `navigate`, existing form loaders

- [ ] **Step 1: Extend `pickSource`**

```javascript
function pickSource(sourceId, opts) {
    state.selectedSourceId = sourceId;
    state.editingNewSource = false;
    state.tagPath = '';
    el('tagTree').innerHTML = '<span class="msg">Browse the active source to load tags.</span>';
    el('tagStatus').textContent = 'Browse all tags, or open folders one level at a time.';
    renderCrumb();
    resetLinkBrowser();
    renderSources();
    if (document.getElementById('view-links')?.classList.contains('active')) renderLinksView();
    if (opts && opts.openOpcDa) navigate('connectivity/opc-da');
}
```

- [ ] **Step 2: Split list rendering inside `renderSources`**

Keep dropdown + `#sourcesList` (OPC DA side) as today. Add:

```javascript
function sourceStatusRowHtml(source) {
    const st = source.connectionState || source.ConnectionState || '';
    const err = source.lastError || source.LastError || '';
    const errBit = err ? ` · <span class="bad">${esc(err)}</span>` : '';
    return `<div class="li source-row"><div><div class="n">${esc(source.displayName || source.sourceId)} ${st ? badge(st, stateClass(st)) : ''}</div><div class="p">${esc(source.sourceId)} · ${esc(source.host || 'localhost')} · ${esc(source.progId || '')} · ${formatMs(source.updateRateMs)}${errBit}</div></div><button class="btn ghost" data-action="select-source-status" data-source-id="${attr(source.sourceId)}">Select</button></div>`;
}

function renderSourcesStatusList() {
    const host = el('sourcesStatusList');
    if (!host) return;
    host.innerHTML = state.sources.length
        ? state.sources.map(sourceStatusRowHtml).join('')
        : '<span class="msg">No sources configured. Click + Add Source.</span>';
}
```

Call `renderSourcesStatusList()` at end of `renderSources()` (after `#sourcesList` update). Soft-null-guard `el('sourcesList')` if needed:

```javascript
const list = el('sourcesList');
if (list) list.innerHTML = /* existing */;
```

- [ ] **Step 3: Bind status list clicks** in `bindDynamicButtons`:

```javascript
const statusList = el('sourcesStatusList');
if (statusList) {
  statusList.addEventListener('click', event => {
    const button = event.target.closest('button[data-action="select-source-status"]');
    if (!button) return;
    pickSource(button.dataset.sourceId || '', { openOpcDa: true });
  });
}
```

Keep existing `#sourcesList` handler calling `pickSource(id)` **without** forced navigation (in-page switch on OPC DA).

- [ ] **Step 4: `showTab` load hook** — when entering opc-da, refresh config:

```javascript
if (activeTab === 'opc-da' || activeTab === 'connection') {
  await loadSources().catch(e => console.warn(e));
}
```

(Sources list also benefits from refresh on enter.)

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): Sources status list opens OPC DA config"
```

---

### Task 4: Help content

**Files:**
- Modify: `src/OpcBridge.App/HelpContent.cs` (~L76–112 and any “Connection tab” residual that means DA sources form)

**Interfaces:**
- Produces: Help text listing Connectivity → Sources, OPC DA, Diagnostics

- [ ] **Step 1: Update diagram line**

From:
`Connectivity ──► Sources, Diagnostics`  
To:
`Connectivity ──► Sources, OPC DA, Diagnostics`

- [ ] **Step 2: Update Dashboard Navigation bullets**

```
- **Connectivity** — Sources (status, + Add Source), OPC DA (connection config, rate, subscriptions, discover, backup), Diagnostics (DA health, time sync)
```

Keep: `Use **Connectivity → Sources → + Add Source** for the guided setup wizard.`

Add one line:
`Use **Connectivity → OPC DA** to edit ProgID/host, credentials, default rate, subscriptions, discover servers, and backup/restore.`

- [ ] **Step 3: Fix stale “Connection tab” / “Connection settings” phrases** that still mean the old DA form where they would confuse (search `Connection` in HelpContent; only change DA-form references, not MQTT “Connection” boxes).

- [ ] **Step 4: Commit**

```bash
git add src/OpcBridge.App/HelpContent.cs
git commit -m "docs(dashboard): document Connectivity OPC DA tab in help"
```

---

### Task 5: Build, Docker smoke, verify contract

**Files:** none new

- [ ] **Step 1: Build image from worktree**

```bash
cd /home/iwan/Development/Projects/OpcDaToUaBridge-linux/.worktrees/feature-ui-ux-modify
docker build -f Dockerfile.local -t opcbridge:local .
```

Expected: publish succeeds.

- [ ] **Step 2: Restart container**

```bash
docker stop opcbridge 2>/dev/null || true
docker run --rm -d --name opcbridge -p 18080:8080 -p 4840:4840 opcbridge:local
```

Wait for listening; `curl -sS http://127.0.0.1:18080/health` → `{"status":"ok"}`.

- [ ] **Step 3: HTML contract smoke**

```bash
html=$(curl -sS http://127.0.0.1:18080/)
echo "$html" | grep -o 'data-tab="opc-da"' | head -1
echo "$html" | grep -o 'id="view-opc-da"' | head -1
echo "$html" | grep -o 'id="sourcesStatusList"' | head -1
echo "$html" | grep -o "connectivity/opc-da" | head -1
# form still present once
echo "$html" | grep -c 'id="cfgProgId"'
echo "$html" | grep -c 'id="sourcesList"'
```

Expected: markers present; `cfgProgId` and `sourcesList` count = 1 each.

- [ ] **Step 4: Manual checklist** (browser or curl + JS reasoning)

1. Sidebar order Sources → OPC DA → Diagnostics  
2. Sources has list, no ProgID field  
3. OPC DA has form + discover + backup  
4. Hash `#/connectivity/opc-da` activates OPC DA tab  

- [ ] **Step 5: Final commit only if fixes needed**; otherwise note smoke result in session.

---

## Spec coverage checklist

| Spec requirement | Task |
|---|---|
| Nav Sources → OPC DA → Diagnostics | 1 |
| Route `connectivity/opc-da`, `data-tab=opc-da`, `view-opc-da` | 1–2 |
| Sources status-only + `#sourcesStatusList` | 2–3 |
| All config under OPC DA, stable `cfg*` ids | 2 |
| Select → navigate OPC DA | 3 |
| Dual list renderers | 3 |
| Legacy hashes | 1 |
| Help update | 4 |
| Docker smoke | 5 |
| No API changes | Global |

## Placeholder / consistency review

- No TBD steps.
- Route string `connectivity/opc-da` identical in nav, `ROUTE_TO_TAB`, `LEGACY_TAB_TO_ROUTE`, `pickSource` navigate, help.
- `sourcesStatusList` vs `sourcesList` roles explicit.
- `data-tab="connection"` retained for Sources.
