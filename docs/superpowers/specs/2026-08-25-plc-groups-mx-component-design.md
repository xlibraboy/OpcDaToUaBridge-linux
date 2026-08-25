# PLC Groups — Named Polling Groups for MX Component Sources — Design Spec

- **Date:** 2026-08-25
- **Branch:** `feature/mx-component-group`
- **Status:** Approved design; implementation pending
- **Scope:** MxComponent sources (`SourceType=MxComponent`, `MxComponentClient`). DA and UA sources are out of scope; the S7 and Melsec serial drivers are explicitly future work but must need no model reshaping.

## 1. Problem

MX Component sources today have exactly one polling cadence surface: a raw numeric `PollRateMs`
per tag (falling back to the bridge default rate). The worker already runs one poller per distinct
rate, so multiple rates *work*, but there is no named-group management layer comparable to OPC DA's
Groups (`DaGroupIoMode`: name + rate + IO mode) or OPC UA's Subscriptions
(`UaSubscriptionSettings`: name + rate). Operators cannot define, name, edit, or delete rate
buckets per source; every cadence change means retyping millisecond values on individual tags.

## 2. Goal

An MxComponent source may define any number of **named PLC groups**, each with its own update
rate. Each tag mapping is explicitly assigned to one named group or left unassigned (riding the
existing default-rate fallback). Operators manage groups in a dedicated dashboard tab.

### Decisions (user-approved)

| Decision | Choice |
|---|---|
| Feature model | New unified **PLC Groups** concept (shared abstraction; S7/Melsec adoption later is a small flip, not a reshape) |
| Rate ownership | **Group rate wins** while assigned; per-tag `PollRateMs` cleared on detach (matches existing `ClearDaGroup` behavior); unassigned tags use the current default fallback |
| Management UI | Dedicated **PLC Groups** tab under Sources (same IA level as DA Groups), with source picker |
| Rollout scope | Settings/storage source-type-generic from day one; API/UI accept **MxComponent sources only** this iteration |
| Delete semantics | Auto-reassign member tags to the source default bucket |

### Non-goals

- No IO-mode concept (DA Groups carry AutoDetect/Sync/Async20; MX Component reads are always polled — there is no push path).
- No deadband/filter configuration per group (per-tag `DeadbandPct` semantics unchanged).
- No enablement for S7/Melsec serial sources in this iteration (model only).
- No automatic synthesis of groups from existing `PollRateMs` values (mirrors the UA multi-subscription spec's non-goal).
- No change to connection identity: group edits never reopen the ActUtlType COM session.

## 3. MX Component API reality (basis for bridge-side groups)

From the MX Component Version 4 Programming Manual (sh081085engt.pdf):

- All bulk value access is synchronous (`ReadDeviceBlock`, `ReadDeviceBlock2`, `ReadDeviceRandom`,
  …). There is no native value-change subscription/callback for data acquisition.
- The closest feature, `EntryDeviceStatus`/`FreeDeviceStatus`/`OnDeviceStatus`
  (manual pp. 409–414), monitors whether devices match a specified status/value: max **20 points**,
  cycle **1 s–1 h**, event notification on match. It is an alarm-condition mechanism, not a data
  subscription path.

Therefore PLC groups are **bridge-side timer buckets**: each group is one poller loop at its
configured rate over the single shared logical-station session, exactly like the worker's existing
per-rate pollers. This is the same conclusion as OPC DA's group model applied to a link that only
offers synchronous reads.

Operational characteristic (documented, not changed): all of a source's buckets share one
ActUtlType session serialized by the client's `SemaphoreSlim(1,1)`; writes queue through the same
semaphore. A slow bucket's worst-case added latency is one fast batch ahead of it.

## 4. Data model & persistence

### Core (`OpcBridge.Core`)

```csharp
/// <summary>
/// One named PLC polling group on a PLC-type source: a display name and the update rate (ms)
/// its member tags are polled at. Pure data type — validation/clamping happens at the
/// settings/API layer (100 ms floor), mirroring UaSubscriptionSettings.
/// </summary>
public sealed record PlcGroupSettings(string Name, int UpdateRateMs);
```

Placed in Core so App/Hmi (and future drivers) can share it without new reference edges.

- `TagMapping` gains `string PlcGroup { get; set; } = ""` (`[JsonPropertyName("plcGroup")]`;
  empty string = source default bucket). Round-trips through `MappingStore` / `mappings.json`
  automatically via its serializer.

### Source settings (`OpcBridge.App`)

- `DaSourceRuntimeSettings` gains an optional top-level parameter
  `IReadOnlyList<PlcGroupSettings>? PlcGroups = null` plus a compat getter
  (`PlcGroupsList => PlcGroups ?? []`). Stored **top-level, not inside**
  `MxComponentSourceOptions`, because PLC Groups is deliberately driver-unified; enabling S7/
  Melsec later must not require reshaping settings records. Persisted in `sources.json` by the
  existing snapshot serializer; DTO gains a matching nullable property with round-trip mapping.
- Validation at the mutation layer (mirrors UA subscription rules): name required, trimmed,
  1–64 chars, unique case-insensitive per source; `UpdateRateMs >= 1` accepted, clamped internally
  to the 100 ms floor; soft cap of **16 groups per source** (`SourceConfigMigration.MaxPlcGroupsPerSource`,
  alongside a `NormalizePlcGroups` helper matching `NormalizeUaSubscriptions`); only allowed when
  `SourceType == MxComponent` in this iteration — other types reject with a clear message.
- Rename is remove+add (no rename operation).

### MappingStore semantics

- Assigning sets `PlcGroup`. Detaching clears `PlcGroup` **and zeroes `PollRateMs`** (identical to
  today's `ClearDaGroup`) so "group rate wins" leaves no stale numeric override behind.
- Removing a group batch-reassigns every member tag to the default bucket in one locked pass /
  ONE persist / ONE change event per call (same shape as `ReassignSubscription`), returning the
  count moved.
- A stored group name that no longer matches any definition never blocks loading: at runtime the
  tag falls into the default bucket (resilience), while normal mutation paths keep stored data clean.

## 5. Runtime: effective-rate resolution & worker

### Effective-rate rule

For every read-mode tag:

```
1. PlcGroup matches a defined group on that source → that group's UpdateRateMs
2. else PollRateMs > 0                             → PollRateMs            (legacy path unchanged)
3. else                                            → snapshot default rate  (unchanged)
```

The default fallback remains resolved at poller-cycle time (`currentSettings.UpdateRateMs` inside
`RunSourcePollerAsync`) exactly as today, preserving live default-rate edits without cache rebuilds.

### Cache changes (`SourceMappingCache`)

- `Build(mappings, rules)` gains one extra input: the per-source group definitions from the
  settings snapshot (case-insensitive `(sourceId, groupName) → rate` map).
- `GetDistinctRates(sourceId, defaultRate)` and `GetSourceReadMappingsByRate(sourceId, rate,
  defaultRate)` resolve rates through the rule above. Resolution stays at query time (not baked
  into Build-time buckets) so live default-rate edits keep working; method signatures stay
  compatible so DA/UA callers do not change.

### Poller mechanics — zero changes

One-poller-per-distinct-effective-rate remains the scheduler. Emergent property: assigning a tag
to a group whose rate equals its current effective rate leaves the distinct-rate set unchanged, so
no poller restarts and value flow never stutters.

### Restart trigger for group definition edits

Creating/deleting/re-rating a group bumps the snapshot `Version` but does not touch
`mappings.json`, so the existing `mappingsChanged` path cannot notice. BridgeWorker keeps a
fingerprint of each MxComponent source's applied `PlcGroups` list (order-insensitive
name→rate equality):

- On a settings-version bump, compare fingerprints; for changed sources compute the new
  distinct-rate set vs. the previously applied set; when they differ, route the source through the
  existing `RestartPollersForSourcesAsync` path (the same path used when a raw per-tag rate set
  changes). When the set is identical, do nothing.
- Double-triggers (settings bump and mapping change in the same tick) are harmless:
  stop/start is idempotent per `{sourceId}:{rate}` poller key.
- **Connection identity untouched:** `SourceConnectionEquals` for MxComponent remains
  type/timeout/retry-only. Group edits restart timer loops only; the COM session is never reopened.

## 6. App / API layer

### Registry (`DaRuntimeSettings`)

- `UpsertPlcGroup(sourceId, name, updateRateMs)` — add/update by normalized name (§4 validation).
- `RemovePlcGroup(sourceId, name)` — removes the definition; returns the new snapshot. Member-tag
  reassignment runs through the `MappingStore` update path (§4).

### Endpoints (house style: POST bodies, mirroring the UA endpoints)

| Endpoint | Body | Behavior |
|---|---|---|
| `POST /api/plc/groups` | `{sourceId, name, updateRateMs}` | Upsert with §4 validation; bumps snapshot version → restart trigger. `200 {ok, version}` / `400 {error}` |
| `POST /api/plc/groups/remove` | `{sourceId, name}` | Remove + auto-reassign; `200 {ok, version, movedMappings}` / `400 {error}` |
| `GET /api/plc/groups?sourceId=` | — | Definitions + member tag counts + each source's current effective distinct-rate set (computed from mappings via the §5 rule) for one source or all MxComponent sources when omitted |

Request records live alongside the existing `UaSubscriptionRequests.cs`.

### Effective-rate display

`DashboardValues.BuildUpdateRateLookup` extends the same precedence rule (assigned group →
group rate; else per-tag `PollRateMs`; else source default). The lookup builder receives the
source's group definitions. While a tag is grouped, the faceplate shows the effective rate
read-only ("group rate wins").

## 7. Dashboard UI

- **PLC Groups tab** under the Sources sidebar group, route `connectivity/plc-groups`, next to
  DA Groups. Source picker filtered to MxComponent sources (empty-state message when none).
- Per source, a card/table of groups: Name · Update rate · Member tags · Edit · Delete, plus an
  *Add group* modal (name + rate ms; 100 ms floor enforced client- and server-side).
- Delete warns when member count > 0 ("N tags will move to the source default rate"), then
  reassigns and reports the moved count.
- Rate edits reuse the existing save/pending indicator convention from the DA Groups modals until
  values refresh.
- **Tag assignment:** faceplate/tag editor gains a **PLC Group** dropdown for tags whose source is
  MxComponent — options *Default* + defined groups (mirrors `fpSubscription` for UA).

## 8. Testing

| Area | Coverage |
|---|---|
| Validation | trim/length/uniqueness, 100 ms clamp, 16-group cap, non-MxComponent rejection |
| Rate resolution | group-wins precedence; legacy per-tag path untouched; unknown-name → default fallback; query-time resolution survives a live default-rate edit without rebuild |
| MappingStore | assign/detach semantics (detach zeroes `PollRateMs`); batch reassign counts; single persist/change event per call |
| Worker | group create/delete/rate-edit fingerprints; restarts only affected source's pollers; no restart when rate set identical; COM session NOT recreated (connection identity untouched) |
| API | happy paths + every `400` branch for all three endpoints |
| Dashboard | tab render, source picker filtering, modal save/delete flows, member counts, faceplate dropdown |

Baseline before implementation: 558 tests passing on `feature/mx-component-group`.

## 9. Migration & back-compat

Absent fields default everywhere; existing `mappings.json` / `sources.json` load byte-identically;
sources without groups behave identically to today, including the tested non-DA poller-restart-on-
rate-set-change path. No migration tooling required.
