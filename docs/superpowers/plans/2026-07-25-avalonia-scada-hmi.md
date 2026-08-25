# Avalonia SCADA HMI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship multi-bridge SCADA Runtime + standalone Designer with popup faceplate/trend and primary-bridge display store, per `docs/superpowers/specs/2026-07-25-avalonia-scada-hmi-design.md`.

**Architecture:** Shared JSON `DisplayDocument` + widget models; Runtime and Designer are separate Avalonia exes; every `OpcBridge.App` hosts display CRUD; live tags stay on per-bridge REST/SignalR with `Hmi:BroadcastFlushMs` (50–1000, default 100).

**Tech Stack:** .NET 8, Avalonia 11, CommunityToolkit.Mvvm, xUnit, Docker `mcr.microsoft.com/dotnet/sdk:8.0` build gate.

**Worktree:** `.worktrees/feature-avolonia-hmi-polish` on `feature/avolonia-hmi-polish`.

---

## File structure (target)

| Path | Responsibility |
|---|---|
| `src/OpcBridge.Client/DisplayDocumentDto.cs` (+ list/widget/binding DTOs) | Wire contracts |
| `src/OpcBridge.App/Hmi/DisplayStore.cs` | File-backed display CRUD + versioning |
| `src/OpcBridge.App/Hmi/HmiOptions.cs` | `BroadcastFlushMs` |
| `src/OpcBridge.App/Hmi/HmiBroadcastService.cs` | Use configured flush |
| `src/OpcBridge.App/Program.cs` | Register store + `/api/hmi/displays*` |
| `src/OpcBridge.Hmi.Core/` | Pure models, binding keys, MultiBridgeTagCache logic |
| `src/OpcBridge.Hmi/` | Runtime shell, widgets, Faceplate/Trend windows |
| `src/OpcBridge.Hmi.Designer/` | Design shell |
| `tests/OpcBridge.LoadTest/HmiDisplayApiTests.cs` | Display store API |
| `tests/OpcBridge.LoadTest/DisplayDocumentDtoTests.cs` | JSON round-trip |
| `tests/OpcBridge.LoadTest/DisplayStoreTests.cs` | Store unit tests (no full host where possible) |

---

### Task 1: Display DTOs + unit round-trip

**Files:**
- Create: `src/OpcBridge.Client/DisplayDocumentDto.cs`
- Create: `src/OpcBridge.Client/DisplayListResponse.cs` (or same file)
- Create: `tests/OpcBridge.LoadTest/DisplayDocumentDtoTests.cs`

- [ ] **Step 1: Write failing DTO round-trip test**

```csharp
[Fact]
public void DisplayDocument_RoundTripsJson()
{
    var doc = new DisplayDocumentDto
    {
        SchemaVersion = 1,
        Id = "plant-overview",
        Name = "Plant Overview",
        Version = 1,
        Width = 1920,
        Height = 1080,
        Widgets =
        [
            new DisplayWidgetDto
            {
                Id = "w1",
                Type = "numeric",
                X = 40, Y = 80, W = 160, H = 48,
                Props = new Dictionary<string, JsonElement>
                {
                    ["label"] = JsonSerializer.SerializeToElement("Tank")
                },
                Binding = new TagBindingDto
                {
                    BridgeId = "line1",
                    SourceId = "default",
                    DaItemId = "Tank.Level"
                }
            }
        ]
    };
    string json = JsonSerializer.Serialize(doc);
    var back = JsonSerializer.Deserialize<DisplayDocumentDto>(json);
    Assert.NotNull(back);
    Assert.Equal("plant-overview", back!.Id);
    Assert.Single(back.Widgets);
    Assert.Equal("line1", back.Widgets[0].Binding!.BridgeId);
}
```

- [ ] **Step 2: Run test — expect compile fail / missing types**

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj -c Release --filter DisplayDocument_RoundTripsJson
```

- [ ] **Step 3: Add DTO types in Client** (camelCase JSON via default ASP.NET / explicit PropertyNamingPolicy as needed to match existing HmiTagDto style — property names Pascal in C#, System.Text.Json default case-insensitive deserialize).

- [ ] **Step 4: Re-run test — PASS**

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.Client tests/OpcBridge.LoadTest/DisplayDocumentDtoTests.cs
git commit -m "feat(hmi): add display document DTOs"
```

---

### Task 2: DisplayStore service (unit tests first)

**Files:**
- Create: `src/OpcBridge.App/Hmi/DisplayStore.cs`
- Create: `tests/OpcBridge.LoadTest/DisplayStoreTests.cs`

Behaviors:
- Root dir: `Path.Combine(baseDir, "displays")`
- Id allowlist: `^[a-zA-Z0-9_-]+$`
- `List()`, `TryGet(id)`, `Put(doc)` create/update with version, `Delete(id)`
- Put create → version 1; update requires matching version → bump; mismatch → conflict result
- Atomic write (temp + replace)
- schemaVersion must be 1; unique widget ids; positive sizes

- [ ] **Step 1: Failing tests** — create, get, list, update bump, 409 conflict, bad id, bad schema, delete

- [ ] **Step 2: Implement `DisplayStore`**

- [ ] **Step 3: Tests PASS in Docker**

- [ ] **Step 4: Commit** `feat(hmi): add DisplayStore file persistence`

---

### Task 3: HTTP endpoints + API tests

**Files:**
- Modify: `src/OpcBridge.App/Program.cs` — `AddSingleton<DisplayStore>()`, map GET list/get, PUT, DELETE
- Create: `tests/OpcBridge.LoadTest/HmiDisplayApiTests.cs`

Endpoints:
- `GET /api/hmi/displays` → `{ items: [...] }`
- `GET /api/hmi/displays/{id}` → 200 doc / 404
- `PUT /api/hmi/displays/{id}` → 200 doc / 400 / 409
- `DELETE /api/hmi/displays/{id}` → 204 / 404

- [ ] **Step 1: API tests with `TestAppHandle` (fail 404)**

- [ ] **Step 2: Wire Program.cs**

- [ ] **Step 3: PASS**

- [ ] **Step 4: Commit** `feat(hmi): expose /api/hmi/displays CRUD`

---

### Task 4: Configurable broadcast flush

**Files:**
- Create: `src/OpcBridge.App/Hmi/HmiOptions.cs` (`BroadcastFlushMs`)
- Modify: `HmiBroadcastService.cs`, `Program.cs`, `appsettings.json`
- Test: unit or options clamp test

- [ ] Clamp 50–1000, default 100
- [ ] Commit `feat(hmi): configurable Hmi:BroadcastFlushMs`

---

### Task 5: S1 Multi-bridge foundation

**Files:**
- Create project `OpcBridge.Hmi.Core` (or Client helpers first if smaller)
- `TagBindingKey`, extend cache with `bridgeId`
- Runtime config model `hmi-config.json`
- Refactor `MainViewModel` toward multi-bridge connection manager (keep single-bridge compat)

TDD: cache merge tests in LoadTest referencing Core/Client.

---

### Task 6: S2 Popup Faceplate + Trend

**Files under `OpcBridge.Hmi`:**
- `Views/FaceplateWindow.axaml(.cs)`, `ViewModels/FaceplateViewModel.cs`
- `Views/TrendWindow.axaml(.cs)`, `ViewModels/TrendViewModel.cs`
- `Services/PopupWindowService.cs` — one window per tag key
- Replace inline faceplate panel usage with popup open from selection / later widgets

Manual smoke on Windows; VM logic unit-tested where possible.

---

### Task 7: S3 Runtime DisplaySurface + widgets

- Load document from store
- Widget UserControls: label, numeric, qualityLamp, boolIndicator, pushButton
- Click → faceplate
- Unbound / unknown bridge / unknown type placeholders

---

### Task 8: S4 Designer app

- New `OpcBridge.Hmi.Designer` WinExe
- Palette, select/move, properties, Save PUT
- Share widget views/models with Runtime

---

### Task 9: S5 Polish + docs

- Hybrid shell, startup display, dark theme
- Help/context.md: preserve `displays/`, multi-bridge config
- Full Docker build+test gate

---

## Build / test commands (always from worktree root)

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c 'dotnet build -c Release && dotnet test -c Release && node --check src/OpcBridge.App/wwwroot/js/DashboardPage.js'
```

(JS check only if dashboard JS touched.)

## Execution note

Implement **Task 1 → 4 (S0)** fully before S1. Do not start Designer until Runtime can load a hand-written display JSON from the store.
