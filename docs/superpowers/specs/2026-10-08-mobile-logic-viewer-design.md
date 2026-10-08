# Mobile Logic Viewer — Logic Model, Evaluator and Android App — Design Spec

- **Date:** 2026-10-08
- **Branch:** `feature/mobile-logic-viewer` (worktree `.worktrees/mobile-logic-viewer`)
- **Status:** Approved design; implementation in progress
- **Scope:** Bridge logic model + evaluator + API + SignalR push + dashboard authoring tab + a
  new Android app (`OpcBridge.Mobile`, .NET MAUI, C#). The existing Avalonia HMI and Designer
  are untouched; the web dashboard gains one tab.

## 1. Problem

Operators on the plant floor need to know *why* a line cannot start — which interlock or
permissive is not true — without reading PLC code, a faceplate, or asking an engineer. The
bridge today has live tag values and tag→tag interlinks, but **no model for plant logic**
(interlocks, permissives, start-up sequences), and no UI that explains a blocked start in
operator language ("Line 01 Start Permit is Blocked — turn the permit key to Permit").

## 2. Goal

Give the bridge a first-class *logic* concept:

- **Authorable in the web dashboard**: blocks of conditions referencing mapped tags, with
  operator-facing text per condition and a "what to do next" line.
- **Evaluated server-side against live tag values**, so the phone carries no plant logic.
- **Shown on an Android phone** as an easy, glanceable screen: every block's state, the first
  thing that is blocking, each condition's true/false/no-data state with its live value, the
  ordered step flow for sequences, and the next-step text.
- **Actionable**: action buttons on a block/step write tags through the existing write path,
  and the operator can leave notes on a block.

### Decisions (user-approved)

| Decision | Choice |
|---|---|
| Logic source of truth | Authored in OpcBridge (new dashboard **Logic** tab, `logic.json` next to `mappings.json`/`links.json`) |
| Mobile app | **Android APK, .NET MAUI, C#** — reuses `OpcBridge.Client` DTOs, `CommunityToolkit.Mvvm` and the SignalR client |
| Operator role | **View + write tags + notes** — action buttons write via `POST /api/hmi/write`; notes posted per block |
| Logic shapes v1 | **Interlock/permissive blocks and ordered sequences** |
| Wire protocol | Existing HTTP REST + SignalR `/hmi` on the bridge HTTP port; session cookie + `Auth:TrustHmi`; no new port/broker |

### Non-goals

- No alarm engine, acknowledgement workflow or audible annunciation.
- No manual "step done" marking and no cycle reset/history for sequences — sequence progress is
  derived from live conditions + optional completion tags (see §4).
- No editing of logic from the phone (dashboard/API only).
- No HTTPS/TLS termination or certificate management (the plant LAN runs plain HTTP today).
- No Play-store signing — the APK is debug-signed for internal install; keystore signing is a
  follow-up.
- No iOS build; no change to the Avalonia HMI/Designer.
- No expression trees: conditions are single tag comparisons (`on|off|gt|lt|eq`), not
  AND/OR/NOT nesting. Nesting can be added later without reshaping storage (conditions carry
  ids and blocks carry order).

## 3. Data model

Stored in `<data dir>/logic.json` (definitions) and `logic-notes.json` (operator notes).
Shared wire types live in `src/OpcBridge.Client/LogicDtos.cs` / `LogicStateDtos.cs` so the
dashboard JS, the phone and the tests all speak the same JSON.

### Definition

```
LogicBlock {
  Guid Id; string Name (1..64); string Description;
  string Kind;            // interlock | permissive | sequence
  bool Enabled; int Order;
  LogicCondition[] Conditions;   // interlock / permissive
  LogicStep[] Steps;             // sequence
  LogicAction[] Actions;         // block-level buttons
}
LogicCondition {
  Guid Id; string Text;          // operator sentence, shown on the phone
  string SourceId; string ItemId;
  string Op;                     // on | off | gt | lt | eq
  double? Value;                 // required for gt | lt | eq
  string? NextStepText;          // "what to do when this is not true"
  string Severity;               // block | warn
}
LogicStep {
  Guid Id; string Name;
  LogicCondition[] Conditions;
  string? NextStepText;
  string? CompletionSourceId; string? CompletionItemId;  // optional handshake tag
  LogicAction[] Actions;
}
LogicAction { string Label; string SourceId; string ItemId; string Value; bool Confirm; }
```

Validation (all-or-nothing per save): name 1–64 chars; text/source/item required per
condition; `Value` required for numeric ops; `sequence` needs ≥1 step and unique step names;
ops and kinds restricted to the constants (`LogicConditionOps`, `LogicBlockKinds`).

### State (evaluated, never persisted)

```
LogicStateSnapshot { long Version; DateTime EvaluatedUtc; LogicBlockState[] Blocks }
LogicBlockState  { Guid Id; string State; string? Reason; LogicConditionState[]; LogicStepState[] }
LogicConditionState { Guid Id; string State; string ValueText; DateTime? TimestampUtc }
LogicStepState   { Guid Id; string State; string? Reason }
```

States: block `ready | blocked | unknown | disabled`; condition `true | false | unknown`;
step `done | current | pending | unknown`.

## 4. Evaluation semantics

`LogicStateEvaluator` is a pure static class in `OpcBridge.App` (same shape as
`InterlinkStatusEvaluator`), so it is unit-testable without a host.

**Condition.** Resolve the latest value with `BridgeState.TryGetSnapshot(sourceId, itemId)`:

- no value, or `!IsGood` → `unknown` (ValueText = the last value or `—`);
- `on` / `off` use `TagDigital.CoerceBool` — so Boolean *and* Byte 0/1 tags work;
- `gt` / `lt` / `eq` compare numerically (invariant culture); `eq` uses a small tolerance;
- `ValueText` comes from the mapping: `OnText`/`OffText` for digital tags, else the number
  with `Decimals` and `Unit`.

**Interlock / permissive block.**

- `Enabled == false` → `disabled`.
- Any `block`-severity condition false → `blocked`, `Reason` = that condition's `Text`
  (first in authored order).
- No false condition but at least one `unknown` → `unknown`, `Reason` = "no data for …".
- Otherwise → `ready`. `warn` conditions never block; they only render as not-true rows.

**Sequence.** Walk steps in order; a step is `done` when all its conditions are true **and**
either no completion tag is configured or the completion tag is true. The first step that is
not done is `current` (its failing condition, or its unsatisfied completion tag, becomes the
block `Reason`); steps after it are `pending`. Unknown values inside the current step make the
step and block `unknown`. All steps done → block `ready` (complete). Order is the authored
step order. Steps are reorderable in the dashboard.

## 5. API

| Route | Method | Role / gate | Purpose |
|---|---|---|---|
| `/api/logic` | GET | Viewer | block definitions + version (authoring UI) |
| `/api/logic/blocks` | POST | Engineer | create/update a block by id → `{ block, version }` |
| `/api/logic/blocks/{id:guid}` | DELETE | Engineer | remove → `{ version }` |
| `/api/logic/state` | GET | HMI path (`TrustHmi`) | evaluated `LogicStateSnapshot` |
| `/api/logic/notes` | GET | HMI path | `?blockId=&limit=` notes, newest first |
| `/api/logic/notes` | POST | HMI path + Operator override | `{ blockId, conditionId?, text }` → note with author + UTC time |

`/api/logic/state` and `/api/logic/notes` are added to `AuthPolicy.HmiPaths` so the phone works
on the plant LAN exactly like `/api/hmi/*`; `POST /api/logic/notes` also gets an Operator
override row so it behaves like `/api/hmi/write` when `TrustHmi` is off. Definition routes stay
on the default table (GET Viewer, mutations Engineer).

Notes are append-only, capped at the newest 500 entries; the author is the session user or
`app` when auth is off. Error idiom mirrors interlinks (`400 {"error"}`, `404`, `409`).

## 6. Protocol and mobile app

**Protocol.** HTTP JSON (snapshot, commands, notes) plus SignalR at `{base}/hmi` (live push) on
the bridge's Kestrel port (default 8080, auto-assigned 8080–8180). The bridge pushes a new
`logic` message carrying a `LogicStateSnapshot` — only when the evaluated state signature
changes, driven by the existing `HmiBroadcastService` flush tick. The phone takes a snapshot
from `/api/logic/state`, applies `logic` pushes, and falls back to 1 s polling when the hub is
down (the dashboard's own cadence). Actions call the existing `POST /api/hmi/write`. Auth is
the existing `opcbridge_session` cookie (login when auth is enabled, `TrustHmi` otherwise).

**App (`src/OpcBridge.Mobile`, net8.0-android; logic in `src/OpcBridge.Mobile.Core`, net8.0).**

- **Overview**: cards per block — name, kind chip, state chip (colour + text, never colour
  alone), blocking reason line, sequence progress (current step / total).
- **Block detail**: interlock/permissive → condition rows with true/false/no-data marks, the
  operator text, live value text, and the next-step text highlighted on failures; a
  "Blocked by …" banner. Sequence → vertical step flow with connectors, the current step
  highlighted, conditions under each step, completion-tag hint; action buttons with a confirm
  dialog → write; notes list + add-note box.
- **Settings**: bridge host/IP + Scan (probe `GET /api/status/ports` across 8080–8180 on the
  entered host), sign-in (username/password → cookie), link state.
- Live behaviour: snapshot on open, hub deltas while open, resync on reconnect, polling
  fallback; explicit "reading…" until first data; unknown is never rendered as OK.

Android specifics: `INTERNET` permission and `usesCleartextTraffic="true"` (the bridge serves
plain HTTP on the plant LAN).

## 7. Build and release

- `MobileVersion` in `Directory.Build.props`, `src/OpcBridge.Mobile/CHANGELOG.md` (own release
  authority, embedded in the assembly), `ChangelogTests` extended to assert it.
- `OpcBridge.Mobile` is **not** added to `OpcBridge.sln` so `dotnet build`/`dotnet test` in the
  plain SDK container stay workload-free; `OpcBridge.Mobile.Core` is added and covered by
  `tests/OpcBridge.LoadTest`.
- `.github/workflows/mobile-release.yml`: `ubuntu-latest`, `workflow_dispatch` + `mobile-v*`
  tags; tag must equal `mobile-v$MobileVersion`; builds the APK with
  `dotnet workload install maui-android` and
  `dotnet publish -f net8.0-android -c Debug -p:AndroidPackageFormats=apk`; uploads the APK and
  publishes the GitHub Release from the app changelog (mirrors the HMI workflow).
- `Dockerfile.mobile` provides the same APK build locally (SDK + JDK 17 + `maui-android`
  workload), documented in `AGENTS.md`.

## 8. Testing

- Pure: `LogicStateEvaluatorTests`, `LogicStoreTests`, `LogicNoteStoreTests`,
  `LogicDtoSerializationTests`.
- Host: `LogicApiTests` via `TestAppHandle` (definitions CRUD, evaluated state with seeded
  mappings, notes), `AuthPolicyTests` additions.
- Dashboard: `DashboardPageTests` additions (nav/view/route/poll-dispatch pins, view scoping,
  brace balance) — the DOM/JS contract comment in `DashboardPage.cs` is updated with the new
  ids and functions.
- Mobile.Core: `MobileLogicApiClientTests` (against `TestAppHandle`), `BridgeProbeTests`,
  `LogicOverviewViewModelTests`, `LogicBlockViewModelTests`.
- Live verification: demo-seeded bridge, logic authored through the dashboard tab, states
  checked via `/api/logic/state` and the dashboard in a browser; APK built by the workflow and
  installed on the phone for the manual QA checklist.

## 9. Risks

- **MAUI Android toolchain is new to the repo**; the APK source of truth is the CI workflow
  (with `Dockerfile.mobile` as the local option). Server and Mobile.Core work is verifiable in
  the existing SDK container regardless.
- **TrustHmi exposure**: like the existing HMI write path, logic state/notes are readable (and
  notes writable) on the LAN when auth is enabled with the default `TrustHmi` setting —
  documented, and role-gated when `TrustHmi` is off.
- **Derived sequence semantics** are a deliberate v1 simplification; a real step state machine
  (manual completion, resets, history) is a follow-up.
