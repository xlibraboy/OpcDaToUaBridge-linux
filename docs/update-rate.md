# Update Rate & Tag Limits

- Each tag can be assigned its own update rate via the faceplate (Tags tab → click a tag → Setup tab). Tags with the same rate share one OPC DA group.
- Tags set to "Source Default" (update rate = 0) inherit the global **Default Update Rate** (Sources → OPC DA).
- The global Default Update Rate is the single fallback for all tags without an explicit rate.
- Watch the alarm bar on the Monitor tab: green = within limits, yellow = cycle budget warning, red = limit exceeded or saturated.

*(appsettings.json → `Bridge:RateLimits`)*
| Rate | Max Tags | Basis |
|------|----------|-------|
| 100 ms | 200 | COM device read ~0.4ms/item; 80ms budget at 80% cycle |
| 250 ms | 500 | Cached reads ~0.4ms; 200ms budget |
| 500 ms | 1,000 | Mixed cache/device ~0.4ms; 400ms budget |
| 1 s | 5,000 | Cached reads; 800ms budget; UA lock ~50ms |
| 2 s | 10,000 | ~1.6s budget; UA updates ~10K/sec |
| 5 s | 20,000 | ~4s budget; network ~2MB/s per UA client |
| 10 s | 50,000 | ~8s budget; network ~5MB/s; lock contention monitor |

## How limits are derived

- **DA COM read time** — `IOPCSyncIO.Read` with N items takes ~0.4–1ms/item (cache) or 2–5ms/item (device). Limit = 80% of rate interval ÷ per-item read time.
- **UA server lock** — each `UpdateValue` holds a lock for ~5–10μs. At 5000 tags × 500ms = 10K updates/sec, lock contention becomes measurable.
- **Network bandwidth** — each UA client notification is ~50–100 bytes. At 5000 tags × 500ms ≈ 500KB/s per client; 50K tags × 100ms ≈ 5MB/s saturates 100Mbps LAN.
- Limits are **conservative estimates**, not hard ceilings. Adjust in `appsettings.json` for your hardware and network. The alarm bar warns before degradation.
