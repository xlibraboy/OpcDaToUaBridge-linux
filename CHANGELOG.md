# Changelog

All notable changes to the OpcBridge **Server** — the bridge, its web dashboard and the
shared libraries — are documented in this file. The bridge serves this file at
`GET /api/changelog` and renders it under **Help ▸ Release Notes**, so what operators
read in the app is exactly what is written here. It is also the release authority: the
version in `Directory.Build.props`, the MSI version and the GitHub release notes follow
its newest section.

The two desktop apps keep their own logs and list only the releases that changed them:
`src/OpcBridge.Hmi/CHANGELOG.md` (HMI runtime) and
`src/OpcBridge.Hmi.Designer/CHANGELOG.md` (HMI designer). All three share one version.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.6.1] - 2026-10-03

### Fixed

**Run-as workers start again — the bootstrap no longer carries a UTF-8 BOM.** The parent wrote
the worker's startup JSON on the child's stdin with `Encoding.UTF8`, which `StreamWriter`
prefixes with a byte-order mark — and `System.Text.Json` rejects a BOM. The child exited
(`bad bootstrap`) before it reached its pipe, the parent discarded the child's stderr, and the
operator saw only `did not connect to its pipe within 10 s`; the source was then faulted for
good on the first attempt. Every worker isolated under a *different* run-as account failed this
way — for any vendor server whose DCOM stack needs a plant identity — while the same-identity
path was unaffected because `Process.StandardInput` writes no BOM. The bootstrap is now written
and read as BOM-less UTF-8, a failed start folds the child's stderr into its message, and it is
retried with the crash backoff — quarantined after five attempts like any crash loop — instead
of faulting the source.

## [1.6.0] - 2026-10-03

### Added

**Tags ▸ Maps follows the Source dropdown.** The dropdown only drove the tag browser, so the
mapping list below it kept showing every mapping of the active tab and switching source never
changed the list. The list now narrows to the selected source before the text filter and sort,
the count keeps reading "shown / tab total", and the empty states tell apart a source with no
mappings yet, an empty tab, and a filter that matched nothing. Entering a tab lands on a source
that has mappings where one exists, so the list does not open empty while another source holds
the work.

### Changed

**Ops ▸ Troubleshoot no longer offers a redundant activation probe.** Every connect already
activates the server, the registration verdict runs automatically on failure, and worker mode
gives permanent process isolation — so the manual probe only duplicated paths that already
exist. Run checks stays; what is gone is the isolated ad-hoc activation test for a ProgID that
is not a configured source (#36).

### Fixed

**A worker-isolated source no longer reads every tag as a Double.** Worker-isolated OPC DA
sources rebuilt their tag mapping from a worker reference carrying only the item id and the poll
rate, so the data type fell back to the initializer default `Double`: every read asked the DA
server for `VT_R8` and booleans and strings arrived as doubles, which Live Values and the
dashboard faceplate then displayed as Double. The worker reference now carries the mapping's
data type (defaulting to `Auto`, the native source type) and the worker session rebuilds the
mapping with it, so worker reads no longer coerce types that were never configured.

**Live source status no longer blinks between retry ticks.** `BridgeState.Configure()` rebuilt
every source snapshot from scratch as *Disconnected*, and the coordinator re-runs it on every
retry tick while any source is down. A healthy source next to a retrying one therefore blinked
*Disconnected* each backoff cycle, and a bridge that was already running was pushed back to
*Starting* for as long as one of its sources retried — the "another source shows disconnected"
report (#35). Configure now merges the previous snapshot for configured sources and keeps the
connection state, errors and detection info of live sessions, and the bridge keeps *Running*
rather than falling back to *Starting*. For the same reason the LAST READ / LAST WRITE indicators
no longer alternate between "just now" and "–" while a source retries: while the bridge is
running, those liveness fields survive a reconfigure tick and are cleared only when it is
actually starting.

**The dashboard no longer flashes before the sign-in card on refresh.** Signing out and
refreshing painted the whole dashboard — nav rail, status pills, the Monitor page and its data
flow — and only then covered it with the sign-in card, because the card appeared after the
`/api/auth/me` round trip returned. Anyone could read the layout, the section tree and the last
known state of the bridge before being asked to sign in. The shell is now held off screen while
the check is in flight: a second inline script in the head marks the gate `pending` before the
body is parsed — the same trick that keeps the stored theme from flashing — and the stylesheet
keeps the topbar, the nav rail and every view hidden until an answer sets `signed-in` or
`signed-out`. A check that never returns, or an engine that never runs the script, therefore
leaves the sign-in card up rather than the dashboard. The card and the modal stack are untouched
by the guard, so an expired session or an idle sign-out still re-opens the card over what is on
screen rather than blanking the page.

## [1.5.0] - 2026-10-02

### Added

**An OPC DA source can run in its own worker process.** A vendor server registered as an
in-proc COM server loads its DLL into the bridge, so a fault there takes the whole bridge
down — Honeywell PMD's subscription crash is the open case. A DA source now opts into
isolation under **Sources → OPC DA → Worker Process**: **Own worker** (a dedicated child
process) or **Group worker** (every source with the same run-as account shares one process).
The worker logs on as the configured account — the plant case where the vendor DCOM stack only
serves `.\mesadm1` — through a real Windows logon (the account needs *Log on as a batch job*)
when the bridge runs as LocalSystem, or as the bridge's own identity otherwise; credentials
reach the worker on stdin, never in its command line, and the stored run-as password is
protected with DPAPI. **Ops ▸ Workers** (Admin) lists the parent and every worker — pid,
account, sources, memory, restarts, last exit — with **Restart**/**Kill** and the crash
timeline; a worker that crashes five times in ten minutes is quarantined instead of spinning.
Values, writes, metadata, the watchdog, MQTT/Influx and the dashboard behave exactly as for an
in-process source — the difference is where the vendor DLL lives.

**Host tools for a faulting in-proc DA server.** Two reversible scripts accompany the worker
feature for hosts that cannot isolate a source: `scripts/windows/enable-wer-localdumps.ps1`
enables Windows Error Reporting dumps for `OpcBridge.App.exe`/`DllHost.exe` (the native fault
left no `crash-*.log`), and `scripts/windows/enable-pmd-surrogate.ps1` can run an in-proc
server out of process in the system COM surrogate (`dllhost.exe`) under a run-as account, with
a registry-export rollback written before any change. The PMD procedure, verification and
rollback live in the field notes.

## [1.4.0] - 2026-10-02

### Added

**The bridge keeps a record of real issues.** Every release now embeds `ISSUES.md` beside the
changelog: one section per real issue — from a plant host, a field report or development —
stating what broke, where it was seen and how it ended, newest first, written by the AI agents
that investigate them. Admin users read it in the dashboard under **Ops ▸ Issues**
(`GET /api/issues`), so a deployed host can answer "is this a known problem?" without opening
the repository; the file already records the 2026-09-30 PRW11709 session, including the
still-open native crash of the in-proc PMD server under DA subscriptions (#34).

**Import from file can take a mis-picked mapping back.** Importing a tag list is quick to get
wrong — an MX export repeats the same name under every PLC, and a row added by mistake stayed
mapped until the faceplate's own Remove was dug out. Every import row that already holds a
mapping now carries an **Un-map** button beside its status chip: it removes that one mapping
behind the same warning the faceplate shows, then compares the file with the source again so
the row comes back as *not mapped* with its description and tick — map the right row in its
place, or re-add this one deliberately (#32).

**Credentials for external OPC UA clients.** The built-in UA server could already
enforce a username and password, but nothing exposed the setting and nothing enforced
it either — the check sat on an overload the stack no longer calls, so with the
requirement on a client activated a session with any password or none at all. Monitor
now has a **UA Server Access** card: tick *Require username & password*, set the
credentials, save, restart, and every external client (UaExpert, SCADA, historians)
must present them; the endpoint advertises the username/password token policy alone,
so anonymous sessions are refused instead of failing later. The stored password never
echoes back to the dashboard, blank credential sets are rejected, and the Help page
documents the flow. The credentials travel unencrypted — the server's only channel
policy is None, so this is an access gate, not transport security.

**The Ports card reports a port a second listener also holds.** The availability check
probed IPv4 only, so it could not see a process holding just the IPv6 side — the common
case being the OPC Foundation's **UA Local Discovery Server** (`opcualds`, installed with
Siemens SIMATIC WinCC, Matrikon and Kepware), which owns 4840 by convention for discovery.
On one rig the bridge and the LDS both ended up listening on 4840, and Windows treats
delivery between two sockets on one port as indeterminate: a client on that machine
resolving `localhost` to `::1` reached the LDS instead of the bridge's address space.
`PortHelper` now probes each address family separately, **Monitor → Ports** marks a port as
*also held on IPv4 / IPv6*, and startup logs a warning. The port the bridge listens on is
deliberately **unchanged**: the installer's firewall rules are built from the ports the MSI
was built with, so moving off 4840 by itself would leave the bridge listening somewhere the
firewall does not allow and silently unreachable from the LAN. The card names the discovery
server when it detects one and states the fix — move the port and its firewall rule from
**Monitor → Port Configuration**.

**Search the tag list in Maps.** A source with a few thousand tags was only workable
through the browser's own find: *Browse All Tags* loads the whole tree, and nothing in the
dashboard could narrow it to the tag you actually wanted. The Tag Browser now has a
**Search** box under the source picker that filters the loaded rows by display name or
item ID as you type — folders match too, the `..` row stays put so you can keep
navigating, and Escape clears the box (the browser's own clear button fires no input
event). The count sits beside the field: *12 of 340*, or *No tags match*. It filters what
a browse has already rendered, so press *Browse All Tags* (or open a folder) first; the
box clears when you switch source or sub-tab, and it is hidden on the address-based
Drivers / MX lanes, which have no browse tree (#24).

**Ports and their firewall rules are editable from the dashboard.** The bridge already moved
itself to the next free port when 8080 or 4840 was taken, but the move was one-way: the
installer's firewall rules stay on the ports the MSI was built with, so a rolled port left
the dashboard and the OPC UA endpoint unreachable from the LAN until someone added a rule by
hand. **Monitor → Port Configuration** now shows the running and saved ports, checks a
candidate on demand — free, held on IPv6 only, or in use by another process — with a *Use
next free* suggestion drawn from the same 8080–8180 and 4840–4940 ranges the bridge scans,
and saves the change for the next restart, saying so (a UA port change also moves the UA
endpoint and re-issues the certificate, exactly like startup auto-assignment). On Windows
the matching rule moves with a changed port, and an **Apply firewall rule** button fixes a
rule an earlier roll left behind; the plain `netsh` line stays on the card as the manual
fallback (#26).

**The bridge's memory can be measured — and its GC mode A/B'd — on a deployed host.** The
service's high working set (#27) is suspected to be its GC mode: `Microsoft.NET.Sdk.Web`
gives it **Server GC** (one heap per core) inside the x86 process the MSI installs for 32-bit
OPC DA COM servers, and nothing measured it. `scripts/windows/measure-windows-memory.sh` runs
the existing `monitor-opcbridge.ps1` on a deployed host over SSH, streams its verdict and pulls
the CSV into `dist/memory/` (gitignored); `--gc-mode Server|Workstation` first flips
`System.GC.Server` in the deployed `OpcBridge.App.runtimeconfig.json` and restarts the bridge —
the same property a `<ServerGarbageCollection>` csproj change writes, so both arms are tested
with no rebuild and the measured effect transfers to the shipped change. Environment variables
are deliberately not used: a service host or Task Scheduler does not pick up a machine variable
set after boot. `docs/ram-measurement.md` documents the flow and how to read the result.

**Maps can bulk-load a tag list from a file — compared with the source first.** Adding a few
hundred PLC tags one browse-and-Add at a time is not a workflow. **Import from file…** now sits
in the Tag Browser toolbar beside *Browse All Tags* and *Browse Folders*, and reads the CSV MX
OPC Configurator writes (File ▸ Save As ▸ CSV — the `#MX_DataTags` table: tag names and
descriptions, nothing else). Reading a file is a comparison, not an import: the bridge reads the
tags the source really exposes — the same recursive browse the Tag Browser does — and reports
both directions, so what you see is the source's address space, not just your saved mappings.
Every file row is *not mapped* / *already mapped* / *mapped with a different description* /
*repeated in the file*, plus whether the source exposes that name at all, and **only a row that
is on the source and not mapped yet can be ticked** — a typo in the file is shown but never
becomes a mapping. A row maps as the tag's **folder path plus its name** —
`DRYEND_PLC.Input_X.X000`, with MX's own `\Address Space` root dropped — not the bare name:
a real plant export carries an X000 under every PLC, and only the path keeps those tags apart
(both for the comparison and for the mapping that is stored). So reviewing one PLC never strikes
out another PLC's same-named tag, and the row shows both faces of the tag — the name you
recognize and the item id it will be keyed by. **Map selected (N)** stores exactly the ticked
rows, *Add all new* does the same for the whole group, and rows whose description differs can be
updated in place. Below the file's rows the dialog lists the other half of the reconciliation:
the tags the source exposes that the file never mentions, each marked with whether the bridge
already maps it. The file's
`\Address Space\<PLC>\<folder>` grouping stays a picker: a list covering several PLCs is
imported one PLC at a time, and the dialog opens on the group that names the selected source. A
source that cannot be enumerated — OPC DA browsing is COM, so it needs Windows and a reachable
server; an OPC UA server is walked up to 20 000 tags and says when the walk was cut short; a
serial driver has no tag list at all — still gets the mapped comparison, with the reason stated
instead of guessed. Every mapping also records **when it was added** (`addedUtc`, printed in the
faceplate's Basic tab and kept across edits — a stamp belongs to the insert, not to the last
save). `POST /api/mappings/import/preview` serves the comparison (#30).

**The bridge keeps a log that outlives the process.** The only record of what the bridge was
doing lived in the bridge: `DashboardLogStore` is a 500-entry ring buffer in memory, and the
console goes nowhere when the app runs as a Windows service. A service that died therefore took
its own evidence with it — on 2026-09-29 a plant host's bridge stopped unexpectedly twice
(Service Control Manager event 7034) and neither stop could be explained. It now writes
`logs/bridge.log` beside its runtime state (under `OPCBRIDGE_DATA`), rolled by size at 2 MB with
three files kept so a chatty failure mode cannot fill an industrial PC's disk, and every line is
written and closed on the spot so the tail survives a process that vanishes mid-sentence. The
file and the dashboard's Logs panel carry the same events — Information for the bridge's own
categories, Warning for everything else — and a logger that cannot write is ignored rather than
fatal.

**A crash leaves a report.** An exception that escapes every managed boundary now writes
`logs/crash-<stamp>.log` with its type, message and full stack alongside the process, port,
session and version — exactly the fields the Windows Error Reporting record for the 2026-09-29
stop lacked, where `e0434352` ("the CLR raised an unhandled managed exception") was all it said.
`AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` are both hooked, and
an unobserved task exception is marked observed: letting a lost background operation tear the
bridge down would turn a hiccup into a plant-wide outage. The crash path deliberately shares no
lock with the logging pipeline — it may be running on the thread that died holding one.

**The Release carries a 64-bit installer too.** The MSI's server is 32-bit because a 64-bit
process cannot activate 32-bit OPC DA COM servers, which left a host whose DA servers are
64-bit only with no installer at all — only the portable win-x64 zip. That host now has one:
both architectures ship as an MSI (`OpcBridge-<version>-win-x86.msi` and `-win-x64.msi`), built
from the same package source and carrying the same service, firewall rules, `OPCBRIDGE_DATA`
layout and HMI apps, only with that architecture's server. The two share one UpgradeCode, so
installing one over the other replaces it — at the same version too, instead of failing with
*another version of this product is already installed* on the service and data directory both
would own — which makes installing the other package how a station changes architecture, with
its runtime state under `C:\ProgramData\OpcBridge` surviving. Apps & features names the 64-bit
install **OpcBridge (64-bit)**, so which build a host holds is readable on the machine.

**OPC DA troubleshooting tells you why a source cannot connect.** The PRW11709 session fixed a
host by hand — the 32-bit COM registration pointed at a build-machine path (`F:\SetUpWork\…`)
that does not exist on the plant machine — and the same class of defect (ProgID → CLSID mapping,
missing `InprocServer32`/`LocalServer32` path, the 32-vs-64-bit registry view a 32-bit bridge
actually reads, discovery keys) now diagnoses itself. **Ops ▸ Troubleshoot** (Admin, matching
Ops ▸ Issues) walks the chain read-only and reports each hop with the registry view, the raw path
and the fix; **Run activation probe** additionally asks the real question — can this identity
activate the server — in a **separate child process**, so a faulty in-proc server DLL (the PMD
crash class, still open) kills only the probe and reads as a failed probe, never a failed bridge.
When a DA source fails to connect, the read-only checks also run once automatically and note a
deduplicated verdict on the durable log, so the next incident leaves its explanation in
`logs/bridge.log`. Honeywell PMD ProgIDs additionally compare the registered DLL against the
installed Honeywell location and point at the field notes.

**Live Values and the faceplate show both value timestamps.** OPC UA stamps every value twice —
the source stamp (when the device, DA server or upstream UA server produced it) and the server
stamp (when the server accepted it) — and UaExpert shows both; the dashboard collapsed them into
one. **Ops ▸ Live Values** now labels the value's own time **Source Timestamp** and adds
**Server Timestamp** beside it: the bridge's own receive time, the same clock the bridge's UA
server reports to its clients, with the full local time in the cell tooltip. The tag faceplate's
live panel labels both and adds the signed gap (`+3 ms`), which is transport latency plus any
source/bridge clock offset. Topology freshness reads the bridge receive time too, so a DA
server whose clock is skewed no longer paints fresh tags yellow. MQTT payloads and InfluxDB
points are unchanged: `t` and the point time stay the source stamp.

### Changed

**The installer's license page names AM2-DEV51, not a person.** The license agreement every
MSI shows still read `Copyright (c) Budi Kurniawan. All rights reserved.` — the publisher fix
(#28) changed the maker Windows lists, but the page the installer itself displays kept the
author's name. `packaging/msi/License.rtf`, shared by all three packages, now reads
`Copyright (c) AM2-DEV51. All rights reserved.` (#33).

**The Release carries one installer per app.** The MSI was one product offering the bridge,
HMI runtime and designer as features of a single feature tree, so a station could not install
or upgrade one without the others. Each app now has its own installer — the bridge at
`OpcBridge-<version>-win-x86.msi` / `-win-x64.msi`, the operator runtime at
`OpcBridge-HMI-<version>-win-x64.msi` and the designer at
`OpcBridge-Designer-<version>-win-x64.msi` — built from a package source each
(`packaging/msi/OpcBridge.wxs`, `OpcBridge.Hmi.wxs`, `OpcBridge.Designer.wxs`). They are
separate products with separate UpgradeCodes: install, upgrade and uninstall are independent,
a bridge upgrade no longer removes the desktop apps, and the desktop apps install on a host
that runs no bridge. Apps & features names each one (`OpcBridge`, `OpcBridge (64-bit)`,
`OpcBridge HMI`, `OpcBridge Designer`). The bridge keeps its x86/x64 pair, and those two
still share one UpgradeCode, so a station changes architecture by installing the other
exactly as before; the desktop apps stay win-x64-only and each gets its own `Program Files`
folder. A station upgrading from the earlier combined package gets the bridge from its
installer and reinstalls the desktop apps from theirs. The portable server zips are no
longer built by the workflow or attached to the Release; `scripts/msi/build-portable.sh`
still builds them locally, through the same `publish_skip.py` runtime-state filter, for
hosts deployed by extraction.

**The bridge runs on Workstation GC.** `Microsoft.NET.Sdk.Web` hands a web app Server GC, which
allocates a heap and a dedicated collection thread per core and sizes for throughput — the wrong
trade for a bridge that is idle most of the time (this app spends about 0.1 s of CPU per ten
minutes). Measured on the plant host — 4 GB RAM, 4 cores, the x86 install, 2 OPC DA sources,
259 tags — with both arms freshly restarted over the same window: private bytes 99 → 115 MB and
still climbing on Server GC against 53.5 → 53.7 MB flat on Workstation, working set 171 MB
against 115 MB, and **virtual address space 825 MB against 378 MB**. That last figure is the one
that matters: the process is x86 because a 64-bit one cannot load 32-bit OPC DA COM servers, so
it has roughly 2 GB to work in and Server GC was holding most of a gigabyte of it. CPU is
unchanged at ~0.6 s per ten minutes. `docs/ram-measurement.md` carries the full A/B and how to
re-run it (#27).

**The OPC UA identity no longer carries the `ohmypi` vendor prefix.** The server's
`ApplicationUri`, `ProductUri` and the tag namespace URI were built from a leftover vendor
prefix (`urn:ohmypi:…`), so UaExpert and other clients showed a name the product does not
use in the address-space namespace list. Everything is `urn:opcbridge:…` now — server
`urn:opcbridge:{ApplicationName}` and `urn:opcbridge:opc-bridge`, tag namespace
`urn:opcbridge:opc-bridge:tags`, shared UA client `urn:opcbridge:{ApplicationName}` and
`urn:opcbridge:opc-bridge-client` — and the server reports `ManufacturerName` `OpcBridge`.
Two consequences matter on an upgrade. The application URI is written into the SAN of the
application certificate and the stack treats a stored certificate for a different URI as
invalid rather than re-issuing one, so `UaCertificateIdentity` deletes it (with its private
key) before the check and the bridge issues a certificate for the new identity on the same
start — **every UA client must trust the new bridge certificate**, and upstream UA servers
must trust the new client certificate. Node IDs are untouched: `ns=2;s={sourceId}/{itemId}`
keeps the same index and identifier, so subscriptions and node lists survive; only clients
that reference the namespace *URI* need a re-browse.

**The sidebar folds.** The Tags, IoT, Historian, Ops and Help groups in the rail are
collapsible now: each header is a toggle with a caret, every group starts closed, and
navigating to one of its pages — by click, pager, wizard follow-up or deep link — opens
its group on the way. A rail that listed every page always now shows five labels until
you ask for more. Sources keeps its pinned pager and never folds.

**The UA root folder is "Bridge Tags" now.** UA clients browsing the server saw every
mapped tag under a folder named "OPC DA Tags" — wrong the moment a source feeds tags
from OPC UA, Melsec or S7-200 instead of DA. The folder under Objects is now
**Bridge Tags** (symbolic name `BridgeTags`, same `ns=2` namespace); variable node IDs
(`ns=2;s={sourceId}/{itemId}`) are untouched, so client subscriptions and node lists
keep working across the rename.

**Release notes are per app now.** The changelog was one file covering the bridge and
both desktop apps; the server keeps its own — this one, the release authority that the
MSI version and the release page follow — while the HMI runtime and the designer ship
theirs (`src/OpcBridge.Hmi/CHANGELOG.md`, `src/OpcBridge.Hmi.Designer/CHANGELOG.md`),
each listing only the releases that changed it. All three keep the shared version, and
each app shows its own file under Help ▸ Release notes.

**The Sources rail pages one source at a time.** The group listed all eight entries flat,
so the rail grew a line for every source added and would keep growing. `Sources` stays
pinned at the top — it is the overview of every source, not one of them — and everything
under it is a pager showing one source at a time: its own line and the sub-page beneath it
(OPC DA with DA Groups, OPC UA with UA Subs), with arrows to
walk the rest and a counter at the foot (`2 / 3`). Drivers stands on its own. The group
therefore keeps the same height however many sources are configured, and stepping away to
another group leaves the carousel where it was. Only the pages that own a connection carry
the 8px state square (green connected, amber degraded, red faulted, grey when the page has
no sources), with the word on the button's title because at rail width there is no room
for it. Every entry stays a real nav button with its route, so bookmarks, `aria-current`
and the rail's scroll-into-view are unchanged, and `+ Add Source` moves to the right of
the SOURCES header, mirroring the label-left / action-right pattern the page headers use.

**UA bandwidth is measured, not estimated.** The Diagnostics tab showed a bandwidth
number derived from `notifications/sec × ~80 bytes` — a guess that was wrong for every
value type, and never zero even with no client connected. Each value change is now
encoded with the stack's own binary encoder and the real notification payload size
(client handle + DataValue) is summed per second, so an Int32 tag costs the ~18 bytes it
actually costs and a long string carries its own length. The box is labelled **Measured
Bandwidth** and reports notifications and bytes both per second and as running totals;
`/api/diagnostics` exposes the measured `bytesPerSec` / `totalBytes` (the old
`estimatedBytesPerSec` is gone, and the monitor script follows). The number still counts
one payload per value change rather than one per connected client, and TCP/UA message
framing stays outside the measurement — both stated in the box tooltip.

**The MSI publishes the product as AM2-DEV51.** Windows labels an installed program's maker
with the word *Publisher* — the column in Programs and Features, the line on the Apps &
features entry, and the `Publisher` value under the uninstall registry key — and it read
`Budi Kurniawan`, the author's own name, because the WiX package carried it as
`Manufacturer`. The installer now carries **AM2-DEV51**
(`packaging/msi/OpcBridge.wxs`), so a station lists the site rather than a person (#28).
Nothing else in the package moves: the product name stays `OpcBridge`, and the upgrade code
and version handling are untouched, so an installed station picks the new publisher up with
its next MSI upgrade.

### Fixed

**A vendor's browse quirks no longer look like an empty address space.** The Tag Browser treated
any non-zero HRESULT from an address-space enumerator as the end of the list, yet a server may
legally return the last names together with `S_FALSE` — one server answering that way browsed as
empty while a better-behaved server on the same machine listed its tags, and a failed
`OPC_BRANCH` / `OPC_LEAF` call, a refused `GetItemID`, a folder that cannot be entered or a root
move the server does not implement all vanished behind the same empty result. Browsing now
consumes every item each reply reports, tolerates the root-move and organization differences,
and falls back to a flat (`OPC_FLAT`) browse when a hierarchical walk of the root finds nothing.
What still fails is named: warnings print above the tag tree in the Tag Browser and are logged
as a Warning, so a server that cannot be browsed says which call was refused and with which
HRESULT instead of showing *No tags or folders here*.

**The OPC DA per-source subscription switch is honoured.** A DA source saved with its
Subscription switch off still subscribed to its server: with I/O mode *AutoDetect* the
decision consulted the global master switch alone, so the per-source flag only took effect
after switching the source to *Sync* — the workaround one plant host needed before its
PMD server stopped receiving callbacks. *AutoDetect* now combines both switches, the same
way the OPC UA path always has.

**The OPC DA watchdog timeout is configurable per source.** It was fixed at 60 s, so a
subscription source whose tags legitimately update less often than a minute was torn down
and reconnected on every scan — the flap the watchdog exists to avoid for everything else.
The nested `OpcDa` block now carries `watchdogTimeoutMs` (0 disables the watchdog),
defaulting to the same 60 s when unset like the UA side.

**A slow service stop is no longer reported as a crash.** When the host's stop window
(`HostOptions.ShutdownTimeout`) elapsed while an OPC source was still being torn down,
`WindowsServiceLifetime.StopAsync` threw `OperationCanceledException` out of `Main`: every
controlled stop wrote a `crash-*.log` and exited through the unhandled-exception path. The
shutdown now notes the timeout on the durable log and exits quietly.

**Event Log logging works in the self-contained Windows build.** `System.Diagnostics.EventLog`
loads `System.Threading.AccessControl` when it first writes to a source, and the publish did
not carry that assembly — so every Event Log write failed with `FileNotFoundException` and a
harmless event (a client resetting a Kestrel connection) surfaced as an error and a crash
report. The assembly is now referenced by the app.

**The service restarts itself after a crash.** The package configured no recovery actions, so a
bridge that died stayed down until a person noticed — which is how one host sat without its
mirror after stopping at 09:10 and again at 15:47 on 2026-09-29, each time logging Service
Control Manager event 7034 and nothing more. The MSI now sets restart-on-failure for the first
three failures, with a five-second delay so a start-up crash loop cannot hammer and a one-day
reset of the failure count.

**The historian no longer ships enabled against a Docker-only hostname.** The stock
`appsettings.json` enabled InfluxDB pointed at `http://host.docker.internal:8086` — a name that
resolves inside Docker Desktop and nowhere else — so every install that had never configured a
historian of its own spent its startup trying that address, landed on Faulted, and stayed there:
`InfluxWriteDrainAsync` drops every point while the writer is not Connected, so the panel read
"Faulted" while the tags meant for it were silently discarded, and nothing retried because the
automatic recovery is armed by a failed *write*, not a failed connect. Auto-connect is off by
default now and the URL is `http://localhost:8086` — the default `InfluxOptions` already carried
— so a station that wants a historian opts in and names a server that exists.

**The memory sampler no longer under-reports growth.** `monitor-opcbridge.ps1` called anything
under 2 MB/min "No growth trend" — but a bridge that is genuinely flat sits at ~0.1 MB/min, and
the Server GC arm above climbed at +1.71 MB/min while being reported as flat. The bands are
1.0 MB/min (suspect) and 0.3 MB/min (watch), and a no-growth verdict now quotes the rates it
measured instead of asserting flatness whatever they were.

**PLC group rate when none is supplied.** Creating a PLC group through `POST
/api/plc/groups` without an `updateRateMs` landed it on the 100 ms floor, so a group made
by a script or an API client polled ten times faster than the dashboard's own Add PLC Group
modal, which offers 1 s. A group posted without a rate now takes 1 s
(`SourceConfigMigration.DefaultPlcGroupRateMs`), the same rate the dashboard shows by
default; a rate that is supplied still clamps up to the 100 ms minimum.

**Enter submits the Add Mapping box.** The mapping form only answered the Add Mapping
button — the dashboard's global Enter/Space activation deliberately skips inputs — so a
typed tag had no keyboard path. That cost the most on address-based sources (MX
Component, Drivers), where the manual box is the only way to add a tag because there is
no tag tree to browse. Enter in either the Item ID / Address field or the UA node field
now runs the same add as the button.

**Auto-typed tags no longer fault their source on the first reading.** A mapping whose
DataType is *Auto* — the default, and what a tag added by address gets — is mirrored by a UA
node whose declared DataType is the abstract `BaseDataType`. The new bandwidth measurement
asked that node for its wrapped `Variant`, which the stack cannot build without a concrete
type: it threw `Unable to cast object of type 'System.Boolean' to type 'Opc.Ua.Variant'`. The
exception escaped the value update into the poll cycle, so the source was parked in **Faulted**
and its readings were cleared — an all-Auto rig, such as an MX Component source of bit tags,
faulted every one of its sources and showed no readings at all. The measurement now wraps the
value itself, the way the stack builds it for a read and for a published notification, so no
declared type can turn a diagnostic into a source failure.

**The dashboard Start Menu entry shows the bridge icon.** The Server feature's only Start
Menu entry is a `.url` — the dashboard opens in a browser — and the installer gave it no icon
of its own. Windows draws a URL shortcut that way through the default browser's handler, which
on a Windows 10 Start Menu with Edge is a blank page, so the entry still read as a missing app
icon after the three exes got theirs. The `InternetShortcut` now pins `IconFile` to the
installed `OpcBridge.App.exe` (index 0) — the same icon as the Apps & features entry — while
the HMI and Designer shortcuts keep resolving theirs from their own exes.

**The Start Menu dashboard entry follows the port the bridge actually listens on.** The
installer writes the shortcut from the port the MSI was built with (8080), so on a host
where something already held 8080 the bridge moved to the next free port and the entry
opened a dead page while the bridge was healthy one port over. The bridge now rewrites the
shortcut's URL on every start to the port it bound, keeping the icon the installer pinned
(#29). The entry also ships as a real `.url` now — WiX's default for the element is a
binary `.lnk` — which is what makes the file rewritable in place. The firewall rules still
come from the MSI's build-time ports; Monitor → Port Configuration moves a rule when a port
is changed or rolled.

**Monitor's UA Access and Port Configuration cards keep a row per control.** The
require-credentials label is a direct child of its field row, so it inherited the shared
104 px field-label column and "Require username & password (off = anonymous access)"
wrapped onto four lines; the port rows kept each label beside its input, so a narrow window
pushed *Check* / *Use next free* onto a line of their own. The checkbox label now takes its
natural width, and each port stacks its label above a 150 px input column with the buttons
bottom-aligned to the input — both cards read as one line per control at a normal window
width.

### Removed

**The dashboard's resolve-to-desktop relaunch is gone.** The session-0 banner carried a
*Resolve* button that wrote two PowerShell helpers next to the exe, registered a temporary
`OpcBridgeResolve` scheduled task with `-LogonType Interactive` and then stopped the bridge so
the desktop-session copy could take the single-instance lock and the ports. A bridge installed
as a Windows service cannot move itself across sessions, so the button, the endpoint
(`POST /api/session/resolve`, admin-only) and its helpers are removed (#25). The session-0
banner stays and names the deployment fix instead: run the published-task deployment with
`register-published-task.ps1 -LogonType Interactive` (or use a real PLC instead of GX
Simulator) — the S4U/Interactive choice belongs to install time, not to the dashboard.

**The MX Component source type is gone, and with it PLC Groups and the Pause/Resume control.**
`MxComponent` was a Windows COM path to a Mitsubishi PLC through MELSOFT MX Component 4
(`ActUtlType`, logical station numbers 0–1023): its own **Sources → MX Component** page, a
driver project (`OpcBridge.Drivers.MxComponent`), `sourceType: "MxComponent"` in `sources.json`,
the `POST /api/drivers/mx-component/test-connection` and `GET …/address-ranges` endpoints, and
the MX-specific validation and diagnostics branches. It is removed whole (#31): install MX
Component's runtime **or** the app, not both. Two features built on top of it go with it —
**PLC Groups** (named polling groups, `sources.json` `PlcGroups` / `mappings.json` `plcGroup`,
`POST /api/plc/groups[/remove]`, `GET /api/plc/groups`, the PLC Groups page and the faceplate
group picker) and the **Pause/Resume** control (`POST /api/da/sources/pause`, the runtime-only
pause flag and the Paused badges), which existed to release the MX Component COM port. The
sources that replace it are unchanged and fully supported: the serial drivers on **Sources →
Drivers** (`MelsecA3n`, `S7200Ppi`) and `OpcUa` inbound sources, or plain `OpcDa` — the MX OPC
server reached as an ordinary OPC DA source still works, GX Simulator session notes included.
The default update rate stays fixed at 1 s; its message now points at per-tag Update Rates for
other cadences. **Deployed configs:** a `sources.json` row with `sourceType: "MxComponent"` is
now dropped at load (and skipped on config import) with a `Source '<id>' uses the removed
'MxComponent' source type` warning, instead of silently collapsing to a broken OPC DA source;
`mxComponent` and `plcGroup` keys elsewhere are ignored on load and disappear on the next save.
Mappings that pointed at a dropped MX source stay in `mappings.json` but are inert — remove them
from Maps or by source id. Also removed with it: the driver's D-bit/bit-in-word batching, the MX
effective-rate display (#23), the MX resume classifier (`MxConnectErrorClassifier`, #5), the
address-range catalog endpoint (re-homed for the serial A3N editor as
`GET /api/drivers/melsec-a3n/address-ranges`) and `scripts/demo/pause-rig.sh`.

## [1.3.0] - 2026-09-24

### Added

**Idle sign-out.** A dashboard left open on an unattended workstation stayed signed in
forever: its 1-second poll counted as activity, so the session's sliding lifetime never
lapsed. Sessions now end after `Auth:IdleMinutes` (default 30) without real input — the
tab tracks mouse, keyboard, wheel, scroll and touch activity, shared across tabs because
they share one session cookie, then returns to the sign-in card explaining why. The
server drops the session on the same window, so a closed tab, a shut-down browser or a
replayed cookie cannot outlive it; set `IdleMinutes` to `0` to disable the check and keep
only `SessionHours`.

**Application icons.** The three Windows apps shipped without one, so Explorer and the
Start Menu showed the generic blank icon for `OpcBridge.App.exe`, `OpcBridge.Hmi.exe` and
`OpcBridge.Hmi.Designer.exe`. Each exe now carries its own icon — the bridge, the operator
runtime and the designer — the Avalonia windows use theirs in the title bar, taskbar and
Alt-Tab, and the installer gives the product an Apps & features entry icon. The artwork is
generated rather than hand-drawn: `scripts/icons/make-icons.py` draws every size from 16px
to 256px and writes the committed `src/*/Assets/*.ico` files.

### Changed

**Status bar rails.** The bar was one flat row, so the free space landed wherever it
fell: the signed-in user sat wedged between the status pills and the theme picker, while
the clock was pushed alone to the far edge. The bar is now two rails — bridge status
(brand, version, pills) on the left, and the operator cluster (`Administrator`, role,
`Password`, `Sign out`) joined with the theme picker and the clock on the right — so
identity and controls read as one group and the slack lands between the rails.

### Fixed

**Session warning banner buttons.** `Resolve` and `Dismiss` sat a wide, uneven distance
apart — both buttons carried `margin-left: auto`, so the browser split the free space
between them instead of pushing the pair to the right edge. They now ride in one
right-aligned actions group using the banner's own 12px gap, and every state of the
banner (warning, relaunching, resolve failed) renders from one markup contract.

**Release notes layout.** Help ▸ Release Notes rendered the changelog into the Guide's
two-column flex layout, so every paragraph, heading and bullet became its own squeezed
column instead of a block of prose. The notes now render into the prose container,
hard-wrapped lines reflow into the paragraph or bullet they belong to, and markdown
links render as links.

**Unbounded handle growth from the resource sampler.** The 5-second sampler behind
Monitor ▸ Resources opened a process handle for every sample and never released it, so the
bridge's handle count climbed by one every 5 seconds for as long as it ran — the panel that
exists to warn about unbounded handle growth was itself inflating the number it reports,
and private bytes crept up with it. A bridge up for 90 minutes was holding 692 process
handles. `Sample()` now disposes the `Process` object it opens, which frees the handle
immediately instead of leaving it to the finalizer.

**Thread leak while a source cannot connect.** A connection attempt that failed left the
client it had just built — and the dedicated COM thread that client owns — to the garbage
collector, because only the success path kept a reference to it. The coordinator retries an
unreachable source every few seconds, so a server that stayed down leaked one thread and its
handles per retry. A client that never reaches the session table is now disposed on the way
out, while a failure after the session is registered still leaves the running session intact.

**COM leak when a DA server refuses the callback subscription.** If a server exposed
`IConnectionPointContainer` but its `Advise` failed, the connection point was dropped
without being released, and the bridge re-attempts the subscription on every poll tick — so a
server in that state leaked one COM reference per poll while falling back to polling. The
failure path now releases the connection point, mirroring the unsubscribe path.

## [1.2.0] - 2026-09-23

### Added

**Windows installer.** A WiX MSI installs OpcBridge straight onto a Windows host as a
feature tree, so a station takes only the parts it needs:

- **OpcBridge Server** — the bridge, installed as a Windows service that starts
  automatically at boot. The installer opens firewall ports 8080 (dashboard) and 4840
  (OPC UA) and adds a Start Menu shortcut to the dashboard.
- **OpcBridge HMI Runtime** — the Avalonia operator client, with a Start Menu shortcut.
- **OpcBridge HMI Designer** — the standalone display authoring tool, with a Start Menu
  shortcut.

The installer is x86 only, because a 64-bit process cannot load 32-bit OPC DA COM
servers (Matrikon, MX Component) without DCOM surrogate setup. Hosts that only use
64-bit or out-of-process DA servers take a portable self-contained build instead
(`scripts/msi/build-portable.sh` produces the win-x86 and win-x64 zips).

Runtime state stays outside the install directory: the installer points the
machine-wide `OPCBRIDGE_DATA` at `C:\ProgramData\OpcBridge`, so an upgrade replaces
binaries and never touches `users.json`, `mappings.json`, `sources.json`, `displays/`
or the PKI store.

The installer is built by `.github/workflows/windows-release.yml` on a Windows runner,
because WiX cannot link an MSI on Linux.

### Changed

- The bridge now also runs as a Windows service when launched by the service control
  manager (`UseWindowsService`); interactive launches are unchanged. A service runs in
  session 0, where session-bound PLC simulators (GX Simulator via MX OPC) cannot
  deliver values — the dashboard's session banner and resolve-to-desktop relaunch
  already cover that case.

## [1.1.0] - 2026-09-23

### Added

**Dashboard sign-in with user roles.** The bridge can now require a login and enforce
it on every endpoint.

- Four roles, each inheriting the one below:
  - **Viewer** — read live values, dashboards, trends, diagnostics, sessions and logs.
  - **Operator** — Viewer plus writing tag values (faceplate/UA writes).
  - **Engineer** — Operator plus configuration: sources, mappings, interlinks, drivers,
    PLC groups, UA subscriptions, MQTT, InfluxDB, browse and config import/export.
  - **Admin** — Engineer plus user management.
- User accounts live in `users.json` beside `mappings.json` and are managed under
  **Ops ▸ Users**: create, change role, enable/disable, reset password, delete.
  Passwords are stored as PBKDF2-SHA256 hashes (100,000 iterations, per-user salt);
  hashes and salts never leave the bridge.
- Endpoints: `POST /api/auth/login`, `/api/auth/logout`, `GET /api/auth/me`,
  `POST /api/auth/password` (change your own password), and the Admin-only
  `/api/auth/users`, `/api/auth/users/update`, `/api/auth/users/password`,
  `/api/auth/users/remove`.
- Failed sign-ins are throttled (5 failures per minute, per user and client address).
- In-app release notes: **Help ▸ Release Notes** reads `GET /api/changelog`, so a
  deployed bridge can show what changed without opening the repository.

### Changed

- The application version now comes from a single source (`Directory.Build.props`)
  instead of being duplicated per project; `/api/version`, `/api/app-info` and the
  assembly metadata all report the same number.
- The dashboard hides the sections a role cannot use: navigation entries that require
  Engineer (Sources, Drivers, PLC Groups, Maps, Interlinks, MQTT, InfluxDB) are not
  shown to Viewer/Operator, and the Users page is Admin-only.

### Security

- Authentication is opt-in via `Auth:Enabled`. Enforcement is API-level through one
  policy table, not merely hidden in the UI: reads require Viewer, mutations require
  Engineer, tag writes require Operator, user management requires Admin.
- Sessions are server-side and in-memory with a sliding 12-hour lifetime, carried in
  an HttpOnly, SameSite=Strict cookie; restarting the bridge signs everyone out.
- The last enabled administrator cannot be demoted, disabled or deleted, and deleting,
  disabling or re-roling an account ends that account's live sessions immediately.
- With `Auth:TrustHmi` (default true) the endpoints the Avalonia HMI apps use stay open
  on the LAN because those apps hold no credentials; set it false to require a session
  for them as well. Note that the dashboard faceplate writes tags through the same
  `/api/hmi/write`, so a trusted-LAN deployment leaves that one write path reachable
  without signing in.
- A fresh install seeds user `admin` with password `admin` and logs a warning at
  startup — change it before exposing the dashboard.

## [1.0.0] - 2026-09-19

First tagged baseline: the OPC DA → OPC UA bridge with its web dashboard, HMI runtime
and HMI designer. Everything below shipped between the initial commit (2026-06-17) and
this release.

### Added

- **OPC DA sources (Windows).** Hand-declared COM/DCOM interop with no vendor SDK:
  server browse and tag browse, remote DCOM with credential impersonation, one OPC DA
  group per poll rate, subscriptions with automatic polling fallback, per-tag deadband,
  and per-item failure isolation (a deleted or denied tag yields a BAD value plus retry
  instead of tearing down its source).
- **OPC UA server.** An in-process OPC UA endpoint mirroring source reads as namespaced
  nodes (`ns=2;s={sourceId}/{itemId}`), with mapping-driven AccessLevel, DataType and
  metadata refreshed in place, write-through to the source, and read/write rejection
  for write-only and read-only tags respectively.
- **OPC UA inbound sources.** Outbound OPC UA client sources (SecurityMode None/Sign/
  SignAndEncrypt, policy Basic256Sha256, optional user token) that subscribe to the
  mapped NodeIds, retry failed monitored items, and support named per-source
  subscriptions with independent update rates.
- **PLC drivers.** Mitsubishi MELSEC A3N (1C frame, serial), Siemens S7-200 PPI (serial)
  and Mitsubishi MX Component (Windows COM), each with address parsing/validation, a
  connection test and a driver-configured source form.
- **Multi-source registry and PLC groups.** Live source registry persisted to
  `sources.json`, per-source and per-tag update rates, named PLC groups with per-group
  I/O mode, and tag-count/limit reporting per rate group.
- **Interlinks.** Provider → consumer tag forwarding within and across sources, with
  derived per-rule health (flowing / idle / faulted), forwarding telemetry and a
  DA-to-DA topology view.
- **Dashboard.** Single-page ASP.NET Core dashboard covering Sources, Drivers, DA/UA/MX
  maps, Interlinks, MQTT, Traffic, InfluxDB, PLC Groups, UA Subs, DA Groups, and Monitor
  (Live Values with type, effective rate and quality), Diagnostics, Sessions, Logs,
  Diagram (zoom/pan topology), Guide and About.
- **MQTT.** Publish mapped tags to an external broker and browse received/published
  topics in the Traffic monitor.
- **InfluxDB historian.** Opt-in per-tag historical logging to InfluxDB 2.x/3.x plus a
  trend-history proxy (`/api/hmi/trends`) so HMI clients never hold an Influx token.
- **HMI.** Avalonia operator runtime (authored process displays, tag browser split by
  bridge and source, saved trend groups, faceplates and popup trend windows) and a
  standalone Designer, both speaking HTTP + SignalR only.
- **Resilience.** Reconnect with backoff, subscription watchdog, per-source fault
  isolation with a "Partial" aggregate state, per-source write queues (no cross-source
  re-enqueue), and Windows session-0 detection with a dashboard banner and one-click
  relaunch into the interactive desktop session.
- **Tests.** The xUnit suite in `tests/OpcBridge.LoadTest` covering the API seams,
  drivers, stores, UA node mapping and the dashboard DOM contract, plus an OPC UA
  load-test rig under `tests/loadtest`.
