# DA worker isolation — design

**Date:** 2026-10-01 · **Branch:** `feature/da-containment-and-workers` · **Status:** implemented
(M8 lab verification pending)

## Goal

Contain the class of failure behind the Honeywell PMD issue: a vendor OPC DA server registered
as an in-proc COM server loads its DLL into `OpcBridge.App.exe`, so a fault in that DLL kills
the whole bridge (and the PMD/DCOM stack only serves specific plant accounts). Let an OPC DA
source run in a supervised child process, under its own account if needed, while values,
writes, metadata, the watchdog and the dashboard keep flowing through the bridge unchanged.

## Decisions

| Decision | Rationale |
|---|---|
| Parent-spawned child processes, **not** SCM services | The benefit is process separation and per-process identity, which children provide; services add admin rights, logon rights and a mini-ICU for nothing. |
| Opt-in per source: `inProcess` \| `group` \| `own` | Most sources need neither a special account nor crash protection; nothing pays for isolation unless asked. |
| `group` = one worker per run-as account; `own` = dedicated | A process has one token, so the account is the natural sharing unit. `own` gives the crash-prone source its own blast radius. |
| Same identity spawns with `Process.Start`; a different account with `LogonUser` (batch) + `DuplicateTokenEx` + `CreateProcessAsUser` | The account must be the process token — impersonation does not change the SID a vendor DCOM stack checks. |
| Parent stays `LocalSystem` when workers need other accounts | It holds *SeAssignPrimaryToken*/*SeIncreaseQuota*; the worker still carries the plant identity. This dissolves the "service must log on as `.\mesadm1`" coupling of the field fix. |
| Credentials on **stdin**, never argv | Same discipline as `--da-probe`. |
| Run-as password protected with DPAPI (LocalMachine), `dpapi:` prefix, plaintext migration | A Windows logon credential is stronger than a DCOM credential; `RemotePassword` deliberately stays as-is. |
| Length-prefixed JSON over a **named pipe**, protocol in `OpcBridge.Client.Workers` | Local-only, no listener port (the app's own discovery scans 8080–8180), reusable by a future thin/AOT worker. |
| Backoff 1/2/5/10/30 s; **quarantine** after 5 crashes in 10 min; never fall back in-process | A crash-looping vendor DLL must not spin, and the parent must not silently re-host the fault it was asked to contain. |
| Worker failures classified transient/terminal, surfaced as `SourceConnectionLostException` | The existing coordinator retry loop drives restarts; no new supervision loop in `BridgeWorker`. |

## Architecture

```
OpcBridge service (parent, LocalSystem)
  WorkerPlacement        source -> key: own:{sourceId} | acct:{account} | null (in-process)
  DaWorkerSupervisor     hosted service: spawn on demand, crash bookkeeping, quarantine,
                         stderr -> bridge log, status + lifecycle history for /api/workers
  DaWorkerProcess        one child: pipe server, bootstrap on stdin, sampling, shutdown/kill
  DaWorkerIdentity       spawn: Process.Start (same identity) or LogonUser+CreateProcessAsUser
  WorkerConnection       parent pipe endpoint: request/response by frame type+seq, push events
  WorkerSourceClient     proxy: ISourceClient + ISubscribableSourceClient +
                         ISubscriptionActiveSource + IRateGroupBoundSource
  RoutingSourceClientFactory  SourceClientFactory subclass: proxy or in-process
        │ named pipe (JSON frames)
        ▼
  OpcBridge.App.exe --da-worker (child, run-as account)
  DaWorkerSession        one real OpcDaClient per source; connect/read/write/metadata requests;
                         subscription values pushed as typed chunks; heartbeats
```

- `Program.cs` dispatches `--da-worker` before CrashLog/lock/web host, exactly like `--da-probe`.
- Values arrive on the pipe and are raised through `ValuesReceived`, so `BridgeWorker.OnSubscriptionValues`, the UA server, MQTT/Influx, the watchdog and `UpdateDaRead` are untouched.
- `IRateGroupBoundSource` preserves the "DA mapping change forces a session rebuild" behaviour for proxies.
- Exit contract: `0` clean, `70` pipe closed/parent gone, `71` bootstrap/platform error, `72` protocol violation.
- Worker-visible symptoms: `channel closed`/`timeout` → `SourceConnectionLostException` (retry); terminal errors fault the source.

## Config model

`worker` block on an OPC DA source: `mode` (`inProcess`\|`group`\|`own`), `runAsUser`,
`runAsPassword` (DPAPI at rest), `runAsDomain`. Normalization trims, drops a password without
an account, downgrades `group` without an account to `inProcess`, and omits the block when
default. The API validates (`DaWorkerOptionsValidator`) and never echoes the password
(`workerRunAsPasswordSet` boolean). Blank password on save = keep the stored one.

## Board and API

`GET /api/workers` (Viewer): supported/platform, parent pid/uptime/memory, per worker
(mode, pid, account, state `running|stopped|quarantined`, sources, memory, handles, restarts,
last exit, last error, heartbeat age), last 50 lifecycle events. `POST /api/workers/{key}/restart`
and `/kill?confirm=true` (Admin). `Ops ▸ Workers` renders it (Admin-only UI, reveal + route
guard like Issues/Troubleshoot) with a 3 s visible-tab poller.

## Non-goals (deliberate)

Service-per-worker; job objects (pipe-EOF is the parent-death cleanup); workers for UA/serial
sources; automatic isolation ("crashed twice → isolate"); NativeAOT/thin worker exe; DPAPI
migration of `RemotePassword`/`UaPassword`.

## Risks

| Risk | Mitigation |
|---|---|
| Pipe ACL/identity mismatch across accounts | Random 128-bit pipe name, server-side creation before spawn, connect timeout reported on the board. Explicit DACL pinning is a follow-up. |
| Batch logon right missing | Preflight message names the right; same-identity path avoids it entirely. |
| Value fidelity across JSON | Type-tagged `WireValue` with exhaustive round-trip tests. |
| Backpressure stalling COM callbacks | Worker writes through a single outbound queue; the parent's read loop never blocks on handlers. |
| Privileged spawn only testable on Windows | Logic factored into pure helpers (account normalization, argument quoting) with unit tests; the Win32 path is on the lab checklist. |
