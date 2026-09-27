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

## After the measurement

If Workstation GC wins, the change belongs in the project file, not on the host:

```xml
<PropertyGroup>
  <ServerGarbageCollection>false</ServerGarbageCollection>
</PropertyGroup>
```

plus a `System.GC.HeapHardLimitPercent` guardrail if the working set still needs a ceiling —
both ship inside the publish, so the MSI service gets them with no environment setup. The
per-value and per-cycle allocation findings from the issue are a separate, larger piece of
work; measure them the same way (same script, longer window) before and after.
