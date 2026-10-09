# Mobile Logic Viewer Implementation Plan

**Goal:** A first-class *logic* model in the bridge (interlocks, permissives, ordered
sequences over live tags), authored in a new dashboard **Logic** tab, evaluated server-side,
pushed over SignalR, and shown in a new Android app (`OpcBridge.Mobile`, .NET MAUI, C#) that
also lets the operator write action tags and leave notes.

**Architecture:** Definitions live in `logic.json` behind a `LogicStore` (mirrors
`InterlinkStore`); a pure `LogicStateEvaluator` derives per-block/per-condition/per-step state
from `BridgeState` + `MappingStore`; `/api/logic*` serves definitions, evaluated state
(`/api/logic/state`, HMI path) and notes (`/api/logic/notes`, HMI path + Operator); the
existing `HmiBroadcastService` flush tick pushes a `logic` message with a `LogicStateSnapshot`
only when the state signature changes. The phone (MAUI) speaks HTTP + the `/hmi` hub and uses
`POST /api/hmi/write` for actions; all client logic sits in `OpcBridge.Mobile.Core` (net8.0)
so the existing xUnit suite covers it.

**Tech Stack:** .NET 8 (net8.0 server/lib, net8.0-android app), C# 12, xUnit,
System.Text.Json, ASP.NET minimal APIs, SignalR, CommunityToolkit.Mvvm 8.3.2, MAUI.

**Spec:** `docs/superpowers/specs/2026-10-08-mobile-logic-viewer-design.md`

## Global Constraints

- Worktree: `.worktrees/mobile-logic-viewer` (branch `feature/mobile-logic-viewer`) — all
  commands run from there; the repo root's `main` copy holds the user's WIP and is untouched.
- Build/test via Docker SDK container (no host SDK): the two recipes in `AGENTS.md`
  (`mcr.microsoft.com/dotnet/sdk:8.0`, `-u "$(id -u):$(id -g)"`, `.nuget-cache` mount).
- Existing suite must stay green at every commit; dashboard JS must satisfy
  `DashboardPageTests.Script_HasBalancedBraces` and the DOM/JS contract comment in
  `src/OpcBridge.App/DashboardPage.cs`.
- House API style: `{ error }` bodies, `Results.Json(new { … version })`, camelCase JSON.
- Conventional commits (`feat(logic): …`, `feat(mobile): …`); server `CHANGELOG.md`
  `[Unreleased]` entry in the same commits as the change.
- `OpcBridge.Mobile` (android TFM) is NOT added to `OpcBridge.sln`; `OpcBridge.Mobile.Core`
  IS added and referenced by `tests/OpcBridge.LoadTest`.
- Test files flat in `tests/OpcBridge.LoadTest/`, `<Subject>Tests.cs`, app-backed tests use
  `[Collection(nameof(InterlinkApiAppCollection))]` + `TestAppHandle`.

---

### Task 1: Shared DTOs — `src/OpcBridge.Client/LogicDtos.cs`, `LogicStateDtos.cs`
- Definition types (`LogicBlockDto`, `LogicConditionDto`, `LogicStepDto`, `LogicActionDto`),
  state types (`LogicStateSnapshot`, `LogicBlockStateDto`, `LogicConditionStateDto`,
  `LogicStepStateDto`), notes types (`LogicNoteDto`, `LogicNoteAddRequest`), constants
  (`LogicBlockKinds`, `LogicConditionOps`, `LogicBlockStates`, `LogicStepStates`,
  `LogicConditionStates`, `LogicConditionSeverities`). Mutable POCOs, camelCase on the wire.
- Test: `LogicDtoSerializationTests` — round-trip and constant stability.

### Task 2: Stores — `src/OpcBridge.App/LogicStore.cs`, `LogicNoteStore.cs`
- Mirror `InterlinkStore` (lock, `version_`, `GetSnapshot`, `TryAdd/TryUpdate/TryRemove`,
  `SetAll`, `Persist` swallow, `LoadFromDisk` null-on-error, `Changed` event for the
  broadcaster, blank-SourceId normalization), paths `logic.json` / `logic-notes.json`.
- Validation all-or-nothing; notes append-only capped at 500.
- Register both singletons beside `InterlinkStore`; tests `LogicStoreTests`,
  `LogicNoteStoreTests`.

### Task 3: Evaluator — `src/OpcBridge.App/LogicStateEvaluator.cs`
- Pure static; input = block + mappings + value lookup + `NowUtc`; per §4 of the spec.
- Tests: `LogicStateEvaluatorTests`.

### Task 4: API — `Program.cs`, `Auth/AuthPolicy.cs`
- `GET /api/logic`, `POST /api/logic/blocks`, `DELETE /api/logic/blocks/{id:guid}`,
  `GET /api/logic/state`, `GET|POST /api/logic/notes` per the spec table.
- `HmiPaths += /api/logic/state, /api/logic/notes`; `Overrides += (/api/logic/notes, POST,
  Operator)`.
- Tests: `LogicApiTests` (TestAppHandle), `AuthPolicyTests` additions.

### Task 5: Push — `src/OpcBridge.App/Hmi/HmiBroadcastService.cs`
- Evaluate on the flush tick; keep the last signature; send `"logic"` when it changes;
  re-evaluate on `LogicStore.Changed` / `MappingStore.Changed`.
- Tests: broadcast-level tests (no send when unchanged, send on change).

### Task 6: Dashboard tab — `DashboardPage.cs`, `DashboardPageTests.cs`
- Nav `tags/logic` after Interlinks; `view-logic`; `ROUTE_TO_TAB` + `showTab` loader +
  refresh() dispatch; `loadLogic()`, `renderLogicView()` and the editor JS modelled on the
  interlink module; live badges from `state.logicStateById`.
- Update the file-header contract comment and `DashboardPageTests` (nav/view/route/poll pins,
  view scoping, brace balance).

### Task 7: Mobile.Core — `src/OpcBridge.Mobile.Core/`
- `LogicApiClient` (state/tags/write/notes/login; `BridgeApiClient` HttpClient shape),
  `MobileHubClient` (`HmiHubClient` + `logic` handler), `BridgeProbe` (host-entered
  8080–8180 probe on `/api/status/ports`), view models `LogicOverviewViewModel`,
  `LogicBlockViewModel`, `SettingsViewModel`.
- Add project to the solution + LoadTest references; tests `MobileLogicApiClientTests`,
  `BridgeProbeTests`, `LogicOverviewViewModelTests`, `LogicBlockViewModelTests`.

### Task 8: Android app — `src/OpcBridge.Mobile/` (net8.0-android, out of the solution)
- MAUI single project; manifest with `INTERNET` + `usesCleartextTraffic`; pages Overview,
  Block detail, Settings, Login; live snapshot + hub + polling fallback; state chips with
  text+colour; confirm dialogs on actions.

### Task 9: Version/release — `Directory.Build.props`, mobile `CHANGELOG.md`,
`ChangelogTests`, `.github/workflows/mobile-release.yml`, `Dockerfile.mobile`, `AGENTS.md`.
- Server `CHANGELOG.md` `[Unreleased]` entry.

### Task 10: Docs + verification — `context.md`, `AGENTS.md`; full suite in Docker; live
bridge with the demo seed; browser pass on the dashboard tab; `/api/logic/state` + notes curl
checks; workflow APK build.

---
