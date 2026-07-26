# Dashboard IA & UX Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the flat 11-tab dashboard with a Kepware-style domain sidebar (Connectivity, Tags, IoT, Historian, Ops, Help), guided Add Source / MQTT / Historian wizards, hash routing, and grouped empty states — without adding backend APIs.

**Architecture:** All work inside `src/OpcBridge.App/DashboardPage.cs` (HTML + CSS + JS constants). Sidebar groups are nav items; `showTab(name)` becomes a router shim that also sets `location.hash`. Wizards are overlay panels that call existing `saveSource` / `saveMqtt` / `saveInflux` + connect functions. No new endpoints; no backend changes; no HMI changes.

**Tech Stack:** .NET 8 (server unchanged), vanilla JS SPA (inline), existing `/api/da/*`, `/api/mqtt/*`, `/api/influx/*` endpoints.

## Global Constraints

- Build gate: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release` → 0 warnings, 0 errors.
- All edits live in worktree `/home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify` on branch `feature/ui-ux-modify`.
- No new backend endpoints. Reuse `/api/da/sources`, `/api/da/servers`, `/api/da/tags`, `/api/mqtt/config|connect|disconnect|status|values`, `/api/influx/config|connect|disconnect|status`, `/api/dashboard`.
- Conventional commits: `feat(dashboard): ...`, `fix(dashboard): ...`, `docs(dashboard): ...`.
- Preserve existing DOM ids that tests assert on (see Task 0 inventory). Do not break `DashboardPageTests.cs` or `HelpContentTests.cs` unless a task explicitly updates them.
- Keep current dark CSS tokens (`--bg`, `--panel`, `--accent`, etc.). No new design system.
- Faceplate stays a modal over Tags → Maps; per-tag MQTT/Influx toggles (`fpMqttEnabled`, `fpInfluxEnabled`) stay on the faceplate.
- `HttpResponseMessage` must use `using var` (C# rule) — not in scope here, but enforced for any new C# edits.
- Default landing page: `#/ops/monitor`.

---

## Task 0: Freeze test-asserted DOM ids

**Files:**
- Read: `tests/OpcBridge.LoadTest/DashboardPageTests.cs`
- Read: `tests/OpcBridge.LoadTest/HelpContentTests.cs`

**Interfaces:**
- Produces: a fixed list of ids/strings that later tasks MUST NOT rename without also updating tests.

- [ ] **Step 1: Collect asserted ids**

From `DashboardPageTests.cs`, the asserted ids/strings (must survive redesign):
- `id="pApps"`, text `Apps`
- `id="fpProvider"` MUST be absent, `Set up links from a tag's faceplate` MUST be absent
- `id="linkConsumerSelect"` absent, `id="linkProviderSelect"` absent
- text `DA Links` present
- `id="linkSourceStatus"` present, `id="linkBrowseTree"` present
- `id="btnClearLinkSelection"`, text `Clear Selection`, text `Delete Saved Link`
- `function clearLinkDraftSelection()`, `state.linkDraft.consumer = null`, `state.linkDraft.provider = null`
- `id="pApps"`, `pApps`, `detectedCount` in script
- `data-tab="influx"`, `id="view-influx"`, `id="fpInfluxEnabled"`, `id="influxUrl"`, `id="influxWritten"`
- `function loadInfluxConfig(`, `function loadInfluxStatus(`, `function saveInflux(`, `function connectInflux(`, `function disconnectInflux(`
- `/api/influx/config`, `influxEnabled: el('fpInfluxEnabled').checked`, `if (name === 'influx')`

From `HelpContentTests.cs`:
- `HelpContent.Markdown` contains `DA Links`, `separate subsystem`, `# InfluxDB (Historical Logging)`, `External InfluxDB 2.x/3.x server required`, `Enable per tag via faceplate Influx checkbox`, `Outage does not stop the bridge`
- `faceplate → Setup → Provider` must NOT appear

- [ ] **Step 2: Record the list in a comment at the top of `DashboardPage.cs` (above `internal static class DashboardPage`)**

Add a region comment listing the frozen ids so every later task sees the contract. This is documentation only; no behavior change.

```csharp
// Test-asserted DOM/JS contract (see tests/OpcBridge.LoadTest/DashboardPageTests.cs, HelpContentTests.cs).
// Do NOT rename without updating tests:
//   data-tab="influx", id="view-influx", id="fpInfluxEnabled", id="influxUrl", id="influxWritten"
//   function loadInfluxConfig/loadInfluxStatus/saveInflux/connectInflux/disconnectInflux
//   /api/influx/config, influxEnabled: el('fpInfluxEnabled').checked, if (name === 'influx')
//   id="pApps", text "Apps", "pApps" in script, "detectedCount" in script
//   text "DA Links", id="linkSourceStatus", id="linkBrowseTree", id="btnClearLinkSelection"
//   text "Clear Selection", text "Delete Saved Link", function clearLinkDraftSelection
//   state.linkDraft.consumer = null, state.linkDraft.provider = null
//   function browseLinkTags(, state.linkDraft, data-action="pick-link-consumer", data-action="pick-link-provider"
```

- [ ] **Step 3: Build to confirm no breakage**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "docs(dashboard): freeze test-asserted DOM/JS contract for redesign"
```

---

## Task 1: Sidebar shell + group nav (no behavior change)

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` lines ~41–49 (CSS), ~401–415 (tabbar markup), ~2228–2245 (`showTab`), ~3511–3513 (init routing)

**Interfaces:**
- Produces: `navigate(route)` function and grouped sidebar; `showTab(name)` stays as a shim calling `navigate` for backward compat.
- Consumes: existing view panels `view-monitor`, `view-connection`, `view-diagnostics`, `view-tags`, `view-links`, `view-logs`, `view-mqtt`, `view-influx`, `view-diagram`, `view-help`, `view-about`.

- [ ] **Step 1: Replace `.tabbar` markup with grouped sidebar**

Replace lines 402–414 (the `<div class="tabbar">…</div>` block containing 11 `tabbtn` buttons) with a grouped sidebar. Keep `data-tab` values identical to today (test contract: `data-tab="influx"` must still exist). Wrap groups in `.nav-group` with a `.nav-group-h` label.

```html
<div class="tabbar">
  <div class="nav-group">
    <div class="nav-group-h">Connectivity</div>
    <button class="tabbtn" data-tab="connection" data-route="connectivity/sources" onclick="navigate('connectivity/sources')">Sources</button>
    <button class="tabbtn" data-tab="diagnostics" data-route="connectivity/diagnostics" onclick="navigate('connectivity/diagnostics')">Diagnostics</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h">Tags</div>
    <button class="tabbtn" data-tab="tags" data-route="tags/maps" onclick="navigate('tags/maps')">Maps</button>
    <button class="tabbtn" data-tab="links" data-route="tags/links" onclick="navigate('tags/links')">DA Links</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h">IoT</div>
    <button class="tabbtn" data-tab="mqtt" data-route="iot/mqtt" onclick="navigate('iot/mqtt')">MQTT</button>
    <button class="tabbtn" data-tab="iot-traffic" data-route="iot/traffic" onclick="navigate('iot/traffic')">Traffic</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h">Historian</div>
    <button class="tabbtn" data-tab="influx" data-route="historian/influx" onclick="navigate('historian/influx')">InfluxDB</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h">Ops</div>
    <button class="tabbtn active" data-tab="monitor" data-route="ops/monitor" onclick="navigate('ops/monitor')">Monitor</button>
    <button class="tabbtn" data-tab="logs" data-route="ops/logs" onclick="navigate('ops/logs')">Logs</button>
    <button class="tabbtn" data-tab="diagram" data-route="ops/diagram" onclick="navigate('ops/diagram')">Diagram</button>
  </div>
  <div class="nav-group">
    <div class="nav-group-h">Help</div>
    <button class="tabbtn" data-tab="help" data-route="help/guide" onclick="navigate('help/guide')">Guide</button>
    <button class="tabbtn" data-tab="about" data-route="help/about" onclick="navigate('help/about')">About</button>
  </div>
</div>
```

- [ ] **Step 2: Add CSS for grouped sidebar**

After line 45 (`.tabbtn.active { … }`), insert group styles. Widen `.tabbar` to 200px.

```css
.tabbar { width: 200px; }
.nav-group { padding: 6px 0; border-bottom: 1px solid var(--border); }
.nav-group:last-child { border-bottom: none; }
.nav-group-h { padding: 8px 16px 4px; font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); }
.nav-group .tabbtn { padding-left: 22px; }
@media (max-width: 600px) { .nav-group { border-bottom: none; } .nav-group-h { display: none; } }
```

- [ ] **Step 3: Add `navigate(route)` router and route→tab map**

In the Script section, before `showTab`, add the route table and router. `navigate` accepts `group/page`, maps to the legacy tab id, calls `showTab`, and sets `location.hash = '#/' + route`.

```javascript
const ROUTE_TO_TAB = {
  'connectivity/sources': 'connection',
  'connectivity/diagnostics': 'diagnostics',
  'tags/maps': 'tags',
  'tags/links': 'links',
  'iot/mqtt': 'mqtt',
  'iot/traffic': 'iot-traffic',
  'historian/influx': 'influx',
  'ops/monitor': 'monitor',
  'ops/logs': 'logs',
  'ops/diagram': 'diagram',
  'help/guide': 'help',
  'help/about': 'about'
};
const DEFAULT_ROUTE = 'ops/monitor';

function navigate(route) {
  const tab = ROUTE_TO_TAB[route] || ROUTE_TO_TAB[DEFAULT_ROUTE];
  showTab(tab, route);
}
```

- [ ] **Step 4: Refactor `showTab(name)` to accept an optional route**

Replace lines 2228–2245 `async function showTab(name) { … }` with a version that sets the active nav item by `data-route` (falling back to `data-tab`), updates the hash to `#/route`, and handles the new `iot-traffic` virtual tab by showing the MQTT view scrolled to Traffic. Keep all existing per-tab load triggers (`logs`, `diagnostics`, `about`, `help`, `mqtt`, `influx`, `links`, `diagram`).

```javascript
async function showTab(name, route) {
  route = route || (Object.keys(ROUTE_TO_TAB).find(r => ROUTE_TO_TAB[r] === name) || DEFAULT_ROUTE);
  const activeTab = name === 'iot-traffic' ? 'mqtt' : name;
  document.querySelectorAll('.tabbtn').forEach(b => b.classList.toggle('active', b.dataset.route === route));
  document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + activeTab));
  if (location.hash !== '#/' + route) history.replaceState(null, '', '#/' + route);
  if (activeTab === 'iot-traffic') {
    const traffic = document.getElementById('mqttTraffic');
    if (traffic) traffic.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
  if (activeTab === 'logs') { state.logsLoaded = false; loadLogs(true).catch(e => el('logMessage').textContent = '✗ ' + e.message); }
  if (activeTab === 'diagnostics') { diagnosticsActive = true; loadDiagnostics(); }
  else { diagnosticsActive = false; }
  if (activeTab === 'about') loadAppInfo().catch(e => el('aboutName').textContent = '✗ ' + e.message);
  if (activeTab === 'help') loadHelp().catch(e => el('helpContent').innerHTML = '<span class="msg bad">✗ ' + esc(e.message) + '</span>');
  if (activeTab === 'mqtt') { await loadMqtt(); await loadMqttValues(); }
  if (activeTab === 'influx') { await loadInflux(); }
  if (activeTab === 'links') loadDaLinks().catch(e => el('linksMessage').textContent = '✗ ' + e.message);
  if (activeTab === 'diagram') {
    state.diagramLoaded = true;
    await Promise.all([loadSources(), loadMappings(), loadDaLinks(), loadMqtt().catch(() => {})]);
    renderDiagram();
  }
}
```

- [ ] **Step 5: Replace init routing at the bottom of the script**

Replace lines 3512–3513:
```javascript
const initTab = location.hash.slice(1);
if (['monitor','connection','diagnostics','tags','links','logs','mqtt','influx','help','about'].includes(initTab)) showTab(initTab);
```
with hash-route-aware init:
```javascript
const initHash = location.hash.replace(/^#\/?/, '');
const initRoute = Object.prototype.hasOwnProperty.call(ROUTE_TO_TAB, initHash) ? initHash
  : (initHash && Object.keys(ROUTE_TO_TAB).find(r => ROUTE_TO_TAB[r] === initHash) ? Object.keys(ROUTE_TO_TAB).find(r => ROUTE_TO_TAB[r] === initHash) : DEFAULT_ROUTE);
navigate(initRoute);
```

- [ ] **Step 6: Build**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Run existing tests**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test -c Release --filter "FullyQualifiedName~DashboardPageTests|FullyQualifiedName~HelpContentTests"`
Expected: all pass (`data-tab="influx"` still present, `view-influx` still present, all faceplate/link ids untouched).

- [ ] **Step 8: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): grouped sidebar with hash routing (IA shell)"
```

---

## Task 2: Add OPC DA Source wizard

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` — add wizard overlay markup near the Sources view, add `openAddSourceWizard`/`nextSourceStep`/`closeAddSourceWizard` functions that drive the existing `newSource()`/`browseServers()`/`saveSource()` path.

**Interfaces:**
- Consumes: existing `newSource()` (line ~3080), `browseServers()` (line ~3097), `pickServer()` (line ~3116), `saveSource()` (line ~3015), `pickSource()` (line ~3004).
- Produces: `openAddSourceWizard()`, a modal `#addSourceWizard` that walks 5 steps and calls `saveSource()` on the last.

- [ ] **Step 1: Add wizard overlay markup**

Insert before `</div>` closing `view-connection` (the Sources page). A modal with 5 step panes, only one visible at a time.

```html
<div class="modal-overlay" id="addSourceWizard" style="display:none" onclick="if(event.target===this)closeAddSourceWizard()">
  <div class="modal wizard" role="dialog" aria-modal="true" aria-labelledby="addSourceWizardTitle">
    <div class="modal-head">
      <div class="modal-title" id="addSourceWizardTitle">Add OPC DA Source</div>
      <button class="modal-close" type="button" onclick="closeAddSourceWizard()">&times;</button>
    </div>
    <div class="wizard-steps">
      <span class="wizard-step" data-step="1">1. Identity</span>
      <span class="wizard-step" data-step="2">2. Server</span>
      <span class="wizard-step" data-step="3">3. Credentials</span>
      <span class="wizard-step" data-step="4">4. Defaults</span>
      <span class="wizard-step" data-step="5">5. Review</span>
    </div>
    <div class="wizard-body">
      <div class="wizard-pane active" data-pane="1">
        <div class="field"><label class="fl">Source ID</label><input type="text" id="wzSourceId" placeholder="server-a"></div>
        <div class="field"><label class="fl">Display Name</label><input type="text" id="wzDisplayName" placeholder="(optional)"></div>
        <div class="hint">Unique key with no spaces. Used in UA Node IDs (ns=2;s={sourceId}/...).</div>
      </div>
      <div class="wizard-pane" data-pane="2">
        <div class="field"><label class="fl">Host</label><input type="text" id="wzHost" placeholder="localhost"></div>
        <div class="field"><label class="fl">ProgID / CLSID</label><input type="text" id="wzProgId" placeholder="Kepware.KEPServerEX.V6"></div>
        <button class="btn ghost" type="button" onclick="wzBrowseServers()">Browse Servers</button>
        <span class="msg" id="wzMsgServers"></span>
        <div class="list" id="wzListServers" style="max-height:180px"></div>
      </div>
      <div class="wizard-pane" data-pane="3">
        <div class="field"><label class="fl">Domain</label><input type="text" id="wzDomain" placeholder="(optional)"></div>
        <div class="field"><label class="fl">Username</label><input type="text" id="wzUser" placeholder="(optional)"></div>
        <div class="field"><label class="fl">Password</label><input type="password" id="wzPass"></div>
        <div class="hint">Only required for remote DCOM or servers in another user's profile.</div>
      </div>
      <div class="wizard-pane" data-pane="4">
        <div class="field"><label class="fl">Update Rate</label><select id="wzUpdateRate"><option value="100">100 ms</option><option value="250">250 ms</option><option value="500">500 ms</option><option value="1000" selected>1 s</option><option value="2000">2 s</option><option value="5000">5 s</option><option value="10000">10 s</option></select></div>
        <div class="field"><label class="fl">Subscriptions</label><input type="checkbox" id="wzSubs" checked> <span class="msg">Use IOPCDataCallback (recommended)</span></div>
      </div>
      <div class="wizard-pane" data-pane="5">
        <div class="wizard-summary" id="wzSummary"></div>
        <div class="hint">Click Finish to save. You can map tags next.</div>
      </div>
    </div>
    <div class="wizard-foot">
      <button class="btn ghost" type="button" onclick="closeAddSourceWizard()">Cancel</button>
      <button class="btn ghost" type="button" id="wzBack" onclick="wzStep(-1)">Back</button>
      <button class="btn" type="button" id="wzNext" onclick="wzStep(1)">Next</button>
      <button class="btn" type="button" id="wzFinish" style="display:none" onclick="wzFinish()">Finish &amp; Save</button>
    </div>
  </div>
</div>
```

- [ ] **Step 2: Add wizard CSS**

Append near the existing modal CSS (search for `.modal-overlay` / `.faceplate` styles). Use existing tokens.

```css
.modal.wizard { width: 480px; max-width: 94vw; }
.wizard-steps { display: flex; gap: 6px; padding: 10px 14px; border-bottom: 1px solid var(--border); flex-wrap: wrap; }
.wizard-step { font-size: 11px; color: var(--muted); padding: 3px 8px; border-radius: 4px; }
.wizard-step.active { color: var(--accent); background: rgba(56,189,248,.12); }
.wizard-step.done { color: var(--good); }
.wizard-body { padding: 14px; max-height: 60vh; overflow-y: auto; }
.wizard-pane { display: none; }
.wizard-pane.active { display: block; }
.wizard-foot { display: flex; justify-content: flex-end; gap: 8px; padding: 10px 14px; border-top: 1px solid var(--border); }
.wizard-summary { font-size: 12px; line-height: 1.6; }
.wizard-summary b { color: var(--text); }
```

- [ ] **Step 3: Add wizard JS**

Add after the existing `newSource()` function (around line 3096). The wizard copies its field values into the legacy `cfg*` inputs (so `saveSource()` works unchanged), then calls `saveSource()`.

```javascript
let wzCurrentStep = 1;
const WZ_STEPS = 5;

function openAddSourceWizard() {
  wzCurrentStep = 1;
  ['wzSourceId','wzDisplayName','wzHost','wzProgId','wzDomain','wzUser','wzPass'].forEach(id => el(id).value = '');
  el('wzHost').value = 'localhost';
  el('wzSubs').checked = true;
  el('wzUpdateRate').value = '1000';
  el('wzListServers').innerHTML = '';
  el('wzMsgServers').textContent = '';
  el('addSourceWizard').style.display = '';
  wzRender();
}
function closeAddSourceWizard() { el('addSourceWizard').style.display = 'none'; }
function wzRender() {
  document.querySelectorAll('.wizard-pane').forEach(p => p.classList.toggle('active', Number(p.dataset.pane) === wzCurrentStep));
  document.querySelectorAll('.wizard-step').forEach(s => {
    const n = Number(s.dataset.step);
    s.classList.toggle('active', n === wzCurrentStep);
    s.classList.toggle('done', n < wzCurrentStep);
  });
  el('wzBack').style.display = wzCurrentStep > 1 ? '' : 'none';
  el('wzNext').style.display = wzCurrentStep < WZ_STEPS ? '' : 'none';
  el('wzFinish').style.display = wzCurrentStep === WZ_STEPS ? '' : 'none';
  if (wzCurrentStep === 5) wzBuildSummary();
}
function wzStep(delta) {
  const next = wzCurrentStep + delta;
  if (next < 1 || next > WZ_STEPS) return;
  if (delta > 0 && !wzValidate(wzCurrentStep)) return;
  wzCurrentStep = next;
  wzRender();
}
function wzValidate(step) {
  if (step === 1) {
    const id = el('wzSourceId').value.trim();
    if (!id) { alert('Source ID is required.'); return false; }
    if (/\s/.test(id)) { alert('Source ID must not contain spaces.'); return false; }
    if (state.sources.some(s => s.sourceId === id)) { alert('Source ID already exists.'); return false; }
  }
  if (step === 2 && !el('wzProgId').value.trim()) { alert('ProgID / CLSID is required.'); return false; }
  return true;
}
async function wzBrowseServers() {
  const host = el('wzHost').value.trim() || 'localhost';
  el('wzMsgServers').textContent = 'Scanning…';
  const body = { host: host === 'localhost' ? null : host };
  try {
    const r = await fetch('/api/da/servers', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), cache: 'no-store' });
    const p = await r.json();
    if (p.error) throw new Error(p.error);
    const servers = p.servers || [];
    el('wzListServers').innerHTML = servers.length ? servers.map(s => {
      const prog = s.progId || s.ProgId;
      const desc = s.description || s.Description || prog;
      return `<div class="li"><div style="flex:1"><div class="n">${esc(desc)}</div><div class="p">${esc(prog)}</div></div><button class="btn ghost" data-action="wz-pick-server" data-prog-id="${attr(prog)}" data-host="${attr(host)}">Use</button></div>`;
    }).join('') : '<span class="msg">No servers found.</span>';
    el('wzMsgServers').textContent = servers.length + ' servers';
  } catch (e) { el('wzMsgServers').textContent = '✗ ' + e.message; }
}
function wzPickServer(progId, host) {
  el('wzProgId').value = progId;
  el('wzHost').value = host;
  el('wzMsgServers').textContent = 'Selected ' + progId;
}
function wzBuildSummary() {
  el('wzSummary').innerHTML =
    `<b>Source ID:</b> ${esc(el('wzSourceId').value)}<br>` +
    `<b>Display Name:</b> ${esc(el('wzDisplayName').value || '—')}<br>` +
    `<b>Host:</b> ${esc(el('wzHost').value || 'localhost')}<br>` +
    `<b>ProgID:</b> ${esc(el('wzProgId').value)}<br>` +
    `<b>Credentials:</b> ${el('wzUser').value ? el('wzDomain').value + '\\' + el('wzUser').value : 'none'}<br>` +
    `<b>Update Rate:</b> ${el('wzUpdateRate').value} ms<br>` +
    `<b>Subscriptions:</b> ${el('wzSubs').checked ? 'on' : 'off'}`;
}
async function wzFinish() {
  el('cfgSourceId').value = el('wzSourceId').value.trim();
  el('cfgDisplayName').value = el('wzDisplayName').value.trim();
  el('cfgProgId').value = el('wzProgId').value.trim();
  el('cfgHost').value = el('wzHost').value.trim() || 'localhost';
  el('cfgUser').value = el('wzUser').value.trim();
  el('cfgPass').value = el('wzPass').value;
  el('cfgDomain').value = el('wzDomain').value.trim();
  state.editingNewSource = true;
  try {
    await saveSource();
    closeAddSourceWizard();
    if (confirm('Source saved. Map tags now?')) navigate('tags/maps');
  } catch (e) {
    el('cfgMessage').textContent = '✗ ' + e.message;
  }
}
```

- [ ] **Step 4: Wire dynamic buttons for `wz-pick-server`**

In `bindDynamicButtons()` (the existing delegated click handler — find `data-action="pick-link-consumer"` to locate it), add a branch:

```javascript
if (btn = event.target.closest('button[data-action="wz-pick-server"]')) {
  wzPickServer(btn.dataset.progId, btn.dataset.host);
  return;
}
```

Adjust the variable name to match the existing handler's pattern (it likely uses `const btn = event.target.closest(...)` per branch).

- [ ] **Step 5: Add a primary "+ Add Source" button on the Sources page**

In the `view-connection` markup, in the `box-h` "Server Connection" header (line ~522), append a primary CTA:

```html
<button class="btn" type="button" onclick="openAddSourceWizard()" style="margin-left:auto">+ Add Source</button>
```

- [ ] **Step 6: Build + test**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release && dotnet test -c Release`
Expected: 0 warnings, 0 errors; all existing tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): Add OPC DA Source wizard over existing saveSource path"
```

---

## Task 3: MQTT setup wizard

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` — add `#mqttWizard` overlay and `openMqttWizard`/`wzMqtt*` functions that feed the existing `mqtt*` form fields and call `saveMqtt()` then optionally `connectMqtt()`.

**Interfaces:**
- Consumes: existing `loadMqtt()` (loads `mqtt*` fields), `saveMqtt()` (line ~2803), `connectMqtt()` (line ~2820).
- Produces: `openMqttWizard()`, modal `#mqttWizard`, 3 steps.

- [ ] **Step 1: Add MQTT wizard markup**

Insert before the closing `</div>` of `view-mqtt`:

```html
<div class="modal-overlay" id="mqttWizard" style="display:none" onclick="if(event.target===this)closeMqttWizard()">
  <div class="modal wizard" role="dialog" aria-modal="true" aria-labelledby="mqttWizardTitle">
    <div class="modal-head">
      <div class="modal-title" id="mqttWizardTitle">Connect MQTT Broker</div>
      <button class="modal-close" type="button" onclick="closeMqttWizard()">&times;</button>
    </div>
    <div class="wizard-steps">
      <span class="wizard-step" data-step="1">1. Broker</span>
      <span class="wizard-step" data-step="2">2. Auth &amp; Topics</span>
      <span class="wizard-step" data-step="3">3. Save &amp; Connect</span>
    </div>
    <div class="wizard-body">
      <div class="wizard-pane active" data-pane="1">
        <div class="field"><label class="fl">Broker URL</label><input type="text" id="wzMqttUrl" placeholder="tcp://localhost:1883"></div>
        <div class="field"><label class="fl">Client ID</label><input type="text" id="wzMqttClientId" placeholder="OpcDaToUaBridge"></div>
        <div class="field"><label class="fl">Auto-connect</label><input type="checkbox" id="wzMqttAuto" checked></div>
      </div>
      <div class="wizard-pane" data-pane="2">
        <div class="field"><label class="fl">Username</label><input type="text" id="wzMqttUser" placeholder="(optional)"></div>
        <div class="field"><label class="fl">Password</label><input type="password" id="wzMqttPass"></div>
        <div class="field"><label class="fl">TLS</label><input type="checkbox" id="wzMqttTls"></div>
        <div class="field"><label class="fl">Topic Prefix</label><input type="text" id="wzMqttPrefix" placeholder="bridge/tags"></div>
        <div class="field"><label class="fl">Payload Fields</label><select id="wzMqttFields"><option>Value, Timestamp</option><option>Value, Timestamp, Quality</option><option>Value, Timestamp, Quality, SourceId, ItemId</option><option>Value, Timestamp, SourceId, ItemId, DisplayName, DataType</option></select></div>
      </div>
      <div class="wizard-pane" data-pane="3">
        <div class="wizard-summary" id="wzMqttSummary"></div>
        <div class="field"><label class="fl">Connect now</label><input type="checkbox" id="wzMqttConnectNow" checked></div>
      </div>
    </div>
    <div class="wizard-foot">
      <button class="btn ghost" type="button" onclick="closeMqttWizard()">Cancel</button>
      <button class="btn ghost" type="button" id="wzMqttBack" onclick="wzMqttStep(-1)">Back</button>
      <button class="btn" type="button" id="wzMqttNext" onclick="wzMqttStep(1)">Next</button>
      <button class="btn" type="button" id="wzMqttFinish" style="display:none" onclick="wzMqttFinish()">Finish</button>
    </div>
  </div>
</div>
```

- [ ] **Step 2: Add MQTT wizard JS**

Add after `connectMqtt()` (line ~2820). The wizard writes its fields into the real `mqtt*` inputs, then calls `saveMqtt()` and optionally `connectMqtt()`.

```javascript
let wzMqttStepCur = 1;
const WZ_MQTT_STEPS = 3;

async function openMqttWizard() {
  wzMqttStepCur = 1;
  await loadMqtt();
  el('wzMqttUrl').value = el('mqttBrokerUrl').value || 'tcp://localhost:1883';
  el('wzMqttClientId').value = el('mqttClientId').value || 'OpcDaToUaBridge';
  el('wzMqttAuto').checked = el('mqttEnabled').checked;
  el('wzMqttUser').value = el('mqttUser').value;
  el('wzMqttPass').value = el('mqttPass').value;
  el('wzMqttTls').checked = el('mqttTls').checked;
  el('wzMqttPrefix').value = el('mqttPrefix').value || 'bridge/tags';
  el('wzMqttFields').value = el('mqttFields').value;
  el('wzMqttConnectNow').checked = true;
  el('mqttWizard').style.display = '';
  wzMqttRender();
}
function closeMqttWizard() { el('mqttWizard').style.display = 'none'; }
function wzMqttRender() {
  document.querySelectorAll('#mqttWizard .wizard-pane').forEach(p => p.classList.toggle('active', Number(p.dataset.pane) === wzMqttStepCur));
  document.querySelectorAll('#mqttWizard .wizard-step').forEach(s => {
    const n = Number(s.dataset.step);
    s.classList.toggle('active', n === wzMqttStepCur);
    s.classList.toggle('done', n < wzMqttStepCur);
  });
  el('wzMqttBack').style.display = wzMqttStepCur > 1 ? '' : 'none';
  el('wzMqttNext').style.display = wzMqttStepCur < WZ_MQTT_STEPS ? '' : 'none';
  el('wzMqttFinish').style.display = wzMqttStepCur === WZ_MQTT_STEPS ? '' : 'none';
  if (wzMqttStepCur === 3) {
    el('wzMqttSummary').innerHTML =
      `<b>Broker:</b> ${esc(el('wzMqttUrl').value)}<br>` +
      `<b>Client ID:</b> ${esc(el('wzMqttClientId').value)}<br>` +
      `<b>Auth:</b> ${el('wzMqttUser').value ? 'yes' : 'none'}<br>` +
      `<b>TLS:</b> ${el('wzMqttTls').checked ? 'on' : 'off'}<br>` +
      `<b>Topic Prefix:</b> ${esc(el('wzMqttPrefix').value)}<br>` +
      `<b>Auto-connect:</b> ${el('wzMqttAuto').checked ? 'on' : 'off'}`;
  }
}
function wzMqttStep(delta) {
  const next = wzMqttStepCur + delta;
  if (next < 1 || next > WZ_MQTT_STEPS) return;
  if (delta > 0 && !wzMqttValidate(wzMqttStepCur)) return;
  wzMqttStepCur = next;
  wzMqttRender();
}
function wzMqttValidate(step) {
  if (step === 1 && !el('wzMqttUrl').value.trim()) { alert('Broker URL is required.'); return false; }
  return true;
}
async function wzMqttFinish() {
  el('mqttBrokerUrl').value = el('wzMqttUrl').value.trim();
  el('mqttClientId').value = el('wzMqttClientId').value.trim() || 'OpcDaToUaBridge';
  el('mqttEnabled').checked = el('wzMqttAuto').checked;
  el('mqttUser').value = el('wzMqttUser').value;
  el('mqttPass').value = el('wzMqttPass').value;
  el('mqttTls').checked = el('wzMqttTls').checked;
  el('mqttPrefix').value = el('wzMqttPrefix').value.trim() || 'bridge/tags';
  el('mqttFields').value = el('wzMqttFields').value;
  try {
    await saveMqtt();
    if (el('wzMqttConnectNow').checked) await connectMqtt();
    closeMqttWizard();
  } catch (e) {
    el('mqttMessage').textContent = '✗ ' + e.message;
  }
}
```

- [ ] **Step 3: Add "Setup Wizard" entry on the MQTT page**

In the `view-mqtt` broker box header (line ~782), append:

```html
<button class="btn" type="button" onclick="openMqttWizard()" style="margin-left:auto">Setup Wizard</button>
```

- [ ] **Step 4: Build + test**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release && dotnet test -c Release`
Expected: 0 warnings, 0 errors; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): MQTT setup wizard over existing saveMqtt path"
```

---

## Task 4: Historian (InfluxDB) setup wizard

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` — add `#influxWizard` overlay and `openInfluxWizard`/`wzInflux*` functions that feed the existing `influx*` fields and call `saveInflux()` then optionally `connectInflux()`.

**Interfaces:**
- Consumes: existing `loadInflux()` (loads `influx*` fields), `saveInflux()` (line ~2887), `connectInflux()` (line ~2903).
- Produces: `openInfluxWizard()`, modal `#influxWizard`, 3 steps.

- [ ] **Step 1: Add Influx wizard markup**

Insert before the closing `</div>` of `view-influx`:

```html
<div class="modal-overlay" id="influxWizard" style="display:none" onclick="if(event.target===this)closeInfluxWizard()">
  <div class="modal wizard" role="dialog" aria-modal="true" aria-labelledby="influxWizardTitle">
    <div class="modal-head">
      <div class="modal-title" id="influxWizardTitle">Enable Historian (InfluxDB)</div>
      <button class="modal-close" type="button" onclick="closeInfluxWizard()">&times;</button>
    </div>
    <div class="wizard-steps">
      <span class="wizard-step" data-step="1">1. Server</span>
      <span class="wizard-step" data-step="2">2. Auth</span>
      <span class="wizard-step" data-step="3">3. Save &amp; Connect</span>
    </div>
    <div class="wizard-body">
      <div class="wizard-pane active" data-pane="1">
        <div class="field"><label class="fl">URL</label><input type="text" id="wzInfluxUrl" placeholder="http://localhost:8086"></div>
        <div class="field"><label class="fl">Org</label><input type="text" id="wzInfluxOrg" placeholder="my-org"></div>
        <div class="field"><label class="fl">Bucket</label><input type="text" id="wzInfluxBucket" placeholder="opc"></div>
      </div>
      <div class="wizard-pane" data-pane="2">
        <div class="field"><label class="fl">Token</label><input type="password" id="wzInfluxToken"></div>
        <div class="hint">API token with write access to the bucket. Stored in influx.json.</div>
      </div>
      <div class="wizard-pane" data-pane="3">
        <div class="wizard-summary" id="wzInfluxSummary"></div>
        <div class="field"><label class="fl">Auto-connect</label><input type="checkbox" id="wzInfluxAuto" checked></div>
        <div class="field"><label class="fl">Connect now</label><input type="checkbox" id="wzInfluxConnectNow" checked></div>
      </div>
    </div>
    <div class="wizard-foot">
      <button class="btn ghost" type="button" onclick="closeInfluxWizard()">Cancel</button>
      <button class="btn ghost" type="button" id="wzInfluxBack" onclick="wzInfluxStep(-1)">Back</button>
      <button class="btn" type="button" id="wzInfluxNext" onclick="wzInfluxStep(1)">Next</button>
      <button class="btn" type="button" id="wzInfluxFinish" style="display:none" onclick="wzInfluxFinish()">Finish</button>
    </div>
  </div>
</div>
```

- [ ] **Step 2: Add Influx wizard JS**

Add after `connectInflux()` (line ~2903):

```javascript
let wzInfluxStepCur = 1;
const WZ_INFLUX_STEPS = 3;

async function openInfluxWizard() {
  wzInfluxStepCur = 1;
  await loadInflux();
  el('wzInfluxUrl').value = el('influxUrl').value || 'http://localhost:8086';
  el('wzInfluxOrg').value = el('influxOrg').value;
  el('wzInfluxBucket').value = el('influxBucket').value;
  el('wzInfluxToken').value = el('influxToken').value;
  el('wzInfluxAuto').checked = el('influxEnabled').checked;
  el('wzInfluxConnectNow').checked = true;
  el('influxWizard').style.display = '';
  wzInfluxRender();
}
function closeInfluxWizard() { el('influxWizard').style.display = 'none'; }
function wzInfluxRender() {
  document.querySelectorAll('#influxWizard .wizard-pane').forEach(p => p.classList.toggle('active', Number(p.dataset.pane) === wzInfluxStepCur));
  document.querySelectorAll('#influxWizard .wizard-step').forEach(s => {
    const n = Number(s.dataset.step);
    s.classList.toggle('active', n === wzInfluxStepCur);
    s.classList.toggle('done', n < wzInfluxStepCur);
  });
  el('wzInfluxBack').style.display = wzInfluxStepCur > 1 ? '' : 'none';
  el('wzInfluxNext').style.display = wzInfluxStepCur < WZ_INFLUX_STEPS ? '' : 'none';
  el('wzInfluxFinish').style.display = wzInfluxStepCur === WZ_INFLUX_STEPS ? '' : 'none';
  if (wzInfluxStepCur === 3) {
    el('wzInfluxSummary').innerHTML =
      `<b>URL:</b> ${esc(el('wzInfluxUrl').value)}<br>` +
      `<b>Org:</b> ${esc(el('wzInfluxOrg').value || '—')}<br>` +
      `<b>Bucket:</b> ${esc(el('wzInfluxBucket').value || '—')}<br>` +
      `<b>Token:</b> ${el('wzInfluxToken').value ? 'set' : '—'}<br>` +
      `<b>Auto-connect:</b> ${el('wzInfluxAuto').checked ? 'on' : 'off'}`;
  }
}
function wzInfluxStep(delta) {
  const next = wzInfluxStepCur + delta;
  if (next < 1 || next > WZ_INFLUX_STEPS) return;
  if (delta > 0 && !wzInfluxValidate(wzInfluxStepCur)) return;
  wzInfluxStepCur = next;
  wzInfluxRender();
}
function wzInfluxValidate(step) {
  if (step === 1 && !el('wzInfluxUrl').value.trim()) { alert('URL is required.'); return false; }
  if (step === 2 && !el('wzInfluxToken').value) { alert('Token is required.'); return false; }
  return true;
}
async function wzInfluxFinish() {
  el('influxUrl').value = el('wzInfluxUrl').value.trim();
  el('influxOrg').value = el('wzInfluxOrg').value.trim();
  el('influxBucket').value = el('wzInfluxBucket').value.trim();
  el('influxToken').value = el('wzInfluxToken').value;
  el('influxEnabled').checked = el('wzInfluxAuto').checked;
  try {
    await saveInflux();
    if (el('wzInfluxConnectNow').checked) await connectInflux();
    closeInfluxWizard();
  } catch (e) {
    el('influxMessage').textContent = '✗ ' + e.message;
  }
}
```

- [ ] **Step 3: Add "Setup Wizard" entry on the Historian page**

In the `view-influx` box header (line ~846), append. Also rename the box-h label from `InfluxDB` to `Historian` with a secondary subtitle:

```html
<div class="box-h">Historian <span class="msg" style="font-weight:400;text-transform:none;letter-spacing:0">InfluxDB 2.x/3.x</span> <span class="info" data-tip="…existing tip…">i</span>
  <button class="btn" type="button" onclick="openInfluxWizard()" style="margin-left:auto">Setup Wizard</button>
</div>
```

Keep the existing `data-tip` text verbatim.

- [ ] **Step 4: Build + test**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release && dotnet test -c Release`
Expected: 0 warnings, 0 errors; `data-tab="influx"` and `id="view-influx"` still present; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): Historian (InfluxDB) setup wizard"
```

---

## Task 5: Empty-state banners + first-run hints

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` — add banner containers to Monitor and Maps views; render logic in the existing `renderSources()` / `renderMappings()` / `loadMqttStatus()` / `loadInfluxStatus()` paths.

**Interfaces:**
- Consumes: `state.sources`, mapping count, MQTT status, Influx status.
- Produces: `#bannerNoSources`, `#bannerNoMappings`, `#hintMqtt`, `#hintInflux`.

- [ ] **Step 1: Add banner containers**

At the top of `view-monitor` (line ~417, before `.alarm-bar`), insert:

```html
<div class="first-run-banner" id="bannerNoSources" style="display:none"></div>
<div class="first-run-banner" id="bannerNoMappings" style="display:none"></div>
```

At the top of `view-tags`, insert:

```html
<div class="first-run-banner" id="bannerTagsNoSources" style="display:none"></div>
```

At the top of `view-mqtt` and `view-influx`, insert respectively:

```html
<div class="first-run-banner" id="hintMqtt" style="display:none"></div>
<div class="first-run-banner" id="hintInflux" style="display:none"></div>
```

- [ ] **Step 2: Add banner CSS**

Append near `.alarm-bar` styles:

```css
.first-run-banner { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-radius: 7px; margin-bottom: 12px; font-size: 12px; background: rgba(56,189,248,.08); border: 1px solid rgba(56,189,248,.3); color: var(--text); }
.first-run-banner button { margin-left: auto; }
```

- [ ] **Step 3: Render banners from existing loaders**

In `renderSources()` (line ~2271), after setting `el('pSources').textContent`, add:

```javascript
const noSources = state.sources.length === 0;
const bannerNo = el('bannerNoSources');
if (bannerNo) bannerNo.style.display = noSources ? '' : 'none';
if (bannerNo && noSources) bannerNo.innerHTML = 'No OPC DA sources configured. <button class="btn" type="button" onclick="navigate(\'connectivity/sources\')">Add Source</button>';
const bannerTags = el('bannerTagsNoSources');
if (bannerTags) bannerTags.style.display = noSources ? '' : 'none';
if (bannerTags && noSources) bannerTags.innerHTML = 'No sources yet. <button class="btn" type="button" onclick="navigate(\'connectivity/sources\')">Add Source</button>';
```

After the mappings render count is set (find where `el('mapCount')` is updated in `renderMappings()`), add:

```javascript
const noMappings = state.mappings.length === 0;
const bannerNoMap = el('bannerNoMappings');
if (bannerNoMap) {
  bannerNoMap.style.display = (noMappings && state.sources.length > 0) ? '' : 'none';
  if (noMappings && state.sources.length > 0) bannerNoMap.innerHTML = 'No tags mapped yet. <button class="btn" type="button" onclick="navigate(\'tags/maps\')">Map Tags</button>';
}
```

In `loadMqttStatus()` (find the function), after status is read, add (soft hint only):

```javascript
const hintMqtt = el('hintMqtt');
if (hintMqtt) {
  const off = !state.mqttConfigured || state.mqttState === 'Disconnected';
  hintMqtt.style.display = (off && state.mappings.some(m => m.mqttEnabled)) ? '' : 'none';
  if (off && state.mappings.some(m => m.mqttEnabled)) hintMqtt.innerHTML = 'MQTT tags exist but broker is disconnected.';
}
```

In `loadInfluxStatus()` (find the function), after status is read, add (soft hint only):

```javascript
const hintInflux = el('hintInflux');
if (hintInflux) {
  const off = !state.influxConfigured || state.influxState === 'Disconnected';
  hintInflux.style.display = off ? '' : 'none';
  if (off) hintInflux.innerHTML = 'Historian (InfluxDB) not configured. <button class="btn" type="button" onclick="navigate(\'historian/influx\')">Configure</button>';
}
```

If `state.mqttConfigured` / `state.influxConfigured` / `state.mqttState` / `state.influxState` do not exist, derive from the loaded config/status objects already in those functions — add the boolean to `state` when config loads (in `loadMqtt()` / `loadInflux()`).

- [ ] **Step 4: Build + test**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release && dotnet test -c Release`
Expected: 0 warnings, 0 errors; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): first-run banners and soft IoT/Historian hints"
```

---

## Task 6: Help content IA update

**Files:**
- Modify: `src/OpcBridge.App/HelpContent.cs` — update the "Dashboard Tabs" section to describe the new grouped nav.
- Modify: `tests/OpcBridge.LoadTest/HelpContentTests.cs` — add assertions for the new group names; keep existing `DA Links`, `# InfluxDB (Historical Logging)`, etc.

**Interfaces:**
- Produces: `HelpContent.Markdown` referencing Connectivity, Tags, IoT, Historian, Ops groups.

- [ ] **Step 1: Update HelpContent "Dashboard Tabs" section**

Find the section describing tabs (around line 76 in HelpContent.cs). Replace the tab list with the new grouped structure:

```markdown
## Dashboard Navigation

The sidebar groups pages by job:

- **Connectivity** — Sources (add/edit OPC DA sources), Diagnostics (DA health, time sync)
- **Tags** — Maps (browse DA, map to UA, faceplate), DA Links (DA→DA forwarding)
- **IoT** — MQTT (broker config), Traffic (publish/subscribe monitor)
- **Historian** — InfluxDB (config, write status, per-tag enable via faceplate)
- **Ops** — Monitor (live values, status), Logs, Diagram
- **Help** — Guide, About

Use **Connectivity → Sources → + Add Source** for the guided setup wizard.
Use **IoT → MQTT → Setup Wizard** and **Historian → InfluxDB → Setup Wizard** for first-time broker/historian setup.
```

Keep all existing InfluxDB section text (`# InfluxDB (Historical Logging)`, `External InfluxDB 2.x/3.x server required`, `Enable per tag via faceplate Influx checkbox`, `Outage does not stop the bridge`) unchanged.

Keep `DA Links` and `separate subsystem` mentions intact.

- [ ] **Step 2: Add help test assertions**

In `HelpContentTests.cs`, add a new test:

```csharp
[Fact]
public void HelpText_DescribesGroupedNavigation()
{
    Assert.Contains("## Dashboard Navigation", HelpContent.Markdown);
    Assert.Contains("Connectivity", HelpContent.Markdown);
    Assert.Contains("Historian", HelpContent.Markdown);
    Assert.Contains("IoT", HelpContent.Markdown);
    Assert.Contains("Setup Wizard", HelpContent.Markdown);
}
```

- [ ] **Step 3: Build + test**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release && dotnet test -c Release`
Expected: 0 warnings, 0 errors; all tests including new one pass.

- [ ] **Step 4: Commit**

```bash
git add src/OpcBridge.App/HelpContent.cs tests/OpcBridge.LoadTest/HelpContentTests.cs
git commit -m "docs(dashboard): update help content for grouped navigation"
```

---

## Task 7: Legacy hash redirect + final verification

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` — init routing to redirect old `#mqtt` / `#influx` / `#tags` etc. to new `#/group/page`.

**Interfaces:**
- Produces: legacy hash compat in the init routing block.

- [ ] **Step 1: Add legacy hash redirect table**

In the init block (replacing what Task 1 wrote), add a legacy map and resolve through it:

```javascript
const LEGACY_TAB_TO_ROUTE = {
  monitor: 'ops/monitor',
  connection: 'connectivity/sources',
  diagnostics: 'connectivity/diagnostics',
  tags: 'tags/maps',
  links: 'tags/links',
  logs: 'ops/logs',
  mqtt: 'iot/mqtt',
  influx: 'historian/influx',
  diagram: 'ops/diagram',
  help: 'help/guide',
  about: 'help/about'
};
const initHashRaw = location.hash.replace(/^#\/?/, '');
let initRoute = Object.prototype.hasOwnProperty.call(ROUTE_TO_TAB, initHashRaw) ? initHashRaw
  : (LEGACY_TAB_TO_ROUTE[initHashRaw] || DEFAULT_ROUTE);
navigate(initRoute);
```

- [ ] **Step 2: Build + full test**

Run: `docker run --rm -v /home/autoinst578/OpcDaToUaBridge/.worktrees/feature-ui-ux-modify:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release && dotnet test -c Release`
Expected: 0 warnings, 0 errors; all tests pass.

- [ ] **Step 3: Smoke checklist (manual, document in commit body)**

Verify by reading the rendered HTML (the `FullHtml` constant) for:
- Sidebar contains groups: Connectivity, Tags, IoT, Historian, Ops, Help.
- `data-tab="influx"` and `id="view-influx"` still present.
- `#addSourceWizard`, `#mqttWizard`, `#influxWizard` modals exist.
- `navigate('ops/monitor')` is the default route.
- Faceplate still contains `fpMqttEnabled` and `fpInfluxEnabled`.

- [ ] **Step 4: Commit + push**

```bash
git add src/OpcBridge.App/DashboardPage.cs
git commit -m "feat(dashboard): legacy hash redirect + final IA verification"
git push -u origin feature/ui-ux-modify
```

---

## Self-Review

**Spec coverage:**
- §1 Goal (no 11 tabs, MQTT→IoT, Influx→Historian, wizards, linear setup) → Tasks 1–4.
- §2 Users/constraints (both audiences, sidebar shell) → Task 1.
- §4 IA (sidebar, old→new map, deep links) → Task 1 + Task 7.
- §5 Wizards (Source, MQTT, Historian, shared UX, non-wizard exclusions) → Tasks 2–4.
- §6 Pages & empty states (banners, soft hints) → Task 5.
- §7 Routing (hash routes, default, legacy redirect) → Task 1 + Task 7.
- §8 Implementation shape (phases, reuse APIs, verification) → all tasks.
- Help IA update → Task 6.

**Placeholder scan:** No TBD/TODO; every code step shows concrete code or exact edit location. Step 3 of Task 5 says "find the function" — acceptable because the function names (`loadMqttStatus`, `loadInfluxStatus`, `renderMappings`) are exact and searchable; the implementer reads the function then inserts the snippet at the status-assignment point.

**Type consistency:** Route strings (`ops/monitor`, `iot/mqtt`, `historian/influx`, `tags/maps`, `tags/links`, `connectivity/sources`, `connectivity/diagnostics`, `ops/logs`, `ops/diagram`, `help/guide`, `help/about`) are identical across Task 1 `ROUTE_TO_TAB`, Task 5 banner `navigate(...)` calls, Task 7 `LEGACY_TAB_TO_ROUTE` values, and Task 6 help text. Wizard function names (`openAddSourceWizard`, `openMqttWizard`, `openInfluxWizard`) are consistent across markup `onclick` and JS definitions. Frozen DOM ids from Task 0 are preserved in all later tasks.
