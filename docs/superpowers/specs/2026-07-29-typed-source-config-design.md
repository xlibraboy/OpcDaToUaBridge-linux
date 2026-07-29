# Typed Source Config (Phase 1)

- **Date:** 2026-07-29
- **Branch:** `feature/modif-bridge-app` (worktree `.worktrees/feature-modif-bridge-app`)
- **Status:** Design — pending approval before implementation
- **Primary surfaces:** `DaRuntimeSettings` / `SourceConfigDto` / `DaClientFactory` / `DaServerConfigRequest` / sources.json
- **Related:** multi-source factory pattern (`2026-07-27-opc-ua-source-design.md`, `2026-07-27-melsec-a3n-driver-design.md`); architecture decision: keep one `BridgeWorker`, typed config per driver
- **Non-goals (this phase):** `IDaClient` → `ISourceClient` rename; `DaItemId` rename; dirty-source-only reconfigure; new drivers; UI redesign; threading rewrite

## 1. Goal

Stop growing one fat source bag with every driver. Each external source keeps **shared identity** plus **typed options for its `SourceType` only**. Factory maps typed options → client. Bridge coordinator, mappings, links, poll/write tasks stay as-is.

**Success means:**

1. Runtime model has nested options: `OpcDa` | `OpcUa` | `Melsec` — not 20 sibling fields on one record.
2. `sources.json` can load **legacy flat** rows and **new nested** rows; save prefers nested.
3. `DaClientFactory` only reads the options block for the active type.
4. Existing API/UI still work (flat request body maps into nested model).
5. Adding source type N later = new options record + factory arm + DTO nest — not another ctor param on a mega-record.
6. OPC DA / UA / Melsec behavior unchanged.

## 2. Decisions locked

| Decision | Choice |
|---|---|
| Architecture | Keep `BridgeWorker` + `IDaClient` port; **config-only** split this phase |
| Runtime shape | Shared header + **one nested options object** per type |
| Disk format | Prefer nested JSON; **migrate flat → nested on load/save** |
| API surface | Keep flat `DaServerConfigRequest` for now; map at boundary |
| Factory | Switch on `SourceType`; build client from typed options only |
| Defaults / normalize | Per-type normalize inside each options type + shared header normalize |
| Unknown `SourceType` on load | Collapse to `OpcDa` (existing resilience); API still validates writes |
| Rename wave | **Out of scope** (`IDaClient`, `Da*`, `DaItemId` stay until Phase 2) |

## 3. Model

### 3.1 Shared header (every source)

| Field | Notes |
|---|---|
| `SourceId` | Unique key |
| `DisplayName` | UI label |
| `SourceType` | `OpcDa` \| `OpcUa` \| `MelsecA3n` |
| `UpdateRateMs` | Poll / publish interval for this source |
| `UseSubscriptions` | Prefer push when client supports it (DA/UA); ignored by Melsec |

Global snapshot fields stay: top-level `UpdateRateMs`, `UseSubscriptions`, `Sources`, `Version` (same as today).

### 3.2 Typed options

**`OpcDaSourceOptions`**

- `ProgId`, `Host`, `RemoteUsername`, `RemotePassword`, `RemoteDomain`

**`OpcUaSourceOptions`**

- `EndpointUrl`, `SecurityMode`, `SecurityPolicy`, `Username`, `Password`, `SessionTimeoutMs`, `ReconnectDelayMs`, `MaxMappedTags`

**`MelsecA3nSourceOptions`**

- `Transport`, `SerialPortName`, `BaudRate`, `DataBits`, `Parity`, `StopBits`, `StationNo`, `PcNo`, `TimeoutMs`, `RetryCount`

### 3.3 Runtime record

Replace the fat positional `DaSourceRuntimeSettings(...)` mega-ctor with:

```text
DaSourceRuntimeSettings(
  SourceId, DisplayName, SourceType,
  UpdateRateMs, UseSubscriptions,
  OpcDaSourceOptions? OpcDa,
  OpcUaSourceOptions? OpcUa,
  MelsecA3nSourceOptions? Melsec)
```

Invariants after normalize:

- Exactly the options object for `SourceType` is non-null (others null).
- Missing/partial options filled with type defaults (same numbers/strings as today’s `SourceConfigMigration.Normalize`).

Compatibility helpers on the record (temporary, keep call sites small):

- `ToOptions(bool useSubscriptions)` → `DaClientOptions` from `OpcDa` block
- `ToUaOptions(snapshot)` → `OpcUaSourceClientOptions` from `OpcUa` block
- Optional read-only projections used by Program/UI JSON if needed

Factory:

```text
OpcUa   → new OpcUaSourceClient(source.ToUaOptions(...))
Melsec  → new MelsecA3nClient(source.ToMelsecOptions())
else    → new OpcDaClient(source.ToOptions(...))
```

`ToMelsecOptions` moves next to the other mappers (on record or factory — one place only).

### 3.4 Persistence (`sources.json`)

**Write (new):**

```json
{
  "updateRateMs": 1000,
  "useSubscriptions": true,
  "sources": [
    {
      "sourceId": "kep",
      "displayName": "Kepware",
      "sourceType": "OpcUa",
      "updateRateMs": 1000,
      "useSubscriptions": true,
      "opcUa": {
        "endpointUrl": "opc.tcp://host:49320",
        "securityMode": "None",
        "securityPolicy": "None",
        "sessionTimeoutMs": 60000,
        "reconnectDelayMs": 5000,
        "maxMappedTags": 50000
      }
    },
    {
      "sourceId": "line1",
      "displayName": "Line 1",
      "sourceType": "OpcDa",
      "updateRateMs": 1000,
      "useSubscriptions": true,
      "opcDa": {
        "progId": "Matrikon.OPC.Simulation.1",
        "host": "localhost"
      }
    },
    {
      "sourceId": "a3n1",
      "displayName": "A3N",
      "sourceType": "MelsecA3n",
      "updateRateMs": 1000,
      "useSubscriptions": false,
      "melsec": {
        "transport": "Serial",
        "serialPortName": "/dev/ttyUSB0",
        "baudRate": 9600,
        "dataBits": 8,
        "parity": "Odd",
        "stopBits": "One",
        "stationNo": "00",
        "pcNo": "FF",
        "timeoutMs": 3000,
        "retryCount": 2
      }
    }
  ]
}
```

**Read:**

1. If nested object present for type → use it.
2. Else if legacy flat fields present → map into nested options (one-shot migration path).
3. Normalize; next `Persist()` rewrites nested.

`SourceConfigDto` gains optional nests `OpcDa`, `OpcUa`, `Melsec` **and keeps flat properties** for one release so old files and any external tools still deserialize.

### 3.5 API boundary

`POST` source config keeps **flat** `DaServerConfigRequest` (dashboard JS already posts flat). Map in Program:

```text
request + SourceType → DaSourceRuntimeSettings with one nested options object
```

No dashboard rewrite in Phase 1. Optional later: nested request body.

Export/import path that builds `DaSourceRuntimeSettings` from raw JSON uses the same DTO migration (`FromDto`).

## 4. Touch list (implementation scope)

| Area | Change |
|---|---|
| `DaRuntimeSettings.cs` | Nested options types; slim record; `FromDto` / `Normalize` / `ToDto`; persist nested |
| `DaClientFactory.cs` | Use nested mappers only; drop flat field reads |
| `Program.cs` | Map flat request → nested settings; any ad-hoc `new DaSourceRuntimeSettings` |
| Tests | Update `new DaSourceRuntimeSettings(` call sites; add load flat + load nested + round-trip persist tests |
| Docs | This spec; plan file under `docs/superpowers/plans/` |

**Out of touch:** `BridgeWorker`, `IDaClient`, mapping/link stores, drivers’ wire protocols, dashboard HTML/JS (unless a compile break forces a tiny fix).

## 5. Test plan

1. **Normalize unknown type** → `OpcDa` (existing).
2. **FromDto flat legacy UA/Melsec/DA** → correct nested block, other nests null.
3. **FromDto nested** → same.
4. **Persist round-trip** → nested JSON on disk; reload equal.
5. **Factory** → UA/Melsec/DA still construct correct client type (existing factory tests adapted).
6. **API upsert** (existing UA/Melsec API tests) still pass with flat body.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Many `new DaSourceRuntimeSettings(` call sites | Add small factory helpers `CreateDa` / `CreateUa` / `CreateMelsec` on `DaRuntimeSettings` or static helpers |
| Dashboard assumes flat GET JSON | GET can still project flat fields for UI **or** keep dual write of flat+nested on DTO until UI updated — prefer **project flat on GET** from nested so UI unchanged |
| Password fields move under nest | Same threat model; paths change only in JSON layout |

**GET projection (locked):** API responses that today return flat source objects continue to return flat shape built from nested options so Connectivity UI needs zero change in Phase 1.

## 7. Follow-ups (not this phase)

- Phase 2: rename `IDaClient` → `ISourceClient`, `DaItemId` → `ItemId`, store/type renames.
- Phase 3: reconfigure only dirty `SourceId`s.
- Nested API request body + UI forms bound to type-specific panels only.
- New drivers add options record + nest key only.

## 8. Approval checkpoint

Implement only after explicit approval of this design (especially: nested runtime + nested disk, flat API/GET projection kept).
