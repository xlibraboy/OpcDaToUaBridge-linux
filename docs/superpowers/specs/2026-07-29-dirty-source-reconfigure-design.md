# Phase 3: Dirty-only source reconfigure

- **Date:** 2026-07-29
- **Branch:** `feature/modif-bridge-app`
- **Status:** Implemented

## Goal

When one source’s config changes (or fails), do **not** dispose/reconnect every source or cancel every poller.

## Behavior

| Event | Action |
|-------|--------|
| Source connection settings unchanged | Keep `ISourceClient` session |
| Source connection settings changed / added / removed | Dispose + reconnect **that** source only |
| Mapping change | UA: reconcile MonitoredItems; **OPC DA only**: force rebuild those DA sessions (rate groups) |
| Poller failure on source S | Tear down **S** only; next tick reconnects S |
| UpdateRate / UseSubscriptions change | Keep client; restart **S** pollers only |

## Implementation notes

- `SourceSession` holds per-source `PollerCts`
- `ReconfigureSessionsAsync` returns changed source ids; skips equal connection settings
- `StopPollersForSourceAsync` / `RestartPollersForSourcesAsync` scope poller lifecycle per `SourceId`
