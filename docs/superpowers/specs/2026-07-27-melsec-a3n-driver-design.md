# Mitsubishi A3N PLC Driver Design

- **Date:** 2026-07-27
- **Branch:** `feature/mhi-plc-driver` (worktree `.worktrees/feature-mhi-plc-driver`)
- **Status:** Approved design (§1–§4), pending implementation plan
- **Primary surfaces:** source registry / `DaClientFactory` / `BridgeWorker`, new MELSEC client, Connectivity **Drivers** UI
- **Related:** multi-type source pattern in `feature/opc-ua-source` (`docs/superpowers/specs/2026-07-27-opc-ua-source-design.md`); Connectivity IA in `docs/superpowers/specs/2026-07-27-connectivity-opc-da-tab-design.md`; architecture in `context.md`
- **Protocol refs:** Pro-face *Melsec-A CPU (SIO) Driver* manual; user-confirmed **MELSEC A-compatible 1C Frame** (Dedicated Protocol / Format 1) over RS-232

## 1. Goal

Add **Mitsubishi MELSEC A3NCPU** as an external inbound **source**, product-labeled a **Driver**. The bridge opens a **serial client** to the PLC’s RS-232 programming/CPU port, maps selected device addresses, polls them into the existing bridge pipeline, and re-publishes on the outbound path (this process’s OPC UA server, MQTT, Influx, HMI). Writes on writeable mapped tags go **back** to the PLC (write-through).

This is **not** OPC DA/UA and **not** modern Ethernet MC Protocol 3E/4E. Wire protocol for v1 is **MELSEC A-compatible 1C Frame** (Dedicated Protocol / Format 1) on host serial.

```text
A3NCPU RS-232  --(1C Frame serial)-->  OpcDaToUaBridge  --(our UA server)--> SCADA / HMI
OPC DA servers --(DA client)------->         ↑
```

**Success means:**

1. Create and configure one or more Mitsubishi A3N drivers under Connectivity → **Drivers**.
2. Map core devices (D / M / X / Y) with explicit addresses; live values via **poll**.
3. **Write-through** for mappings with `Writeable=true`.
4. Existing OPC DA sources and outbound endpoints remain unchanged in behavior.
5. Protocol codec is unit-testable without hardware; client is Linux-capable via `/dev/tty*`.

## 2. Decisions locked in brainstorming

| Decision | Choice |
|---|---|
| Product naming | UI: **Driver** / Drivers list; code discriminator: `SourceType = MelsecA3n` |
| PLC target | Mitsubishi **A3NCPU** (MELSEC-A / AnN class device map) |
| Protocol | **MELSEC A-compatible 1C Frame** (Dedicated Protocol / Format 1) |
| Transport v1 | **Host serial only** (e.g. `/dev/ttyUSB0`); TCP serial-device-server **later** behind a transport seam |
| I/O scope v1 | **Read + write**, limited devices: **D, M, X, Y** only |
| Address form | Native device + optional bit suffix: `D100`, `M10`, `X20`, `Y0F`, bit-in-word `D100:8` or `D100.8` |
| Connectivity IA | **Generic PLC Drivers list** + wizard type step (A3N first) |
| Architecture | **A** — `SourceType` + `IDaClient` factory branch; reuse `BridgeWorker` sessions, mappings, write queue, UA mirror |
| Value updates | **Poll only** (no PLC change-subscription in v1) |
| Item identity | Keep mapping key `(SourceId, DaItemId)`; `DaItemId` = MELSEC device address string |

## 3. Non-goals (v1)

- TCP tunnel / serial device server (config enum reserved; no implementation)
- Devices beyond D/M/X/Y (TN/CN/TS/TC/CS/CC/B/W/R/L/F, special M9xxx/D9xxx) unless free with the same codec path — **not required**
- Ethernet MC Protocol (1E/3E/4E) as primary path
- Computer-link multi-drop station networks as a first-class topology (StationNo/PcNo remain configurable for CPU-direct defaults)
- OPC-style tree browse of PLC memory
- Full rename `IDaClient` → `ISourceClient`
- Parallel Drivers value bus / second BridgeWorker
- Placing MELSEC code inside `OpcBridge.Da` (COM)

## 4. Scale & performance contract

A3N serial is orders of magnitude slower than OPC. Design for **correct batching**, not 20k-tag Ethernet scale.

| Rule | Contract |
|---|---|
| What is hot | Only tags present in `MappingStore` for that `SourceId` |
| Ingest | **Poll** rate groups only (existing BridgeWorker pollers) |
| Batching | Group consecutive same-device addresses into 1C batch reads; respect protocol consecutive limits (AnN-class: treat **64 words** / **~32–256 bits** as soft caps until fixtures lock exact limits) |
| Writes | Per-tag via `WriteQueue`; bit-in-word uses read-modify-write on the holding word |
| Caps | Optional `MaxMappedTags` per driver; default modest (e.g. **2000**) with hard fail on exceed |
| Dashboard | Drivers list O(sources); tag map is address entry, not full memory dump |
| Port ownership | **One exclusive open** per serial port per process; two sources must not share the same `SerialPortName` |

## 5. Configuration model

### 5.1 Source type

Every source gains:

- `SourceType`: `"OpcDa"` | `"MelsecA3n"` (and `"OpcUa"` when that branch merges)
- Missing / null in existing `sources.json` → **`OpcDa`** (backward compatible)
- Unknown type on **load** → normalize to `OpcDa` and log a warning (same migration posture as UA-source plan); on **API write** reject unknown types once multi-type is live

### 5.2 Shared identity fields

| Field | Notes |
|---|---|
| `SourceId` | Unique key; outbound NodeIds `ns=2;s={SourceId}/…` |
| `DisplayName` | UI label |
| `UpdateRateMs` | Default poll interval for the source |
| `SourceType` | Discriminator |

### 5.3 OPC DA fields

Unchanged; used only when `SourceType == OpcDa`.

### 5.4 Mitsubishi A3N fields

| Field | Purpose | v1 default / notes |
|---|---|---|
| `Transport` | `Serial` \| `TcpTunnel` | **`Serial` only implemented** |
| `SerialPortName` | e.g. `/dev/ttyUSB0`, `COM3` | required for Serial |
| `BaudRate` | serial speed | **9600** |
| `DataBits` | | **8** |
| `Parity` | `None` \| `Odd` \| `Even` | **Odd** (Pro-face env) |
| `StopBits` | `One` \| `OnePointFive` \| `Two` | **One** |
| `StationNo` | 1C station field | default for CPU direct (commonly `00` / 0 — lock in codec fixtures) |
| `PcNo` | 1C PC number | default for CPU direct (commonly `FF` — lock in codec fixtures) |
| `TimeoutMs` | receive timeout | e.g. **3000** (configurable; Pro-face lists up to 10s) |
| `RetryCount` | retries on timeout/sum-check | **2** |
| `MaxMappedTags` | hard cap | e.g. **2000** |
| `Host` / `Port` | reserved for future TcpTunnel | ignored in v1 Serial |

No password fields for serial v1.

### 5.5 Persistence

- Continue **`sources.json`** via existing runtime settings service.
- Extend `DaSourceRuntimeSettings` / DTOs / API request bodies with the fields above (nullable / ignored for other types).
- Export/import include `SourceType` + non-secret serial fields.
- Seed from appsettings remains DA-oriented; v1 runtime creation via API/UI.

### 5.6 Mapping identity & address syntax

- Keep `TagMapping` key `(SourceId, DaItemId)` case-insensitive.
- For A3N, **`DaItemId` = device address string**, canonicalized on map save.
- Accepted forms:
  - Word/bit devices: `D100`, `M10`, `X20`, `Y0F` (X/Y **octal** digits; D/M **decimal**)
  - Bit-in-word: `D100:8` or `D100.8` (bit 0–15)
- Reject unsupported device letters and out-of-range AnN addresses on map upsert (**hard reject**).
- AnN reference ranges (from Pro-face AnN table; enforce as v1 caps):

  | Device | Bit range | Word range |
  |---|---|---|
  | X | X0000–X07FF (octal) | word-aligned X…0 |
  | Y | Y0000–Y07FF (octal) | word-aligned Y…0 |
  | M | M0000–M2047 | multi of 16 for word view |
  | D | — | D0000–D1023 (+ bit `:0`–`:15`) |

- Default outbound bridge NodeId remains `ns=2;s={SourceId}/{DaItemId}` with existing sanitization for outbound only; **stored** `DaItemId` stays the PLC address used for Read/Write.
- `DataType`, `Writeable`, `AccessRights`, `PollRateMs`, `MqttEnabled`, `InfluxEnabled`, `Mode`/`ManualValue` keep current meanings. Manual mode still bypasses source reads for that tag.

## 6. Runtime architecture

### 6.1 Approach A (locked)

1. **Registry** — versioned source snapshot includes `SourceType` + A3N fields.
2. **Factory** — `DaClientFactory` returns:
   - `OpcDaClient` for `OpcDa`
   - `MelsecA3nClient` for `MelsecA3n`
   - (later) `OpcUaSourceClient` for `OpcUa`
3. **`BridgeWorker`** — existing `ReconfigureSessionsAsync` / `SourceSession` / pollers / `WriteQueue`.
4. **Normalized unit** — still `BridgeValue(SourceId, DaItemId, Value, TimestampUtc, DaQuality, IsGood)`.
5. **Outbound** — unchanged.

### 6.2 Client seam

Implement `MelsecA3nClient : IDaClient`:

- `ConnectAsync` — open serial port exclusively; optional lightweight probe (e.g. short device read or link check if defined); set Connected/Faulted
- `ReadAsync(mappings)` — parse addresses, batch by device type + consecutive runs, issue 1C reads, map to `BridgeValue[]`
- `WriteAsync(daItemId, value)` — word write or bit write (bit device or bit-in-word RMW)
- `TryGetTagMetadata` — from address parse (Boolean vs Int16/UInt16, access rights)
- `IAsyncDisposable` — close port

No COM/STA. Prefer single-flight I/O lock on the port (serial is half-duplex request/response).

### 6.3 Project placement

- New assembly: **`OpcBridge.Drivers.Melsec`** (preferred name; alternative `OpcBridge.Melsec`).
- Layers inside assembly:
  - `Protocol/` — 1C frame codec (pure, no I/O)
  - `Transport/` — `IMelsecTransport` + `SerialMelsecTransport` (v1); future `TcpTunnelMelsecTransport`
  - `Addressing/` — parse/canonicalize/validate device ids
  - `MelsecA3nClient` — `IDaClient` orchestration
- **Do not** put this in `OpcBridge.Da`.
- App references the drivers project; factory constructs the client.

### 6.4 Protocol (1C Frame)

- ASCII Dedicated Protocol **Format 1 / A-compatible 1C** over RS-232.
- Control characters and sum-check as defined for 1C (ENQ-led requests, sum check, CR terminator — exact byte layouts fixed with **unit fixtures** from protocol references during implementation; do not invent alternate framing).
- Support only commands needed for D/M/X/Y batch read and write (bit and word as required by device class).
- On NAK / sum-check fail / timeout: retry up to `RetryCount`, then surface error to caller / Faulted path.
- Quality mapping: successful parse → Good; timeout/protocol error for an item → Bad; do not crash the process.

Implementation must document the locked frame examples in tests (request/response hex or ASCII strings).

### 6.5 Transport seam

```text
IMelsecTransport
  OpenAsync / CloseAsync
  TransactAsync(ReadOnlyMemory<byte> request, timeout) → response bytes
```

v1: `SerialMelsecTransport` using `System.IO.Ports.SerialPort` (or a thin abstraction if package policy prefers).  
Later: TCP client that forwards raw 1C bytes to a serial device server — **same codec**.

### 6.6 Connect lifecycle

On add/update source or settings version bump:

1. Dispose previous session for that `SourceId` if any.
2. Create client from factory.
3. `ConnectAsync`: configure serial, open port, optional probe.
4. Mark `BridgeState` Connected / Faulted with clear errors.
5. Pollers pick up enabled non-Manual mappings.

Empty / missing `SerialPortName` → Disconnected with explicit error (mirror empty `ProgId` for DA).  
Port busy / access denied → Faulted + LastError; process stays up; rebuild path unchanged.

### 6.7 Poll path

- Existing per-`(SourceId, PollRateMs)` pollers call `ReadAsync`.
- Client coalesces mappings into efficient 1C batches.
- Apply `BridgeValue[]` to `BridgeState` and outbound consumers like DA.

### 6.8 Writes

- Reuse `WriteQueue` per-source consumer.
- `WriteAsync`:
  - Bit device (M/X/Y): bit write command
  - Word device (D): word write
  - Bit-in-word (`D100:8`): read word → modify bit → write word (document race with ladder writers; same class of risk as HMI bit-in-word notes in Pro-face manual)
- Type coercion: bool/int/short/string numeric → device representation; failure returns false / bad write without crash.

### 6.9 Subscriptions

- A3N v1 is **poll-only**.
- Do not cast to `OpcDaClient` for this type.
- When generalizing subscription hooks for UA, A3N simply does not implement the optional subscribe interface.

## 7. HTTP API

### 7.1 Extend existing source APIs

- `GET /api/da/sources` — include `sourceType` and type-specific fields (DA **or** A3N serial). Legacy `da` prefix kept in v1 (same as UA plan).
- `POST /api/da/sources` — polymorphic body; validate required fields per `sourceType`.
- Update-rate / remove endpoints work for both types.

Validation examples:

- `MelsecA3n`: `SerialPortName` required; baud/parity/stop in allowed sets; `Transport` must be `Serial` in v1 (reject `TcpTunnel` until implemented).
- Reject two sources claiming the same `SerialPortName` (400 with clear message).
- Enforce `MaxMappedTags` on mapping add for this source type.

### 7.2 Driver helper endpoints

| Endpoint | Role |
|---|---|
| `POST /api/drivers/melsec-a3n/test-connection` | Body: serial settings or `sourceId`. Short-lived open + optional probe; returns ok/error. Must not leave a leaked port open on failure. |
| `POST /api/drivers/melsec-a3n/parse-address` | Optional v1: validate/canonicalize one address; useful for UI |

No full memory browse API in v1.

### 7.3 Mappings

Existing `/api/mappings/*` remain. For A3N sources, `daItemId` is the device address. Validate syntax server-side on upsert.

## 8. Dashboard / Connectivity UI

### 8.1 Information architecture

```text
CONNECTIVITY
  Sources        status list (all source types; type badge)
  OPC DA         connectivity/opc-da          existing DA config
  Drivers        connectivity/drivers         NEW generic driver list + wizard
  Diagnostics    connectivity/diagnostics     unchanged
```

Optional detail route: `connectivity/drivers/melsec-a3n` when editing an A3N instance (or load type-specific form panel on the Drivers page). Prefer one Drivers page with type-specific form region over many empty nav leaves until more drivers exist.

### 8.2 Drivers list

- Lists sources where `SourceType` is a driver family (v1: `MelsecA3n`); may also show all non-DA sources.
- Columns/cards: type badge (`A3N`), display name, sourceId, serial port + baud summary, connection badge, last error.
- **+ Add Driver** wizard:
  1. Driver type (Mitsubishi A3N)
  2. Identity (SourceId, Name)
  3. Serial link (port, baud, parity, data, stop, station/PC, timeout/retry)
  4. Defaults (update rate, max tags)
  5. Review → Save (`POST /api/da/sources`)
- **Test connection** on form.
- Save / Reset / New / Remove toolbar pattern aligned with OPC DA page.
- Stable control id prefix: `drvA3n*` / `wzDrv*` (do not reuse `cfgProgId`).

### 8.3 Tag map

- Free-form address entry with validation feedback.
- No OPC tree browser for this type.
- Optional later: quick-add templates (D0–D99 block) — not v1 required.

### 8.4 Sources status

- Type badge: `DA` / `A3N` (and `UA` when present).
- Select → appropriate config surface (opc-da vs drivers).

## 9. Error handling

| Condition | Behavior |
|---|---|
| Missing serial port name | Disconnected + explicit error; no open attempt |
| Port open failure / access denied / already in use | Faulted + LastError; process up |
| Timeout / no response | Retry then Faulted or per-poll bad quality; reconnect/rebuild via existing session logic |
| Sum-check / NAK / malformed frame | Retry then error; log frame diagnostic at debug |
| Unsupported address on map | HTTP 400; do not persist |
| Partial batch (if protocol returns partial) | Good items update; failed items Bad quality. If frame is all-or-nothing, fail that batch tick for included items only |
| Write failure | `WriteAsync` false; WriteQueue completion path unchanged |

## 10. Testing strategy

| Layer | What |
|---|---|
| Protocol unit tests | Encode/decode 1C frames; sum check; bit/word read/write fixtures (no hardware) |
| Address unit tests | Parse, canonicalize, AnN range reject, bit-suffix forms |
| Transport mock | Fake `IMelsecTransport` scripted request/response for client Read/Write |
| Factory / settings | SourceType migration; Serial validation; port conflict |
| Integration | Optional with real A3N or serial loopback emulator — **not a v1 merge gate** |

Do not require full solution test suite mid-implementation; targeted tests at plan end.

## 11. Docs & help

- Help blurb: Drivers → Mitsubishi A3N, 1C Frame over RS-232, address examples, serial defaults (9600 8O1), write-through warning for bit-in-word RMW.
- Note Pro-face cable guidance is HMI-oriented; host USB-serial adapters are the expected Linux path.
- Link this spec from `context.md` when implementation lands.

## 12. Implementation sketch (not the plan)

Order of work (detailed plan comes after spec approval):

1. `SourceType` model + migration + DTO/API fields (align with UA-source model if cherry-picking).
2. `OpcBridge.Drivers.Melsec` codec + address + mock transport tests.
3. `MelsecA3nClient` + factory branch + BridgeWorker connect rules.
4. Test-connection API + mapping address validation.
5. Drivers UI list + wizard + form.
6. Docs / help; smoke with mock transport.

## 13. Open implementation details (non-blocking)

These are fixed during coding with fixtures, not product questions:

- Exact 1C ASCII layouts for the chosen bit/word read/write commands (lock via unit test golden strings).
- Default StationNo / PcNo byte/nibble encoding for CPU-direct.
- Precise consecutive-read max per command class.
- Serial package choice (`System.IO.Ports` vs alternative) under the project’s TFM/Linux constraints.

## 14. Approval record

Brainstorming locked:

- Goals & scope — approved
- Architecture Approach A — approved
- Protocol, addressing, config — approved
- UI / API / errors / testing — approved
- Transport: serial v1 only; TCP tunnel later
- Devices: D/M/X/Y read+write
- Address: typed with bit suffix
- UI: generic Drivers list
