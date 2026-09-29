# Measuring bridge memory on Windows (issue #27)

The Windows service's working set is high. The leading suspicion is the GC mode, and it is
verifiable without touching the code:

- `Microsoft.NET.Sdk.Web` sets **`System.GC.Server: true`**, so the bridge runs with **Server
  GC** — one heap per core, sized for throughput, not for the small footprint of a bridge.
- The MSI ships **x86 only** (a 64-bit process cannot load 32-bit OPC DA COM servers), so the
  process has a ~2 GB address space to work in.

The measurement path below A/Bs exactly that, on a deployed host, without a rebuild.

## The A/B in three commands

Run from the Linux checkout (any branch that has these scripts; they are uploaded to the host
on every run, so the deployed copy does not have to be current):

```bash
# 1. Baseline: the bridge as deployed (Server GC)
scripts/windows/measure-windows-memory.sh --label baseline --minutes 10

# 2. Switch to Workstation GC — restarts the bridge, waits 60 s, then samples
scripts/windows/measure-windows-memory.sh --label workstation --gc-mode Workstation --minutes 10

# 3. Put it back (optional, or keep Workstation if step 2 confirms)
scripts/windows/measure-windows-memory.sh --label server --gc-mode Server --minutes 10
```

Defaults: host alias `winvm-direct`, 10 samples per minute, scratch dir
`C:\Windows\Temp\opcbridge-measure`, results in `dist/memory/`. `--host` picks another host
(e.g. the plant PC); `--help` lists everything.

## What `--gc-mode` actually changes

`set-bridge-gc-mode.ps1` flips `System.GC.Server` in the **deployed**
`OpcBridge.App.runtimeconfig.json` (found from the running process, so an MSI install at
`C:\Program Files\OpcBridge` and a portable publish both work), restarts the bridge — the
task or the service, whichever is installed — and waits for `/health`. The runtimeconfig is
read once at process start, so the restart is what applies it.

The file is the honest lever: it is the same property a `<ServerGarbageCollection>` change in
`src/OpcBridge.App/OpcBridge.App.csproj` writes, so the measured effect transfers to the
shipped change. Environment variables (`DOTNET_gcServer=0`) are *not* used on purpose: an
installed service is started by the service host or the Task Scheduler, neither of which
picks up a machine variable set after boot, and the MSI sets no environment for the service at
all.

## Reading the result

Each run leaves a `.log` (the sampler's own output) and a `.csv` beside it. The comparison is
between the two `.log` **Trend** and **Verdict** blocks:

- `working set` falling over the window with `private` flat, handles and threads flat — that
  is the GC returning memory. Server GC on an idle-ish bridge is the usual cause of a working
  set that sits high and never comes back down.
- `private` climbing steadily (+ MB/min) with handles/threads flat is *managed growth*, not a
  GC-mode problem — that is a different issue (unbounded stores, per-value churn) and the
  mode switch will not fix it.
- Handles or threads climbing has its own meaning, spelled out by the verdict text.

Compare like with like: same tag count, same sources connected, same client attachments, and
give both arms the same window. The 60 s warm-up after a switch keeps the restart's cold start
out of the sample.

## Caveats

- Working set is the metric the issue is about; it is **not** the managed heap. If the two arms
  are close, get GC-level detail with `dotnet-counters monitor -p <pid> --counters System.Runtime`
  on the host (the VM already has a .NET SDK; `dotnet tool install -g dotnet-counters`), and
  read `gc-heap-size` and the gen0/1/2 rates alongside.
- A 32-bit process fragments sooner than a 64-bit one, so an arm that looks "the same" in
  managed terms can still differ in working set.
- The sampler needs no dashboard sign-in. `--user/--password` adds the app's own counters from
  `/api/status` and `/api/diagnostics`; that pair travels on the command line, so it is visible
  to process lists on the host — omit it unless the extra counters decide the question.
- `dist/memory/` is gitignored: results are evidence, not artifacts to commit.

## Result: Workstation GC wins (measured 2026-09-29, WORKSTAT02)

Run on the plant host — a Precision T1700, 4 GB RAM, 4 cores, the x86 MSI install with 2 OPC DA
sources and 259 mapped tags. Both arms were sampled on a **freshly restarted** service with the
same 60 s warm-up and the same 9.67-minute window, so the GC mode was the only variable.

| | Server GC | Workstation GC |
|---|---|---|
| private bytes | 99 → 115 MB, **+1.71 MB/min** | 53.5 → 53.7 MB, **+0.02 MB/min** |
| working set | 152 → 171 MB, **+1.95 MB/min** | 115 → 116 MB, **+0.09 MB/min** |
| virtual | ~825 MB | **~378 MB** |
| threads | 30–35 | 26–32 |

**−53% private bytes, −33% working set, −54% virtual address space**, and the Server arm was still
climbing when its window closed while the Workstation arm had already plateaued. CPU is unchanged
(~0.6 s per ten minutes either way) — this workload is far too small for Server GC's throughput
advantage to buy anything.

The virtual figure is the one that matters most: the process is x86 (a 64-bit one cannot load
32-bit OPC DA COM servers), so it has ~2 GB of address space, and Server GC's per-core heaps with
larger segments were holding most of a gigabyte of it. Results are kept in `dist/memory/`
(`arm-Server-workstat02.csv`, `arm-Workstation-workstat02.csv`).

The shipped change is `<ServerGarbageCollection>false</ServerGarbageCollection>` in
`src/OpcBridge.App/OpcBridge.App.csproj` — the same property `--gc-mode` writes, so what was
measured is what now ships. No heap hard limit was added: `System.GC.HeapHardLimitPercent` is a
percentage of physical memory (2.4 GB at 60% of this host's 4 GB), which is larger than the
address space it would nominally guard, and a hard limit converts memory pressure into an
`OutOfMemoryException` — the failure it is meant to prevent.

### When the numbers are suspicious, check the mode first

A bridge that is genuinely flat sits at ~0.1 MB/min; the Server arm's +1.71 MB/min was
unmistakable growth, but `monitor-opcbridge.ps1`'s old 2 MB/min band reported it as "No growth
trend". The bands are 1.0 and 0.3 MB/min now. If a measurement looks flat but the service's
working set is high, confirm the running process actually picked up the switch — the runtimeconfig
is read once at process start, so a switch without a restart changes nothing.

## After the measurement

Workstation GC won, so the change lives in the project file and not on the host:

```xml
<PropertyGroup>
  <ServerGarbageCollection>false</ServerGarbageCollection>
</PropertyGroup>
```

It ships inside the publish, so the MSI service picks it up with no environment setup — still the
right shape here, because a service host does not read a machine variable set after boot. No
`System.GC.HeapHardLimitPercent` guardrail was added; the Result section says why.

The per-value and per-cycle allocation findings from the issue are a separate, larger piece of
work; measure them the same way (same script, longer window) before and after.
