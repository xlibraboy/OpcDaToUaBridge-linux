# OPC UA Source Design

- **Date:** 2026-07-27
- **Branch:** `feature/opc-ua-source` (worktree `.worktrees/feature-opc-ua-source`)
- **Status:** Approved design (§1–§4), pending implementation plan
- **Primary surfaces:** `BridgeWorker` / source registry, new UA client, dashboard Connectivity
- **Related:** Connectivity IA in `docs/superpowers/specs/2026-07-27-connectivity-opc-da-tab-design.md`; architecture in `context.md`

## 1. Goal

Add **OPC UA as an inbound source type**. The bridge opens a **UA client** to external servers (Siemens, Kepware, etc.), maps selected Variable nodes, keeps them live at high scale, and re-publishes them on the existing outbound path (this process’s OPC UA server, MQTT, Influx, HMI). Writes on writeable mapped tags go **back** to the external UA server (write-through).

This is **not** a replacement for the bridge’s existing OPC UA **server** endpoint (`opc.tcp://…:4840`). External SCADA/HMI clients still connect **to** the bridge on that endpoint. An OPC UA **source** means the bridge connects **out** to another UA server and ingests values.

```text
OPC DA servers  --(DA client)-->  OpcDaToUaBridge  --(our UA server)--> SCADA / HMI
External UA     --(UA client)-->         ↑
  (Siemens/Kepware)
```

**Success means:**

1. Create and configure multiple OPC UA sources under Connectivity (alongside OPC DA).
2. Browse the external address space (paged) and map Variable nodes to bridge tags.
3. Live values via **UA subscriptions** on the mapped set; poll only as fallback for that set.
4. **Write-through** for mappings with `Writeable=true` (UA / HMI / MQTT write path).
5. Steady operation with **tens of thousands of mapped tags** per source without collapsing throughput (see §4 scale contract).
6. Existing OPC DA sources and the outbound UA server endpoint remain unchanged in behavior.

## 2. Decisions locked in brainstorming

| Decision | Choice |
|---|---|
| Role of OPC UA source | Inbound **client** into the bridge (pull from Siemens/Kepware) |
| v1 capability | **Read + write-through** |
| Security modes | **None** + **Sign** + **SignAndEncrypt** with **Basic256Sha256** (and None policy for None mode) |
| Tag selection | **Browse + explicit map** in v1; **subtree import later** (config shape must not block it) |
| Value updates | **Subscriptions first**, poll fallback on mapped set only |
| Architecture | **A** — extend multi-source model + client seam; reuse `BridgeWorker` sessions, mappings, write queue, UA mirror |
| Scale target | Design for **≥ 20k mapped tags / source** steady state; multi-source additive |
| Hot set | **Only mapped tags** ever enter live ingest (never full remote address space) |
| Item identity | Keep mapping key `(SourceId, DaItemId)`; for UA, `DaItemId` stores **NodeId string** (documented as source item id; no mass rename in v1) |
| Self-feed | Guard against connecting a source to **this** process’s own UA endpoint |

## 3. Non-goals (v1)

- Auto-mirror of the entire remote UA address space
- Subtree / folder bulk import (designed for later, not implemented in v1)
- Full OPC UA security policy matrix beyond None + Basic256Sha256 Sign / SignAndEncrypt
- Historical read (`HistoryRead`) from the external server
- Alarms & conditions, methods, programs, complex/extension object types beyond scalar + string (+ straightforward arrays only if free with stack defaults; no custom encoding work in v1)
- Product rename / full `IDaClient` → `ISourceClient` rebrand across the solution
- DA Links with UA endpoints as provider or consumer
- Full certificate-manager UI (paths + defaults + docs; advanced UX later)
- Virtualized redesign of the dashboard live-values table for 50k rows (track as follow-up if needed)

## 4. Scale contract

External node count and bridge load are different. Bridge load is driven by **mapped** tags only.

| Rule | Contract |
|---|---|
| What is hot | Only tags present in `MappingStore` for that `SourceId` |
| Primary ingest | OPC UA **MonitoredItems** on one or more **Subscriptions** |
| Poll fallback | Mapped set only, rate-grouped; never full-tree poll |
| Capacity target | **≥ 20 000 mapped tags per UA source** steady state; multiple sources add |
| MonitoredItem create/delete | Batched (implementation default **500–1000** per call, honor server limits) |
| Notification apply | Batched into `BridgeState` + `UaServerHost.UpdateValue`; no per-tag sync UI work on the hot path |
| Outbound mirror | One UA variable per mapping (existing pattern); bulk `SyncMappings` / `UpdateValue` |
| Writes | Per-tag write via existing `WriteQueue` consumer; no read-modify-write storms |
| Browse | Lazy, paged; browse **never** drives the live value path |
| Caps | `MaxMappedTags` per UA source, **configurable**; default high enough for the capacity target (e.g. **50000**), soft warn below hard fail |
| Dashboard | Status and config remain O(sources); tag UIs must not require loading all remote nodes at once |

```text
Kepware / Siemens (e.g. 50k+ nodes in address space)
        │  paged browse (UI only)
        │  map N tags (N can be 10k–50k)
        ▼
UA client Subscription(s) on those N NodeIds only
        ▼
BridgeValue[] → BridgeState → our UA server / MQTT / Influx
        ▲
WriteQueue (writeable mapped tags only)
```

## 5. Configuration model

### 5.1 Source type

Every source gains:

- `SourceType`: `"OpcDa"` | `"OpcUa"`
- Missing / null in existing `sources.json` → **`OpcDa`** (backward compatible)

### 5.2 Shared identity fields

| Field | Notes |
|---|---|
| `SourceId` | Unique key; used in outbound NodeIds `ns=2;s={SourceId}/…` |
| `DisplayName` | UI label |
| `UpdateRateMs` | Default publishing interval / poll interval for the source |
| `SourceType` | Discriminator |

### 5.3 OPC DA fields (unchanged semantics)

`ProgId`, `Host`, `RemoteUsername`, `RemotePassword`, `RemoteDomain` — used only when `SourceType == OpcDa`.

### 5.4 OPC UA fields

| Field | Purpose | v1 default / notes |
|---|---|---|
| `EndpointUrl` | e.g. `opc.tcp://kepware:49320` | required for UA |
| `SecurityMode` | `None` \| `Sign` \| `SignAndEncrypt` | `None` |
| `SecurityPolicyUri` or policy name | `None` \| `Basic256Sha256` | `None` when mode is None; Basic256Sha256 when Sign* |
| `Username` / `Password` | optional UserName identity token | empty = anonymous (when server allows) |
| `SessionTimeoutMs` | client session timeout | stack-sensible default (e.g. 60000) |
| `ReconnectDelayMs` | backoff base after disconnect | e.g. 5000 |
| `MaxMappedTags` | hard cap for mappings on this source | e.g. 50000 |
| `UseSubscriptions` | prefer MonitoredItems | `true` |

Passwords remain in `sources.json` like DA remote passwords today (same threat model; no new secret store in v1).

### 5.5 Persistence

- Continue **`sources.json`** beside the app (`AppContext.BaseDirectory`) via the existing runtime settings service.
- Extend `DaSourceRuntimeSettings` / `SourceConfigDto` / API request DTOs with the fields above (nullable / ignored for the other type).
- Optional rename of types toward “source” in code comments and new symbols is fine; **file name** `sources.json` stays for continuity.
- Seed from `appsettings` remains DA-oriented unless an explicit `UaSources` seed is added later; v1 runtime creation is via API/UI.

### 5.6 Mapping identity

- Keep `TagMapping` key `(SourceId, DaItemId)` case-insensitive.
- For UA sources, **`DaItemId` = NodeId string** as returned/browsed (e.g. `ns=2;s=Channel1.Device1.Tag` or `ns=3;i=1001`).
- Default outbound bridge NodeId remains `ns=2;s={SourceId}/{DaItemId}` with the same sanitization rules as today if any; if NodeId characters break the string NodeId form, sanitize for the **outbound** id only — **do not** alter the stored source item id used for client Read/Write/Monitor.
- `DataType`, `Writeable`, `AccessRights`, `PollRateMs`, `MqttEnabled`, `InfluxEnabled`, `Mode`/`ManualValue` keep current meanings. Manual mode still bypasses source reads for that tag.

## 6. Runtime architecture

### 6.1 Approach A (locked)

Extend the multi-source pipeline rather than a parallel ingest worker:

1. **Registry** — versioned source snapshot includes type-specific settings.
2. **Factory** — `DaClientFactory` (or thin `SourceClientFactory`) returns:
   - `OpcDaClient` for `OpcDa`
   - `OpcUaSourceClient` for `OpcUa`
3. **`BridgeWorker`** — existing `ReconfigureSessionsAsync` / `SourceSession` / pollers / `WriteQueue` consumers.
4. **Normalized unit** — still `BridgeValue(SourceId, DaItemId, Value, TimestampUtc, DaQuality, IsGood)`.
5. **Outbound** — unchanged `UaServerHost` / MQTT / Influx / HMI.

### 6.2 Client seam

Current `IDaClient`:

- `ConnectAsync`
- `ReadAsync(mappings)`
- `WriteAsync(daItemId, value)`
- `TryGetTagMetadata`
- `IAsyncDisposable`

**v1 plan:** implement `OpcUaSourceClient` against this seam, and **extend** only as needed:

- Subscription callback surface (today DA hooks `OpcDaClient.OnCallbackValues` with a concrete type check). Prefer a small interface addition or shared event pattern so `BridgeWorker` does not special-case only `OpcDaClient`.
- Optional `BrowseAsync` may live on a separate browse service used by HTTP APIs (not required on the hot `IDaClient` path).

Full rename to `ISourceClient` is an optional follow-up, not a v1 gate.

### 6.3 Project placement

- Prefer new client types under **`OpcBridge.Ua`** (already references OPC Foundation stack) **or** a focused area clearly named source-client within Ua/App — avoid putting UA client code in `OpcBridge.Da`.
- App references remain; factory in App (or Core-neutral factory in App) constructs the client.
- Reuse `OPCFoundation.NetStandard.Opc.Ua` (Client assemblies already transitively available). **Server and client PKI must be separated** (see §8).

### 6.4 Connect & session lifecycle

On add/update source or settings version bump:

1. Dispose previous session for that `SourceId` if any.
2. Create client from factory.
3. `ConnectAsync`: create application configuration (client), select endpoint matching URL + security mode/policy, create session, optional user identity.
4. Mark `BridgeState` Connected / Faulted with clear errors.
5. After connect (and on mapping version change): **reconcile MonitoredItems** to current enabled, non-Manual mappings for that source.

Empty `EndpointUrl` (UA) or empty `ProgId` (DA) → Disconnected with explicit error; process stays up.

### 6.5 Subscriptions (primary path)

- Create subscription with publishing interval derived from source `UpdateRateMs` (and/or per-rate groups if multiple intervals are required).
- For each mapped source item (enabled, `Mode != Manual`): ensure MonitoredItem on that NodeId.
- Sampling interval: align with tag `PollRateMs` when > 0, else source default; deadband: map `DeadbandPct` only if stack/server supports percent/absolute easily — otherwise document “best effort / server default” for v1 rather than inventing client-side filter that breaks write-through expectations.
- Data-change notifications → build `BridgeValue[]`:
  - `DaQuality` / `IsGood` from UA `StatusCode` via a small `UaQualityMapper` (Good → good; uncertain/bad → not good; preserve numeric quality field with a documented mapping table).
  - Timestamp: source timestamp if present, else server timestamp, else UTC now.
- Apply batch to `BridgeState` and outbound UA mirror (same as DA subscription path).

**Reconcile algorithm (mapping change):**

1. Desired set = mapped NodeIds for source.
2. Remove MonitoredItems not in desired set.
3. Add missing MonitoredItems in **batches**.
4. Never block the coordinator loop indefinitely; failures on individual items surface per-tag / log without necessarily disposing the whole session.

### 6.6 Poll fallback

If subscription setup fails or server cannot support required items:

- Set source status to a degraded but connected state if session is up (e.g. connection detail / last error explains “subscriptions unavailable; polling”).
- Existing poller tasks call `ReadAsync` on the mapped set for that rate group only.
- Do not attempt to poll unmapped nodes.

### 6.7 Writes

- Reuse `WriteQueue` and per-source consumer.
- `OpcUaSourceClient.WriteAsync(nodeId, value)` performs a UA Write to that NodeId.
- Success/failure completes the same `TaskCompletionSource` path used by UA server / HMI writes today.
- Type coercion: best-effort convert CLR value to the monitored/mapped data type; on failure return false / bad write (no crash).

### 6.8 Loop / self-endpoint guard

Before connect, reject configuration when `EndpointUrl` refers to **this** bridge’s listening UA endpoint (host loopback / wildcard equivalence + path), using current `UaServerOptions.EndpointUrl` (and bound ports). Error message must state that a source cannot target the bridge’s own server.

## 7. HTTP API

### 7.1 Extend existing source APIs

- `GET /api/da/sources` — include `sourceType` and type-specific fields (progId/host **or** endpointUrl/security/…). Name prefix `da` is legacy; acceptable in v1 to avoid a large API rename. Document as “sources” in Help.
- `POST /api/da/sources` — accept polymorphic body; validate required fields per `sourceType`.
- Remove / update-rate endpoints — work for both types.

Validation examples:

- `OpcUa`: `EndpointUrl` required, must be `opc.tcp://…` (reject empty / non-opc.tcp in v1 unless discovery story expands).
- `SecurityMode` / policy combination must be valid.
- Enforce `MaxMappedTags` on mapping add when source is UA (return 400 with clear error when exceeded).

### 7.2 New UA helper endpoints

| Endpoint | Role |
|---|---|
| `POST /api/ua/test-connection` | Body: endpoint + security + credentials (or sourceId). Result: ok / error, server details if available |
| `POST /api/ua/browse` | Body: connection fields or sourceId, `nodeId` (default Objects folder), `maxNodes`, optional continuation point. Returns paged children (nodeId, displayName, nodeClass, type if cheap) |
| `POST /api/ua/endpoints` | Optional v1: discovery URL → list endpoints (URL, security mode, policy). Can ship in same milestone if low cost |

Browse and test-connection must:

- Run with timeouts.
- Work on Linux for UA (no COM).
- Not require Windows (unlike `/api/da/servers` and `/api/da/tags`).

### 7.3 Mappings

Existing `/api/mappings/*` remain. For UA sources, `daItemId` is the NodeId string. No separate mapping store.

## 8. Security & PKI

| Topic | v1 behavior |
|---|---|
| Modes | None; Sign; SignAndEncrypt |
| Policy | None; Basic256Sha256 |
| Client certs | Auto-create/store under a **client-specific** PKI root, e.g. `pki/ua-client/` (`own`, `trusted`, `issuers`, `rejected`) — **not** mixed with server `pki/` |
| Trust | Configurable auto-accept for lab (mirror server’s dev default carefully); production: trust peer certs explicitly via trusted store |
| User auth | Anonymous and UserName tokens |
| Secrets | Password fields in sources.json (same as DA remote password today) |

Document that production Siemens/Kepware deployments typically need SignAndEncrypt + trusted certs + user credentials.

## 9. Dashboard / Connectivity UI

### 9.1 Information architecture

```text
CONNECTIVITY
  Sources       connectivity/sources     status list (DA + UA)
  OPC DA        connectivity/opc-da      DA config (existing)
  OPC UA        connectivity/opc-ua      NEW UA source config
  Diagnostics   connectivity/diagnostics unchanged
```

### 9.2 Sources status list

- All sources; **type badge** `DA` / `UA`.
- Summary: display name, sourceId, host+ProgId **or** EndpointUrl, rate, connection badge, last error snippet.
- **Select** → `connectivity/opc-da` or `connectivity/opc-ua` with that source loaded.
- **+ Add Source** wizard: first step **type** (OPC DA | OPC UA), then type-specific steps.

### 9.3 OPC UA config page

Mirror OPC DA page structure:

- Selected source + identity (Source ID, Name)
- Endpoint URL, security mode/policy, username/password
- Update rate, use subscriptions
- Toolbar: Save / Reset / New / Remove
- **Test connection**
- Side: saved sources list (type-filtered or mixed with badge); optional endpoint discovery panel
- Backup: prefer shared export if trivial; otherwise leave DA backup on OPC DA page and add UA-safe export in a follow-up (must not claim “full config” if UA sources omitted)

Stable new control id prefix: `uaCfg*` / `wzUa*` (do not reuse `cfgProgId` for endpoint).

### 9.4 Tags / browse / map

- Tag Browser gains source-type awareness: DA browse APIs for DA sources; `/api/ua/browse` for UA sources.
- Selecting Variable nodes → add mapping with NodeId as item id.
- Faceplate Writeable → write-through via existing write pipeline.
- Subtree import: hidden/disabled in v1; do not paint into a corner that requires schema rewrite later (e.g. keep source-level notes free-form if needed).

### 9.5 Routes & Help

| Nav | Route | `data-tab` |
|---|---|---|
| OPC UA | `connectivity/opc-ua` | `opc-ua` |
| Legacy | `#opc-ua` | same |

Help Connectivity line: Sources (status), OPC DA (DA config), **OPC UA (UA client sources)**, Diagnostics.

Clarify in Help: “OPC UA **source** = bridge connects to external UA servers. OPC UA **endpoint** under server settings = clients connect to this bridge.”

## 10. Resilience

- Session fault → source `Faulted` + `LastError`; process remains up; other sources continue.
- Reconnect with delay (`ReconnectDelayMs`), exponential optional but not required in v1.
- After reconnect: recreate subscription and reconcile MonitoredItems.
- Individual bad NodeIds → that item bad quality / skip; do not dispose entire session unless the session itself fails.
- Rate-limited logging on reconnect storms.
- Coordinator behavior stays failure-resilient (existing failed-source rebuild patterns apply).

## 11. Performance notes (20k+ mapped)

Implementation must treat the following as requirements, not polish:

1. **No per-notification UI work** — dashboard continues to poll summary APIs; do not push 20k DOM updates per second.
2. **Batch MonitoredItem operations** — create/delete in chunks; respect `MaxMonitoredItemsPerCall` if exposed by server/stack.
3. **Batch value apply** — notification handler aggregates to lists before `BridgeState` / UA mirror updates.
4. **Mapping reconcile incremental** — diff previous vs desired NodeId sets; do not drop/recreate entire subscription on single tag add when avoidable.
5. **Write path isolation** — write consumer must not block notification processing (existing queue separation).
6. **Memory** — avoid retaining full remote browse trees; cache only open browse pages / optional display names for mapped ids.
7. **Our UA server** — `SyncMappings` already versioned; ensure bulk add of large mapping sets is chunked if stack/UI timeouts appear (implementation plan detail).

If load tests show `BridgeState` dictionary or dashboard `/api/values` payloads become the bottleneck at 20k+, follow-up is API pagination / HMI already-separated paths — not abandonment of subscriptions.

## 12. Testing & verification

| Layer | What |
|---|---|
| Unit | JSON migrate default `SourceType`; factory branch; self-URL guard; quality mapping; validation of security mode/policy; MaxMappedTags enforcement |
| Integration | Local UA server fixture (or optional test dependency) for connect / subscribe / write on Linux CI when feasible; otherwise Windows lab script |
| Scale smoke (lab) | Map large synthetic set; confirm batched subscribe, steady notifications, write still works, process healthy |
| Dashboard smoke | Add UA source, test connection, browse, map, live value, write-through |
| Regression | Existing DA source connect/read/write; outbound UA endpoint; MQTT/Influx opt-in flags |

Primary bar remains: clean build (0w/0e) + `/health` + targeted smoke of the new path.

## 13. Implementation sketch (not a plan)

1. Model: `SourceType` + UA fields on runtime settings / DTOs / `sources.json` migrate.
2. `OpcUaSourceClient` + factory branch + quality mapper + self-URL guard.
3. `BridgeWorker` subscription hook generalized beyond `OpcDaClient`.
4. APIs: polymorphic sources + `/api/ua/test-connection` + `/api/ua/browse`.
5. Dashboard: OPC UA tab, wizard type step, Sources badge, Select routing.
6. Tag browser UA path + mapping add.
7. Security PKI client store + Sign/SignAndEncrypt.
8. Scale pass (batching, reconcile, lab smoke).
9. Help + `context.md` update.

Detailed task breakdown belongs in `docs/superpowers/plans/` after this spec is reviewed.

## 14. Risks

| Risk | Mitigation |
|---|---|
| Dual UA client+server PKI confusion | Separate `pki/ua-client/` tree; distinct application name/URI for client config |
| 20k+ mappings stress state/API | Subscriptions + batching; optional later pagination of `/api/values` |
| `DaItemId` naming confusion | Document as source item id; NodeId string contract in Help |
| Server monitored-item limits | Batch creates; surface server errors; configurable cap |
| Write storms from HMI/MQTT | Existing write queue; only writeable mappings |
| Scope creep (history, events, subtree) | Explicit non-goals |
| Legacy `/api/da/*` naming | Accept in v1; document; optional rename later |
| Self-subscription loop | Connect-time guard against own endpoint |

## 15. Out of scope follow-ups

- Subtree / folder import with filters
- `ISourceClient` rename and `/api/sources` rebrand
- Cert manager UI, GDS
- HistoryRead, A&C, methods
- DA Links across UA
- Live values virtualization for huge tables
- Auto endpoint security negotiation UX beyond basic discovery list

## 16. Approval

Brainstorming approvals (2026-07-27):

- §1 Goal, non-goals, scale contract — approved
- §2 Config model, client, pipeline seams — approved
- §3 Connectivity UI & tag mapping UX — approved
- §4 Resilience, testing, risks, roll-out — approved
- Architecture approach **A** with high-scale mapped-tag subscriptions — approved
- Capability: read + write-through; security None + Sign + SignAndEncrypt (Basic256Sha256); browse+map now / subtree later; subscriptions-first — approved
