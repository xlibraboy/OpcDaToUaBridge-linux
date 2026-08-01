# OPC UA Endpoint — Bind vs Connect

The **Endpoint URL** in Connection settings has two faces:

| Field | Value | Purpose |
|---|---|---|
| **Endpoint (config)** | `opc.tcp://0.0.0.0:4840/OpcBridge` | The server's **bind address**. `0.0.0.0` means "listen on all network interfaces" (localhost + LAN + VPN). This is the correct value for a server. |
| **Connect from client** | `opc.tcp://<hostname>:4840/OpcBridge` | The URL you enter in an **OPC UA client** to connect. The dashboard shows this with the host's real name filled in. |

**Do not** put `0.0.0.0` in your client's connect string — `0.0.0.0` means "this machine" to a client, which is the *client's* own machine, not the bridge. Always use the bridge host's IP address or hostname:

- Same machine: `opc.tcp://localhost:4840/OpcBridge`
- Another machine on the LAN: `opc.tcp://192.168.x.x:4840/OpcBridge` or `opc.tcp://HOSTNAME:4840/OpcBridge`

The **Monitor** tab shows both values: the configured bind address and the derived client connect URL.


# OPC UA Source vs OPC UA Server Endpoint

The bridge can sit on **both sides** of an OPC UA connection — do not confuse them:

- **OPC UA source (inbound)** — configured under **Sources → OPC UA**. The bridge acts as a UA **client** and connects **out** to an external UA server (PLC gateway, historian, another bridge). Its tags are pulled into the bridge like DA source tags.
- **OPC UA server endpoint (outbound)** — the bridge's own built-in UA server (`opc.tcp://0.0.0.0:4840/OpcBridge`). HMI/SCADA clients connect **to** the bridge here to read the mirrored tags. This endpoint exists regardless of whether any UA sources are configured.

### Mapping UA source tags

- The **item id** for a UA source mapping is the external server's **NodeId string** (e.g. `ns=2;s=Channel1.Device1.Tag1` or `i=2258`).
- Browse the external address space from the source and map Variable nodes to bridge tags — the same browse-and-map flow used for DA items.

### Security

Supported security modes: **None**, **Sign**, **SignAndEncrypt** — with security policy **None** or **Basic256Sha256** (Sign/SignAndEncrypt require Basic256Sha256). Credentials are an optional UserName token; leave blank for anonymous access.

### Scale

Only **mapped** tags are subscribed on the external server — the unmapped address space is never polled. Large mapped sets are supported (per-source mapped-tag cap, default 50000). Live values arrive via UA **subscriptions**; polling is only a fallback for the mapped set. Writes from UA clients, HMI, or MQTT write **through** to the external server for mappings marked writable.
