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

### Added

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
server when it detects one and states the fix — pin `Bridge:OpcUaPort` to a free port and
open that port in the firewall.

### Changed

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
(OPC DA with DA Groups, OPC UA with UA Subs, MX Component with PLC Groups), with arrows to
walk the rest and a counter at the foot (`2 / 4`). Drivers stands on its own. The group
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

### Fixed

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
