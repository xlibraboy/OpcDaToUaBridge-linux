# DA worker isolation + PMD containment — implementation plan

**Date:** 2026-10-01 · **Spec:** `docs/superpowers/specs/2026-10-01-da-worker-isolation-design.md` ·
**Branch:** `feature/da-containment-and-workers` (single branch for the whole roadmap; not merged)

**Goal:** contain the PMD-class in-proc crash with host tools (Phase 0) and per-source worker
processes (Phase 1), with a dashboard board to configure and control them.

## Phase 0 — host tools (no app change)

- [x] `scripts/windows/enable-wer-localdumps.ps1` — WER LocalDumps for `OpcBridge.App.exe`/`DllHost.exe`, read-back verified, `-Remove`; tests: PowerShell AST parse + non-elevated guard path.
- [x] `scripts/windows/enable-pmd-surrogate.ps1` — system COM surrogate (`DllSurrogate` + `RunAs`) on the PMD AppID with a reg-export/JSON rollback record and `-Rollback`; refuses on `LocalServer32`, reports the batch logon right.
- [x] Field notes: capture + surrogate procedures, verification, risks, Change/Where/Rollback table.
- [ ] Lab: run both on a PMD host, record results in the field notes + ISSUES.md (M8).

## Phase 1 — worker processes

- [x] **M2 config** — `DaWorkerOptions`/`DaWorkerModes` on `OpcDaSourceOptions`, `SecretProtector` (DPAPI), `DaWorkerOptionsValidator`, upsert `worker` block, `SetSourceWorker`, session equality. Tests: `DaWorkerOptionsTests`, `DaWorkerOptionsValidatorTests`, `SecretProtectorTests`, `SourceConnectionEqualsTests`.
- [x] **M1 protocol** — `OpcBridge.Client/Workers`: `WorkerProtocol`, `FrameCodec` (4-byte LE length, 4 MiB cap), `WireValue`/`WireValueCodec`. Tests: `DaWorkerProtocolTests`.
- [x] **M3 child mode** — `DaWorkerHost` (`--da-worker`, early dispatch), `DaWorkerSession` (clients, requests, values, heartbeat, exit contract), `DaChildProcess.Resolve`. Tests: `DaWorkerHostTests` (incl. a spawned process on Linux).
- [x] **M4 supervisor** — `WorkerPlacement`, `WorkerLifecyclePolicy`, `WorkerConnection`, `DaWorkerProcess`, `DaWorkerSupervisor`, `WorkerSourceClient`, `RoutingSourceClientFactory`, `IRateGroupBoundSource`. Tests: placement/policy/proxy-over-duplex/routing.
- [x] **M4b run-as spawn** — `DaWorkerIdentity` (`LogonUser` batch + `DuplicateTokenEx` + `CreateProcessAsUser`, inherited pipes, error taxonomy), `DaWorkerChild`. Tests: normalization, quoting, platform/missing-password guards. Lab: M8.
- [x] **M5 API** — `GET /api/workers`, `POST …/restart`, `POST …/kill?confirm=true`, AuthPolicy row, supervisor history. Tests: `WorkersApiTests`, `AuthPolicyTests`, `AuthApiTests`.
- [x] **M6 board** — nav/view/route/role reveal/poller + cards, timeline, restart/kill; contract tests in `DashboardPageTests`.
- [x] **M6b source form** — Worker Process section (mode/account/password/domain), load/save wiring incl. "blank keeps"; contract test + worker-block upsert API test.
- [x] **M7 docs** — CHANGELOG `[Unreleased]`, Help guide section, `docs/da-worker-isolation.md`, ISSUES.md PMD update, plant-deploy row, context.md section + stamp, this spec/plan pair.
- [ ] **M8 lab** — Windows host checklist:
  - [ ] PMD (or the injected fault server) in `own` mode with subscriptions: worker dies, parent PID/`/health` unchanged, board shows crash + restart, values resume.
  - [ ] Worker under `.\mesadm1` while the service is `LocalSystem` (privileged spawn) — PMD activation succeeds.
  - [ ] Two group sources share one pid; removing one leaves the other flowing.
  - [ ] `taskkill /f` the parent → workers gone within ~5 s.
  - [ ] Board restart/kill live; viewer POST → 403.
  - [ ] Regression: in-process sources unchanged; full suite green; Windows publish 0/0.

## Global constraints

- `net8.0`, Windows-only paths guarded (`[SupportedOSPlatform]` + `OperatingSystem.IsWindows()`), zero-warning build.
- Credentials never in argv; run-as password never echoed by the API.
- Tests run on Linux: Windows-only behaviour must degrade to explicit errors, and the privileged spawn stays on the lab checklist.
