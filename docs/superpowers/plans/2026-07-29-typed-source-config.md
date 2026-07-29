# Typed Source Config (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fat `DaSourceRuntimeSettings` bag with shared header + nested per-type options; load flat legacy `sources.json`; persist nested; keep flat API/UI via projection.

**Architecture:** One runtime record with `OpcDa` / `OpcUa` / `Melsec` nests; compat getters for existing call sites; `SourceConfigMigration` dual-load; factory reads nests only.

**Tech Stack:** .NET 8, System.Text.Json, xUnit

**Spec:** `docs/superpowers/specs/2026-07-29-typed-source-config-design.md`

**Note:** `MaxMappedTags` lives on the **shared header** (UA + Melsec both enforce it), not only under `opcUa`.

---

### Task 1: Nested options model + migration

**Files:**
- Modify: `src/OpcBridge.App/DaRuntimeSettings.cs`
- Modify: `tests/OpcBridge.LoadTest/UaSourceSettingsTests.cs`
- Modify: `tests/OpcBridge.LoadTest/MelsecSourceSettingsTests.cs`
- Modify: `tests/OpcBridge.LoadTest/DaClientFactoryTests.cs`
- Modify: `tests/OpcBridge.LoadTest/MelsecApiTests.cs` (helpers that `new DaSourceRuntimeSettings`)

- [ ] Step 1: Add option records + slim runtime record with compat getters
- [ ] Step 2: Rewrite `FromDto` / `Normalize` / `ToDto` / `Persist` for nested (+ flat legacy load)
- [ ] Step 3: Update `CreateDaSource` and all `new DaSourceRuntimeSettings` call sites
- [ ] Step 4: Update settings tests (flat load, nested load, unknown type); add persist round-trip if cheap
- [ ] Step 5: `dotnet test` on LoadTest filter for settings/factory — pass

### Task 2: Factory + Program boundaries

**Files:**
- Modify: `src/OpcBridge.App/DaClientFactory.cs`
- Modify: `src/OpcBridge.App/Program.cs` (upsert, import, `ToSourceApiDto` if needed — prefer compat getters)

- [ ] Step 1: Factory maps from nests only
- [ ] Step 2: Upsert/import build nested settings from flat request/JSON
- [ ] Step 3: Confirm `ToSourceApiDto` still flat via getters
- [ ] Step 4: `dotnet test` UA/Melsec API + factory tests — pass

### Task 3: Verify + commit

- [ ] Step 1: Full `dotnet test tests/OpcBridge.LoadTest` (or solution tests)
- [ ] Step 2: Commit

---

## Execution handoff

Plan complete and saved. Two execution options:

**1. Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks

**2. Inline Execution** — execute tasks in this session with checkpoints

Which approach?
