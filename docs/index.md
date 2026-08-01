# OPC DA → UA Bridge

The **OPC DA → UA Bridge** connects legacy **OPC DA** servers (COM/DCOM) to modern
**OPC UA** clients, PLC serial drivers, MQTT brokers, and InfluxDB historians —
all through a single web dashboard.

```
OPC DA Server ──► OpcDaClient ──► BridgeWorker ──► OPC UA Server (opc.tcp://…)
      ▲                                 │
      └──── write queue ◄───────────────┘
```

## What it does

- **Maps OPC DA tags to OPC UA nodes** — one unified address space (`ns=2`) for all
  sources, browsable by any OPC UA client (HMI/SCADA).
- **Polling or subscriptions** per rate group, with per-tag update rates, deadband,
  access rights (Read / Read-Write / Write), and simulation mode.
- **DA Links** — forward a provider tag's value to a consumer tag, even across
  different DA servers.
- **PLC drivers** — Mitsubishi A3N (MELSEC A-compatible 1C Frame over RS-232) and
  Siemens S7-200 PPI.
- **MQTT** — publish tag values to an external broker and subscribe inbound writes.
- **InfluxDB** — historical logging of tag values (v1.1).
- **Web dashboard** — monitor, configure, and troubleshoot from a browser.

## Where to start

- New to the bridge? Read the [Getting Started](getting-started.md) guide and the
  [Topology & Data Flow](topology.md) overview.
- Installing on Windows? See [Installation on Windows](installation.md).
- Problems? Check [Troubleshooting](troubleshooting.md).
- Every configuration key and tag field: [Configuration Reference](configuration.md).
