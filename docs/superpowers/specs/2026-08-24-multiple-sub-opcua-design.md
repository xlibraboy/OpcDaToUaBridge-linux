# Multiple Named Subscriptions per OPC UA Source — Design Spec

- **Date:** 2026-08-24
- **Branch:** `feature/multiple-sub-opcua`
- **Status:** Approved design; implementation pending
- **Scope:** OPC UA inbound sources (`SourceType=OpcUa`, `OpcUaSourceClient`). DA and PLC drivers are out of scope.

## 1. Problem

Today every OPC UA inbound source runs exactly **one** UA `Subscription`. Its publishing interval is
the fastest desired sampling across all mapped tags (`UaSamplingRates.DesiredPublishingInterval`), and
per-tag rates only refine monitored-item sampling inside that single publishing queue. An operator
cannot run some tags at 250 ms and others at 10 s without forcing the subscription (and therefore the
server's publish cycle) to the fastest rate for everything.

## 2. Goal

An OPC UA source may define any number of **named subscriptions**, each with its own update rate.
Each tag mapping is explicitly assigned to one named subscription or left unassigned (riding the
source's existing default rate). Users manage subscriptions in a dedicated dashboard tab.

### Decisions (user-approved)

| Decision | Choice |
|---|---|
| Tag → subscription assignment | Explicit per-tag pick (new `Subscription` field on `TagMapping`) |
| Source-level `UpdateRateMs` | Stays as an implicit unnamed *default* bucket; named subs are extra buckets |
| Management UI | Separate **Subscriptions** tab under Sources (not inside the source form) |
| Deleting an in-use subscription | Auto-reassign its tags back to the source default |

### Non-goals

- No change to DA sources, PLC drivers, MQTT/Influx config surfaces, or HMI client.
- No deadband/filter configuration per subscription (per-tag `DeadbandPct` semantics unchanged).
- No subscription-level security/priority controls (`priority=0`, standard default).
- No automatic rate-bucket synthesis from `PollRateMs`.

## 3. Standards basis (OPC UA Part 4 v1.05.07)

The feature models each rate bucket as a real UA `Subscription`, which is the correct standard
mapping: a Subscription owns exactly one publishing queue (`§5.14.1`), so distinct delivery cadences
require distinct Subscriptions. Conformance rules applied:

- **Revision read-back (`§5.14.2`):** servers never reject revisable parameters; they return revised
  values. The client stores `CurrentPublishingInterval` per bucket and the API/UI expose requested vs
  actual interval. Requested interval ≤ 0 is rejected at our API layer instead (clear operator error),
  then clamped to the 100 ms floor before reaching the server.
- **Lifetime ≥ 3× keep-alive:** buckets use `KeepAliveCount=10`, `LifetimeCount=1000` (same as the
  current single subscription; satisfies the §5.14.2 minimum rule).
- **`maxNotificationsPerPublish=0`** (unlimited) and **`priority=0`** ("clients that do not require
  special priority settings should set this value to zero").
- **Sampling (`§5.13.1.2`):** member items inherit the bucket contract — `SamplingInterval` = bucket
  rate; time-between-values ≥ sampling interval is guaranteed by the server. `QueueSize=1` +
  `DiscardOldest=true` is the standard last-value pattern for mirror pipelines (Prosys guidance:
  larger queues only matter when recording every sample; the bridge keeps latest-value-only state).
- **`Bad_TooManySubscriptions`:** a per-bucket creation failure degrades the whole source to the
  polling fallback — identical contract to today's single-subscription failure path. No partial
  silent states.

## 4. Data model & persistence

### Core (`OpcBridge.Core`)

```csharp
public sealed record UaSubscriptionSettings(string Name, int UpdateRateMs);
```

Placed in Core so Da/App/Ua projects can share it without new reference edges.

- `TagMapping` gains `string Subscription { get; init; } = ""` (empty = source default bucket).
  Round-trips through `MappingStore` / `mappings.json` automatically via its serializer.
- Normalization: names are trimmed and matched case-insensitively against a source's definitions.
  A stored name that no longer matches any definition does not break loading — at runtime the tag
  groups into the default bucket (resilience), while the normal mutation paths (sub delete, explicit
  mapping edit) keep stored data clean.

### Source settings (`OpcBridge.App`)

- `DaSourceRuntimeSettings` gains `IReadOnlyList<UaSubscriptionSettings>? Subscriptions` — only
  meaningful when `SourceType == OpcUa`; null/empty ⇒ byte-for-byte legacy behavior. Persisted in
  `sources.json` by the existing snapshot serializer.
- Validation on upsert: name required, trimmed, 1–64 chars, unique case-insensitive per source;
  `UpdateRateMs >= 1` accepted, clamped internally to the 100 ms floor; soft cap of 16 named subs
  per source (rejects with a clear message long before a server would return
  `Bad_TooManySubscriptions`); only allowed on `SourceType=OpcUa`.
- Rename is remove+add (no rename operation).

### Migration / back-compat

Existing `mappings.json` / `sources.json` load unchanged (absent fields default). Existing sources
without named subs behave identically to today, including the regression-tested session-recreate on
source-rate change.

## 5. UA client changes (`OpcBridge.Ua`)

- `OpcUaSourceClientOptions` gains `IReadOnlyList<UaSubscriptionSettings> Subscriptions` (default
  empty); `SourceClientFactory` passes them through from source settings.
- Internal state becomes a dictionary of **buckets**: key = normalized subscription name, `""` =
  default. Each bucket holds its `Subscription`, per-item bookkeeping, and actual publishing interval.
  NodeIds remain unique across buckets (a tag belongs to exactly one bucket), so global maps
  (`failed_items_`, display-name index) keep working keyed by NodeId alone.

### Reconcile (still fully serialized through the `SemaphoreSlim`)

1. A pure, unit-testable planner (`UaSubscriptionPlan.GroupByBucket`) partitions the desired set
   (enabled, non-Manual, non-empty NodeId, non-Write-only — same filters as today) into per-bucket
   ordered maps: assigned tags whose name resolves against `options_.Subscriptions` go to that
   bucket; everything else goes to default.
2. Default bucket keeps **today's exact algorithm**: item sampling = per-tag `PollRateMs` override
   else source `UpdateRateMs`; publishing = fastest desired sampling in the bucket, clamped ≥ 100 ms.
   Named buckets: item sampling = publishing = the bucket's configured rate (assigned tags ride their
   subscription's rate exactly; per-tag `PollRateMs` does not apply inside named buckets).
3. Each bucket diffs independently using the existing `MonitoredItemReconcile.Diff`; add/remove
   batches reuse the existing chunking (750) and failed-create bookkeeping.
4. Cross-bucket moves (tag reassigned, sub deleted, sub renamed away) fall out naturally: the tag
   leaves one bucket's active set and enters another's diff.
5. `subscriptions_active_` = true iff total desired > 0 AND every bucket is Created AND total tracked
   monitored items > 0. Any bucket failure tears down all buckets and returns to poll fallback.

### Rate-change semantics

- **Named subscription rate change:** recreate only that `Subscription` (teardown + re-add within the
  serialized reconcile). No session bounce — an improvement over the source-rate path and safe
  because the session itself is unaffected.
- **Source default `UpdateRateMs` change:** unchanged behavior — still part of `SourceConnectionEquals`,
  still recreates the session (existing tests cover this).

### Unchanged mechanics

Keep-alive/reconnect adoption, `FastDataChangeCallback` (already parameterized by owning
subscription), notification batching (flush at 1000), failed-item retry timer (re-runs the full
desired set through the planner), and the `Warning`/log surface (`subscription reconcile:` summary
extended with per-bucket counts).

## 6. App / API layer

### Registry (`DaRuntimeSettings`)

- `UpsertUaSubscription(sourceId, UaSubscriptionSettings)` — add/update by normalized name.
- `RemoveUaSubscription(sourceId, name)` — removes the definition **and auto-reassigns** affected
  mappings to `""` through the normal `MappingStore` update path, returning the count moved.

### Reconcile triggering

Subscription-definition changes must reach the live client even though connection equality holds.
`BridgeWorker.ReconfigureSessionsAsync` gains a "definitions changed, connection otherwise equal"
check for UA sources: if the subscriptions list differs (name/rate sequence, order-insensitive), it
calls `ReconcileMonitoredItemsAsync` directly instead of recreating the session.

### Endpoints (house style: POST bodies, same prefixes as existing UA endpoints)

| Endpoint | Body | Behavior |
|---|---|---|
| `GET /api/ua/subscriptions?sourceId=` | — | Definitions + live status per source (or all UA sources when omitted): name, rateMs, itemCount, actualPublishingInterval, created |
| `POST /api/ua/subscriptions` | `{sourceId, name, updateRateMs}` | Upsert with §4 validation; bumps snapshot version → reconcile trigger |
| `POST /api/ua/subscriptions/remove` | `{sourceId, name}` | Remove + auto-reassign; reports `movedMappings` count |

Live status comes from a small extension to `ISubscribableSourceClient`
(`GetSubscriptionsStatus()` returning per-bucket snapshots); disconnected sources report definitions
with zeroed status.

### Mapping APIs

`/api/mappings/add`, `/bulk-add`, `/update` accept optional `subscription` (pass-through like
`accessRights`: absent = unchanged/empty; `/update` replaces the whole mapping, so clients send all
fields). Mapping responses include `subscription`.

### Effective-rate display

`DashboardValues.BuildUpdateRateLookup` resolves a tag's effective rate as: assigned named sub →
bucket rate; else per-tag `PollRateMs`; else source default. The lookup builder receives the source's
subscription definitions.

## 7. Dashboard UI

- **Subscriptions tab** under Sources: table grouped by UA source — Name | Rate | Tags |
  Actual interval | Status — plus add/edit/delete forms. Delete confirms nothing but reports how many
  tags were moved back to default. Non-UA sources do not appear.
- **Maps:** forms for UA-source rows gain a Subscription select (*Source default* + defined names);
  rows show a pill with the assigned name; the Rate column reflects the effective-rate resolution
  above. Assignments ride the existing add/update/bulk flows (no separate endpoint). When a row is
  assigned to a named subscription, its per-tag PollRateMs control is hidden/disabled in the form
  (it has no effect inside named buckets — see §5) and shown read-only as "sub rate" context.

## 8. Testing

Unit tests (xUnit, `tests/OpcBridge.LoadTest`, `InternalsVisibleTo` pattern):

- `UaSubscriptionPlan.GroupByBucket`: assignment resolution (case-insensitivity, unknown name →
  default), filter parity with today, deterministic ordering.
- Bucket-aware reconcile diff: cross-bucket move handling, per-bucket rate application, default-bucket
  algorithm unchanged.
- `MappingStore`: `Subscription` round-trip; auto-reassign on subscription removal.
- `DaRuntimeSettings` validation: duplicate names, non-UA source rejection, clamp, cap of 16.
- Endpoint tests: upsert/remove/list shapes incl. `movedMappings`.
- `DashboardValues` effective-rate precedence.
- Existing suite stays green — especially `SourceConnectionEqualsTests` (default-rate session
  recreate preserved).

Rig verification (post-implementation, manual): define fast (250 ms) and slow (5 s) buckets against
`opcua-sim`, assign tags, confirm cadence separation from notification timestamps in bridge logs and
requested-vs-actual intervals in the tab.

## 9. Risks

- **Reconcile complexity:** the bucket loop multiplies `ApplyChangesAsync` calls; mitigated by keeping
  the existing batch sizes and serialization discipline (the known stale-monitored-item failure mode).
- **Server revision drift:** operators may expect exact cadence while a server revises intervals;
  surfaced honestly via requested-vs-actual columns rather than hidden.
- **Schema drift between nodes:** `TagMapping.Subscription` must be tolerated by older readers
  (unknown JSON fields ignored — System.Text.Json default) enabling rollback safety.
