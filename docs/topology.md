# Topology & Data Flow

```
  ┌─────────────────────────────────────────────────────────────────────┐
  │                        OPC DA Server (COM/DCOM)                      │
  │                   Matrikon / Kepware / RSLinx / etc.                │
  └──────────────┬──────────────────────────────┬───────────────────────┘
                 │                              │
          IOPCSyncIO.Read               IOPCItemMgt.AddItems
                 │                              │
  ┌──────────────▼──────────────────────────────▼───────────────────────┐
  │                      OpcDaClient (per source)                        │
  │                                                                      │
  │   ┌─── Rate Group 500ms ──┐  ┌─── Rate Group 1000ms ──┐  ┌── 5000ms │
  │   │ IOPCItemMgt + SyncIO  │  │ IOPCItemMgt + SyncIO   │  │  ...     │
  │   │ Tags: A, B, C         │  │ Tags: D, E             │  │  Tag: F  │
  │   └───────────┬───────────┘  └───────────┬────────────┘  └────┬─────┘
  │               │                          │                    │       │
  │         ReadAsync() ──────────────── merges all groups ───────┘       │
  └───────────────┬──────────────────────────────────────────────────────┘
                  │
          BridgeValue[] per poll cycle
                  │
  ┌───────────────▼──────────────────────────────────────────────────────┐
  │                      BridgeWorker (.NET 8)                            │
  │                                                                      │
  │   ┌─── Poller 500ms ────┐  ┌─── Poller 1000ms ───┐  ┌── Poller 5000ms│
  │   │ reads tags A,B,C    │  │ reads tags D,E      │  │  reads tag F   │
  │   │ Task.Delay(500)     │  │ Task.Delay(1000)    │  │  Task.Delay(5s)│
  │   └─────────┬───────────┘  └─────────┬──────────┘  └───────┬────────┘
  │             │                        │                     │         │
  │             └────────────┬───────────┴─────────────────────┘         │
  │                          │                                           │
  │          bridge_state_.SetValue() + ua_server_.UpdateValue()         │
  │          bridge_state_.UpdateRateGroup() ──► alarm bar                │
  └──────────────┬───────────────────────────────────────────────────────┘
                 │
          UpdateValue → ClearChangeMasks
                 │
  ┌──────────────▼───────────────────────────────────────────────────────┐
  │                   OPC UA Server (Opc.Ua.Server SDK)                   │
  │                                                                       │
  │   Folder: OpcDaTags (ns=2)                                            │
  │   ├── ns=2;s=sourceA/TagA   ← live value, quality, timestamp         │
  │   ├── ns=2;s=sourceA/TagB   ← live value                             │
  │   ├── ns=2;s=sourceA/TagD   ← live value                             │
  │   └── ns=2;s=sourceB/TagF   ← manual override value                  │
  └──────────────┬───────────────────────────────────────────────────────┘
                 │
          OPC UA TCP (opc.tcp://...)
                 │
  ┌──────────────▼─────────────────┐  ┌──────────────────────────────────┐
  │     UA Client 1 (HMI/SCADA)    │  │     UA Client 2 (Logger)          │
  │  Subscribes to monitored items │  │  Reads values on demand           │
  └────────────────────────────────┘  └──────────────────────────────────┘


  ┌─────────────────────────────────────────────────────────────────────┐
  │            Web Dashboard (HTTP port, default 8080)                     │
  │                                                                      │
  │  Sidebar groups pages by job:                                        │
  │  Connectivity ──► OPC DA, Drivers, Diagnostics                       │
  │  Tags ──► Maps, DA Links                                             │
  │  IoT ──► MQTT, Traffic                                               │
  │  Historian ──► InfluxDB                                              │
  │  Ops ──► Monitor, Logs, Diagram                                      │
  │  Docs ──► Documentation (embedded in dashboard), About                                               │
  │                                                                      │
  │  HTTP API: /api/dashboard, /api/mappings, /api/da/sources, etc.      │
  │                                                                      │
  │  PLC driver sources (SourceType=MelsecA3n) are edited on the         │
  │  Connectivity → Drivers page, not the OPC DA page.                   │
  │                                                                      │
  │  **Apps Pill**: Shows count of detected bridge instances across all    │
  │  configured DA source hosts. Updates every 10 seconds.                 │
  └─────────────────────────────────────────────────────────────────────┘
```

**Key data flow:**

- Each **source** has one `OpcDaClient` (one COM connection) pinned to a **dedicated STA thread**, with multiple **rate groups** (one OPC DA group per distinct rate).
- Values arrive either via **subscription callbacks** (`IOPCDataCallback`, default) or **poller tasks** (`IOPCSyncIO.Read`, fallback) — one path per rate group.
- Subscription values flow: DA Server → `IOPCDataCallback` → OpcDaClient → BridgeWorker → BridgeState + UaServer. Poll values flow: DA Server → `IOPCSyncIO.Read` → poller task → BridgeState + UaServer.
- **UA writes** (writeable mappings) flow: UA Client → BridgeNodeManager → WriteQueue → per-source consumer → `IOPCSyncIO.Write` → DA Server.
- UA clients subscribe to UA nodes and receive notifications when values change.
- The web dashboard reads from `/api/dashboard` (1s polling) to display live status and resource telemetry.

## Dashboard Navigation

The sidebar groups pages by job:

- **Sources** — Sources (status, + Add Source), OPC DA (connection config, rate, subscriptions, discover, backup), **OPC UA (client sources)** (external UA servers the bridge connects to), Drivers (PLC serial drivers: Mitsubishi A3N, Siemens S7-200 PPI), Diagnostics (DA health, time sync)
- **Tags** — Maps (OPC DA / OPC UA / Drivers sub-tabs: browse, map to UA, faceplate), DA Links (DA→DA forwarding)
- **IoT** — MQTT (broker config), Traffic (publish/subscribe monitor)
- **Historian** — InfluxDB (config, write status, per-tag enable via faceplate)
- **Ops** — Monitor (live values, status), Logs, Diagram
- **Docs** — Documentation (embedded in the dashboard), About

Use **Sources → OPC DA → + Add Source** for the guided setup wizard.
Use **Sources → OPC DA** to edit ProgID/host, credentials, default rate, subscriptions, discover servers, and backup/restore.
Use **Sources → OPC UA** to add and configure OPC UA client sources — the bridge connects **out** to external UA servers.
Use **Connectivity → Drivers** for PLC serial drivers (Mitsubishi A3N, Siemens S7-200).
Use **IoT → MQTT → Setup Wizard** and **Historian → InfluxDB → Setup Wizard** for first-time broker/historian setup.
