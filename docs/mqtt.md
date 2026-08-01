# MQTT (OPC UA ↔ External Broker)

## Important: You Need an External MQTT Broker

**This app does NOT include its own MQTT broker.** It connects TO an external MQTT broker (like Mosquitto, HiveMQ, EMQX, AWS IoT, etc.). You must have a broker running before you can use MQTT features.

**Quick start with Mosquitto (Docker):**
```bash
docker run -d -p 1883:1883 --name mosquitto eclipse-mosquitto
```

Then configure the bridge to connect to `tcp://localhost:1883`.

## What MQTT Does

The bridge can:
- **Publish** OPC UA tag values to your MQTT broker (when tags have MQTT enabled)
- **Subscribe** to topics and write inbound messages back through the OPC UA path

MQTT is scoped to the **OPC UA layer** — it reads the mirrored UA tag values and writes through the same UA write path a UA client uses.

## Setup Steps

1. **Start an MQTT broker** (see examples above)
2. **Configure the broker connection** in the MQTT Broker section:
   - Turn ON the **Enabled** checkbox
   - Enter your broker URL (e.g., `tcp://localhost:1883`)
   - Add credentials if your broker requires them
   - Click **Save Config** then **Connect**
3. **Enable MQTT for specific tags**:
   - Go to Tags tab → click a tag → check the **MQTT** checkbox
   - Optionally set a custom topic in the **MQTT Topic** field

## Topics

- **Publish**: `{TopicPrefix}/{SourceId}/{ItemId}` (default prefix `bridge/tags`), or a per-tag `MqttTopic` override set in the tag faceplate.
- **Subscribe**: the bridge subscribes to `{TopicPrefix}/#` and resolves inbound topics to tags the same way.

## Payload

Minimal JSON. Selectable fields (MQTT Broker → Payload Fields): `v` (value), `t` (timestamp), `q` (quality), `sourceId`, `itemId`, `displayName`, `dataType`. Default is `v` + `t`.

```json
{ "v": 12.3, "t": "2026-07-08T12:00:00.0000000Z" }
```

## Notes

- **Connection resilience**: Publish and subscribe are failure-resilient — a broker outage does not stop the bridge; the client auto-reconnects.
- **Write protection**: Inbound writes to a tag flow through the UA write path (same as a UA client write) and are rejected if the tag is read-only.
- **Popular MQTT brokers**: Mosquitto (free, open source), HiveMQ (enterprise), EMQX (scalable), AWS IoT Core, Azure IoT Hub, Google Cloud IoT Core.

## Subscriptions & Deadband

- Subscriptions can be toggled in **Sources → OPC DA → DA Subscriptions**. When ON (default), the bridge uses `IOPCDataCallback` to receive value changes from the DA server instead of polling with `IOPCSyncIO.Read`. Changing the toggle takes effect on the next source reconnect.
- Subscriptions deliver values on change (faster than update rate) and respect the per-group **deadband**.
- **Deadband %** (Tags tab → faceplate → Setup tab → Deadband %) sets the OPC DA group's `percentDeadband`. The DA server suppresses callbacks for changes within the deadband. Set 0 for no filtering, 1.0 for 1% noise suppression.
- If the DA server does not support `IOPCDataCallback`, the bridge logs a warning and falls back to device-read polling — deadband has no effect in polling mode.
- All COM work for a source (reads, writes, subscription callbacks) is pinned to a dedicated **STA thread** per source to avoid COM apartment marshalling failures.

## Resource Telemetry

- The Monitor tab shows a **Resources** panel with native process counters (Windows only):
  - **Handles** — total OS handles held by the process (`GetProcessHandleCount`)
  - **GDI / USER** — GDI and USER object counts (`GetGuiResources`)
- These are sampled every 5 seconds. A steady or slowly-growing handle count confirms no COM/resource leak. On non-Windows, the panel shows "n/a (non-Windows)".
- Watch for handle count growth over time — a steady upward trend indicates a handle or COM object leak.
