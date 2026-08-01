# Configuration Reference

## appsettings.json

- **Da:ProgId** — OPC DA server ProgID (e.g. `Matrikon.OPC.Simulation.1`)
- **Da:Host** — DA server host (localhost or remote IP)
- **Da:UpdateRateMs** — default update rate for new sources (min 100ms); can be changed live in Sources → OPC DA → Default Update Rate
- **Da:UseSubscriptions** — use `IOPCDataCallback` subscriptions (default `true`); can be toggled live in Sources → OPC DA → DA Subscriptions
- **Ua:EndpointUrl** — OPC UA server endpoint (default `opc.tcp://0.0.0.0:4840/OpcDaToUaBridge`)
- **Ua:AutoAcceptUntrustedCertificates** — accept untrusted UA client certs (dev/test)
- **Bridge:RateLimits** — max tags per rate group (rate ms → max tags)
- **Bridge:ExpectedTagCount** — pre-allocation hint for the value cache (default 1000; grows past it)
- **Bridge:Mappings** — initial tag mappings loaded at startup

## Tag mapping fields

| Field | Default | Description |
|-------|---------|-------------|
| `sourceId` | `default` | DA source identifier |
| `itemId` | *(required)* | OPC DA item ID |
| `uaNodeId` | auto | UA node ID (default `ns=2;s={sourceId}/{itemId}`) |
| `displayName` | = itemId | Label shown in UA and dashboard |
| `description` | `null` | Operator-entered notes/description (shown as tooltip in tag list) |
| `dataType` | `Double` | Data type hint for UA node |
| `enabled` | `true` | Include in DA reads and UA publishing |
| `accessRights` | `Read` | `Read` (DA→UA), `Read-Write` (DA↔UA), or `Write` (UA→DA only) |
| `mode` | `Source` | `Source` (live DA value) or `Manual` (simulation — publishes ManualValue) |
| `manualValue` | `null` | Fixed value when mode is `Manual` (simulation) |
| `pollRateMs` | `0` | Per-tag update rate in ms (0 = source default) |
| `deadbandPct` | `0` | Deadband % for subscription filtering (0–100; 0 = no filter) |
