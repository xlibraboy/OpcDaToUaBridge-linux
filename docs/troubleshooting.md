# Troubleshooting

- **DA browse fails** — check ProgID, host reachability, DCOM permissions, and credentials (Sources → OPC DA → Credentials section).
- **Values stop moving** — check Monitor → Source Status for connection state and last read timing. Check the alarm bar for rate-group saturation.
- **Tags not appearing in UA** — verify the tag is Enabled (Tags tab → faceplate → Setup tab). Check Monitor → OPC UA Endpoint for node count.
- **Rate group saturated** — the read time exceeds 80% of the update rate. Increase the rate or reduce the number of tags in that rate group.
- **Tag limit exceeded** — the number of tags in a rate group exceeds the configured limit. Move some tags to a slower rate or increase the limit in `appsettings.json`.
- **UA write rejected** — verify the tag's Access Rights is **Read-Write** or **Write** (Tags tab → faceplate → Setup tab). Read-only tags return `BadWriteNotSupported`. A write that times out (DA server unresponsive for 5s) returns `BadRequestTimeout`; a DA-side failure returns `BadNoCommunication`.
- **Deadband not filtering** — deadband only works under **subscriptions** (Sources → OPC DA → DA Subscriptions ON). If the DA server doesn't support `IOPCDataCallback`, the bridge falls back to polling and deadband has no effect. Check Logs for the subscription fallback warning.
- **Handle count growing** — a steady upward trend in Monitor → Resources → Handles indicates a COM object or handle leak. Restart the scheduled task if it grows unbounded; report the source for investigation.
- **Subscription fallback** — if `/api/logs` shows "OPC DA server does not support subscriptions", the bridge silently switched to polling. Values still flow at the update rate. This is non-fatal.
