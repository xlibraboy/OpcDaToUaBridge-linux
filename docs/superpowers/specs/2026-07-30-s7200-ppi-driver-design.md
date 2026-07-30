# Siemens S7-200 PPI Driver Design

- **Date:** 2026-07-30
- **Branch:** `feature/add-s7200-driver` (worktree `.worktrees/feature-add-s7200-driver`)
- **Status:** Approved design (§1–§9), pending implementation plan
- **Primary surfaces:** source registry / `SourceClientFactory` / `BridgeWorker`, new S7-200 client, Connectivity **Drivers** UI
- **Related:** Melsec A3N serial driver (`docs/superpowers/specs/2026-07-27-melsec-a3n-driver-design.md`); typed nested source options (`docs/superpowers/specs/2026-07-29-typed-source-config-design.md`); `ISourceClient` rename (`docs/superpowers/specs/2026-07-29-source-client-rename-design.md`); architecture in `context.md`
- **Protocol refs:** [libnodave](https://github.com/netdata-be/libnodave) (`daveProtoPPI`, `testPPI.c`, `_daveExchangePPI` / `_daveConnectPLCPPI`) used as **reference only** — pure managed reimplementation, no P/Invoke, no LGPL link

## 1. Goal

Add **Siemens S7-200** as an external inbound **source**, product-labeled a **Driver**. The bridge opens a **serial PPI client** over a host USB/RS-485 PPI cable (e.g. `/dev/ttyUSB0`), maps selected memory addresses, polls them into the existing bridge pipeline, and re-publishes on the outbound path (this process’s OPC UA server, MQTT, Influx, HMI). Writes on writeable mapped tags go **back** to the PLC (write-through).

This is **not** OPC DA/UA, **not** ISO-on-TCP / S7Comm for S7-300/400, and **not** a native libnodave wrapper. Wire protocol for v1 is **PPI** (Point-to-Point Interface) on host serial, implemented in pure C#.

```text
S7-200 PPI port  --(PPI serial)-->  OpcDaToUaBridge  --(our UA server)--> SCADA / HMI
OPC DA / UA / A3N --------------->         ↑
```

**Success means:**

1. Create and configure one or more S7-200 PPI drivers under Connectivity → **Drivers**.
2. Map core areas (**I, Q, M, V**) with Siemens-style addresses; live values via **poll**.
3. **Write-through** for mappings with `Writeable=true`.
4. Existing OPC DA / OPC UA / Melsec A3N sources and outbound endpoints remain unchanged.
5. Protocol codec is unit-testable without hardware; client is Linux-capable via `/dev/tty*`.

## 2. Decisions locked in brainstorming

| Decision | Choice |
|---|---|
| Product naming | UI: **Driver** / Drivers list; code discriminator: `SourceType = S7200Ppi` |
| PLC target | Siemens **S7-200** family (CPU 22x class) |
| Protocol | **PPI** over serial (libnodave `daveProtoPPI` as reference) |
| Implementation | **Pure C#** — no P/Invoke of libnodave |
| Transport v1 | **Host serial only** (e.g. `/dev/ttyUSB0`); TCP serial-device-server **later** behind a transport seam |
| Architecture | **A** — mirror Melsec: new `OpcBridge.Drivers.S7` + `ISourceClient` factory branch; reuse `BridgeWorker` sessions, mappings, write queue, UA mirror |
| I/O scope v1 | **Read + write**, areas **I, Q, M, V only** |
| Address form | Siemens Micro/WIN style: `I0.0`, `Q0.1`, `M10.2`, `VB10`, `VW100`, `VD200`, `MB0`, `MW10`, `MD20` |
| Serial defaults | **9600 8E1**; **LocalPpiAddress = 0**; **RemotePpiAddress = 2** (libnodave `testPPI` defaults) |
| Connectivity IA | Existing **Drivers** list + wizard type step (add S7-200 next to A3N) |
| Value updates | **Poll only** (no PLC change-subscription in v1) |
| Item identity | Keep mapping key `(SourceId, ItemId)`; `ItemId` = Siemens address string |

## 3. Non-goals (v1)

- P/Invoke / shipping `libnodave.so` (LGPL link surface)
- MPI, ISO-on-TCP, S7online, USB-MPI adapters as first-class transports
- S7-300 / S7-400 / S7-1200 / S7-1500
- Areas beyond I/Q/M/V (T, C, AI, AQ, SM, S, L, …) unless free on the same codec path — **not required**
- TCP tunnel / serial device server (config enum reserved; no implementation)
- Multi-drop PPI network discovery / partner scan as a product feature (addresses remain configurable)
- OPC-style tree browse of PLC memory
- Shared `OpcBridge.Drivers.Serial` extraction / refactor of Melsec transport
- Placing S7 code inside `OpcBridge.Da` (COM) or `OpcBridge.Drivers.Melsec`
- Parallel Drivers value bus / second BridgeWorker

## 4. Scale & performance contract

PPI serial is orders of magnitude slower than OPC Ethernet. Design for **correct batching**, not 20k-tag scale.

| Rule | Contract |
|---|---|
| What is hot | Only tags present in `MappingStore` for that `SourceId` |
| Ingest | **Poll** rate groups only (existing BridgeWorker pollers) |
| Batching | Group consecutive same-area byte ranges into one PPI read; soft cap e.g. **~64–222 bytes** per request (lock exact PDU limit with fixtures from libnodave PDU size) |
| Writes | Per-tag via `WriteQueue`; bit writes use bit PDU or RMW where needed |
| Caps | Optional `MaxMappedTags` per driver; default modest (e.g. **2000**) with hard fail on exceed |
| Dashboard | Drivers list O(sources); tag map is address entry, not full memory browser |

## 5. Configuration model

### 5.1 Source discriminator

```csharp
// SourceTypes.cs
public const string S7200Ppi = "S7200Ppi";
```

Allowlist updates (same places as Melsec/UA): migration normalize, API resolve, factory, BridgeWorker connect rules, BridgeState summary badges, Dashboard type checks, export/import.

Unknown types on disk still collapse safely; API rejects unknown on write.

### 5.2 Nested options (typed source config)

```csharp
public sealed record S7200PpiSourceOptions(
    string Transport,          // "Serial" only in v1
    string SerialPortName,
    int BaudRate,              // default 9600
    int DataBits,              // default 8
    string Parity,             // default "Even"
    string StopBits,           // default "One"
    int LocalPpiAddress,       // default 0 (PC/adapter)
    int RemotePpiAddress,      // default 2 (PLC)
    int TimeoutMs,             // default 3000
    int RetryCount);           // default 2
```

Extend `DaSourceRuntimeSettings` with `S7200PpiSourceOptions? S7200` (name may be `S7200Ppi` property — pick one and keep DTO/JSON aligned).

Compat getters for serial fields **must not** steal Melsec’s `StationNo`/`PcNo` for PPI addresses. Prefer:

- Shared: `Transport`, `SerialPortName`, `BaudRate`, `DataBits`, `Parity`, `StopBits`, `TimeoutMs`, `RetryCount` when unambiguous
- PPI-specific: `LocalPpiAddress`, `RemotePpiAddress` (new fields; Melsec keeps `StationNo`/`PcNo`)

Do **not** overload Melsec station/PC strings as PPI integers.

### 5.3 Persistence

- Continue **`sources.json`** via existing runtime settings service (`FromDto` / `ToDto` / disk load).
- Nested JSON for new sources; flat legacy not required for S7 (new type).
- Export/import include `SourceType` + non-secret serial/PPI fields.
- No password fields for serial v1.

### 5.4 Mapping identity & address syntax

- Keep `TagMapping` key `(SourceId, ItemId)` case-insensitive.
- For S7-200, **`ItemId` = Siemens address string**, canonicalized on map save.
- Accepted forms (v1):

  | Form | Meaning | Area (libnodave) |
  |---|---|---|
  | `I<b>.<bit>` | Digital input bit | `daveInputs` (0x81) |
  | `Q<b>.<bit>` | Digital output bit | `daveOutputs` (0x82) |
  | `M<b>.<bit>` | Flag/Merker bit | `daveFlags` (0x83) |
  | `IB<n>` / `IW<n>` / `ID<n>` | Input byte/word/dword | Inputs |
  | `QB<n>` / `QW<n>` / `QD<n>` | Output byte/word/dword | Outputs |
  | `MB<n>` / `MW<n>` / `MD<n>` | Merker byte/word/dword | Flags |
  | `VB<n>` / `VW<n>` / `VD<n>` | V memory byte/word/dword | `daveDB` / DB **1** (S7-200 V area) |

- Bit index **0–7** for bit forms. Offsets decimal.
- Canonical examples: `I0.0`, `Q1.3`, `M10.2`, `VB0`, `VW100`, `VD200`, `MB0`, `MW10`.
- Reject unknown prefixes, bad bit index, empty address on map upsert (**hard reject**).
- Soft range caps (document; tighten with hardware): V 0–10239 (CPU-dependent), I/Q typically 0–15 bytes class-dependent, M 0–31 bytes class-dependent — **do not hard-fail exotic CPU sizes in codec**; optional warn / config later.
- Default outbound NodeId remains `ns=2;s={SourceId}/{ItemId}` with existing sanitization; **stored** `ItemId` stays the PLC address for Read/Write.
- `DataType`, `Writeable`, `AccessRights`, `PollRateMs`, `MqttEnabled`, `InfluxEnabled`, `Mode`/`ManualValue` keep current meanings.

## 6. Runtime architecture

### 6.1 Approach A (locked)

1. **Registry** — versioned source snapshot includes `SourceType` + nested `S7200` options.
2. **Factory** — `SourceClientFactory` returns:
   - `OpcDaClient` for `OpcDa`
   - `OpcUaSourceClient` for `OpcUa`
   - `MelsecA3nClient` for `MelsecA3n`
   - **`S7200Client` for `S7200Ppi`** (new)
3. **`BridgeWorker`** — existing reconfigure / sessions / pollers / `WriteQueue`.
4. **Normalized unit** — `BridgeValue(SourceId, ItemId, Value, TimestampUtc, Quality, IsGood)`.
5. **Outbound** — unchanged.

### 6.2 Client seam

Implement `S7200Client : ISourceClient`:

- `ConnectAsync` — open serial exclusively; PPI link/connect sequence (libnodave `daveConnectPLC` / `_daveConnectPLCPPI` equivalent); set Connected/Faulted
- `ReadAsync(mappings)` — parse addresses, batch consecutive same-area byte spans, issue PPI reads, map to `BridgeValue[]`
- `WriteAsync(itemId, value)` — byte/word/dword/bit write as required
- `TryGetTagMetadata` — from address parse (Boolean vs Int16/UInt16/Int32/Float-sized, access rights; Q writable, I typically read-only metadata)
- `IAsyncDisposable` — close port

No COM/STA. **Single-flight** `SemaphoreSlim` on the port (half-duplex request/response).

Test injection: `S7200Client(options, IS7Transport)` + scripted transport in unit tests.

### 6.3 Project placement

- New assembly: **`OpcBridge.Drivers.S7`**
- Layers:

  ```text
  OpcBridge.Drivers.S7/
    Addressing/     S7Address, S7Area (parse/canonicalize/validate)
    Protocol/       PpiFrameCodec, PpiAreas, PpiException (pure, no I/O)
    Transport/      IS7Transport, SerialS7Transport
    S7200Client.cs
    S7200ClientOptions.cs
  ```

- **Do not** put this in `OpcBridge.Da` or inside `OpcBridge.Drivers.Melsec`.
- App references the drivers project; factory constructs the client.
- Solution entry in `OpcDaToUaBridge.sln`.

### 6.4 Protocol (PPI)

- Binary PPI framing as used by S7-200 programming ports (STX/DLE/ETX style envelopes around S7 PDUs — **exact byte layouts locked with unit fixtures** derived from libnodave `nodave.c` PPI paths and/or captured traces; do not invent alternate framing).
- Public-facing ops needed in v1:
  - Connect / link setup to `RemotePpiAddress`
  - Read bytes from area (I/Q/M/V→DB1)
  - Write bytes / bits to area
- On NAK / BCC fail / timeout: retry up to `RetryCount`, then surface error; do not crash process.
- Quality: successful parse → Good; timeout/protocol error for an item → Bad.
- Document locked request/response hex fixtures in tests.

**V memory:** S7-200 V area is accessed as **DB 1** (libnodave `daveDB` + DB number 1). Codec must encode V offsets that way; address parser still accepts `VBn`/`VWn`/`VDn` product syntax.

### 6.5 Transport seam

```text
IS7Transport
  OpenAsync / CloseAsync
  TransactAsync(ReadOnlyMemory<byte> request, timeout) → response bytes
  // or slightly richer if PPI needs multi-step exchange helper methods
  // on the client while transport stays byte-oriented — prefer thin transport
```

v1: `SerialS7Transport` via `System.IO.Ports.SerialPort`.  
Later: TCP serial device server — **same codec**.

Parity default **Even** (9600 8E1). Allow Odd/None/Mark/Space in validation sets consistent with Melsec where practical.

### 6.6 Connect lifecycle

On add/update source or settings version bump:

1. Dispose previous session for that `SourceId` if any.
2. Create client from factory.
3. `ConnectAsync`: configure serial, open port, PPI connect/probe.
4. Mark `BridgeState` Connected / Faulted with clear errors.
5. Pollers pick up enabled non-Manual mappings.

Empty / missing `SerialPortName` → Disconnected with explicit error.  
Port busy / access denied → Faulted + LastError; process stays up.

### 6.7 Poll path

- Existing per-`(SourceId, PollRateMs)` pollers call `ReadAsync`.
- Client coalesces mappings into efficient PPI batches (same area, consecutive byte ranges).
- Apply `BridgeValue[]` to `BridgeState` and outbound consumers.

### 6.8 Writes

- Reuse `WriteQueue` per-source consumer.
- `WriteAsync`:
  - Bit (`I` typically rejected or still attempted per metadata; `Q`/`M` bit forms): bit write
  - Byte/word/dword: sized write
  - Type coercion: bool/int/short/float/string numeric → device representation; failure returns false without crash
- I-area writes: allow at API if user marks Writeable, but `TryGetTagMetadata` should default I to read-oriented access rights (mirror physical reality); do not special-case crash.

### 6.9 Subscriptions

- S7-200 v1 is **poll-only**.
- Do not implement `ISubscribableSourceClient`.

## 7. HTTP API

### 7.1 Extend existing source APIs

- `GET/POST /api/da/sources` (legacy `da` prefix kept) — polymorphic body; include nested S7 options when `sourceType=S7200Ppi`.
- Update-rate / remove endpoints work for all types.

Validation:

- `S7200Ppi`: `SerialPortName` required; baud/parity/stop in allowed sets; `Transport` must be `Serial` in v1; `LocalPpiAddress` / `RemotePpiAddress` in **0–126** (PPI address space).
- Reject two sources claiming the same `SerialPortName` (400).
- Enforce `MaxMappedTags` on mapping add for this source type.
- Address validation on mapping upsert for `S7200Ppi` sources.

### 7.2 Driver helper endpoints

| Endpoint | Role |
|---|---|
| `POST /api/drivers/s7200-ppi/test-connection` | Body: serial+PPI settings or `sourceId`. Short-lived open + connect/probe; returns ok/error. Must not leak port on failure. |
| `POST /api/drivers/s7200-ppi/parse-address` | Optional v1: validate/canonicalize one address for UI |

No full memory browse API in v1.

### 7.3 Mappings

Existing `/api/mappings/*` remain. For S7 sources, `itemId` is the Siemens address.

## 8. Dashboard / Connectivity UI

### 8.1 Information architecture

Reuse existing **Drivers** page (`connectivity/drivers`). Add S7-200 as a second driver type — no new nav leaf.

### 8.2 Drivers list / wizard

- List includes `MelsecA3n` **and** `S7200Ppi` (type badges `A3N` / `S7-200`).
- **+ Add Driver** wizard step 1 options:
  - Mitsubishi Melsec A3N (serial 1C)
  - Siemens S7-200 (PPI serial)
- Type-specific form region:
  - Shared serial: port, baud, data bits, parity, stop, timeout, retry, rate, max tags
  - A3N: StationNo, PcNo
  - S7: Local PPI address, Remote PPI address
- **Test connection** routes to the matching `/api/drivers/.../test-connection`.
- Control id prefix: `drvS7*` for S7 fields (do not reuse `drvA3n*` or `cfgProgId`).
- Wizard ids: extend `wzDrvType` options; S7 panes reuse serial fields + `wzDrvLocalPpi` / `wzDrvRemotePpi`.

### 8.3 Tag map

- Free-form address entry with validation feedback (`VW100`, `I0.0`, …).
- No OPC tree browser for this type.

### 8.4 Sources status

- Type badge: `DA` / `UA` / `A3N` / `S7-200`.
- Select → appropriate config surface.

### 8.5 Help / export

- HelpContent: short S7-200 PPI section (cable, 9600 8E1, addresses, PPI addresses).
- Export/import round-trip includes S7 sources.

## 9. Error handling

| Condition | Behavior |
|---|---|
| Missing serial port name | Disconnected + explicit error; no open attempt |
| Port open failure / access denied / in use | Faulted + LastError; process up |
| PPI connect / handshake failure | Faulted + LastError |
| Timeout / BCC / NAK after retries | Item Bad quality or write false; session may stay Connected unless repeated fatal |
| Invalid address on map save | 400 / validation error; mapping not stored |
| Write failure | `WriteAsync` false; WriteQueue completion path unchanged |
| Unsupported Transport (`TcpTunnel`) | 400 on save |

## 10. Testing strategy

| Layer | What |
|---|---|
| Address parser | Valid/invalid Siemens forms; canonicalize; area/offset/size/bit |
| PPI codec | Fixture hex from libnodave-derived frames: connect, read V/M/I/Q, write bit/byte |
| Transport | Scripted / fake `IS7Transport`; optional loopback if available |
| Client | `S7200Client` with scripted transport: batching, RMW/bit, retries, dispose |
| Factory | `SourceClientFactory` returns `S7200Client` for `S7200Ppi` |
| Settings | Nested options DTO round-trip; defaults 9600 E 8/1, local 0, remote 2 |
| API | Create source, test-connection validation, mapping address reject |
| UI | Manual / light: wizard type option visible; no full browser e2e required in v1 unit suite |

Prefer xUnit in `tests/OpcBridge.LoadTest` matching Melsec `Melsec*Tests.cs` style.

## 11. File checklist (implementation)

**New**

- `src/OpcBridge.Drivers.S7/` (csproj + Addressing/Protocol/Transport + Client/Options)
- `tests/OpcBridge.LoadTest/S7*.cs` (address, codec, transport, client, factory, settings, api)
- This spec; later `docs/superpowers/plans/2026-07-30-s7200-ppi-driver.md`

**Touch**

- `OpcDaToUaBridge.sln`
- `src/OpcBridge.Core/SourceTypes.cs`
- `src/OpcBridge.App/DaRuntimeSettings.cs` (nested options + DTO)
- `src/OpcBridge.App/SourceClientFactory.cs`
- `src/OpcBridge.App/Program.cs` (API allowlist, test-connection, validation)
- `src/OpcBridge.App/BridgeWorker.cs` / `BridgeState.cs` (type badges / connect rules as needed)
- `src/OpcBridge.App/DashboardPage.cs` (Drivers wizard + form)
- `src/OpcBridge.App/HelpContent.cs`
- Export/import paths if separate from settings DTO
- App csproj project reference

## 12. Open implementation details (not product decisions)

These are fixed during coding with fixtures, not brainstorming:

1. Exact PPI envelope bytes and multi-step exchange (length prefix, DLE stuffing, BCC) — derive from libnodave `nodave.c` PPI functions.
2. Exact max payload per read for batching.
3. Whether `IS7Transport.TransactAsync` is one-shot or whether client drives multi-frame exchange via multiple transport read/write calls (prefer whatever keeps codec pure and tests simple).
4. Float (`VD` as IEEE) vs raw Int32 presentation in `BridgeValue` — default: sized integer for B/W/D unless mapping DataType says Float.

## 13. Approval

| Section | Status |
|---|---|
| §1 Goal | Approved |
| §2 Locked decisions | Approved |
| §3 Non-goals | Approved |
| §4 Scale | Approved |
| §5 Config / addresses | Approved |
| §6 Runtime / protocol | Approved |
| §7 API | Approved |
| §8 UI | Approved |
| §9 Errors | Approved |
| §10–§12 Testing / files / open details | Spec complete; implementation may refine fixtures |

**Next:** user review of this written spec → implementation plan (`writing-plans`) → execute on `feature/add-s7200-driver`.
