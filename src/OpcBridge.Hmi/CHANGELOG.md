# Changelog

All notable changes to the OpcBridge HMI Runtime are documented in this file. The
runtime ships this file inside its assembly and shows it under **Help ▸ Release
notes**, so what an operator reads in the app is exactly what is written here.

The runtime versions and releases on its own: this file is its release authority, and
its newest section is the `HmiVersion` the app reports and the `hmi-v*` installer is
stamped with (`Directory.Build.props`). A release that changes only the bridge or the
designer does not touch the runtime's version, tag or release.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

**The connection state is now live, not latched at connect.** The status pill, the Home bridge
card and each Config row kept the value the last successful connect produced — pulling the
network still read "Connected", while the Influx poll was already reporting the outage. The
runtime now follows the SignalR link itself: a dropped link reads "Reconnecting" on a warning
pill, a closed one reads "Failed" with the error in the row's tooltip, and a summary line names
the bridges behind the state. Heartbeats are tuned (5 s keep-alive, 15 s server timeout, retries
0/1/2/5 s then every 5 s), so a silent drop is noticed within about 15 seconds instead of 30.

**Tag values refresh as soon as the link comes back.** A single un-retried snapshot fetch stood
between a reconnect and fresh values, and a failure left the pre-outage values on screen. The
post-reconnect snapshot now retries on its own schedule, and if that schedule is exhausted the
background tick keeps trying until the values actually land — the status bar says "values
refreshed" when they have.

**One unreachable bridge no longer fails the whole connect.** A single bridge that could not be
reached tore every session down. Each bridge now connects independently: an unreachable one
shows Failed with its error and is retried on a backoff (2 s → 30 s) until it joins, while the
healthy bridges keep serving tags. Connect still reports failure when no bridge can be reached.

**Trend values now show the same digits as the rest of the app.** Hovering or pinning the chart
echoed the value stored in InfluxDB at full precision, so a tag the bridge rounds to a couple of
decimals — stored as a widened float, e.g. 12.34 as 12.340000152587891 — read as a long string of
digits in the value chips and on the Y-axis. The chart (hover chips, pinned readout and axis
labels) now formats to the same 3-decimal display precision as the pen table, trailing zeros
trimmed, so those numbers read 12.34 again.

## [1.8.1] - 2026-10-05

### Fixed

**Saving a renamed bridge now applies the new name to the live connections.** Save persisted the
rows but left the running bridges labeled with the names they connected with — a rename never
appeared in the tag browser, the bridge selectors or the status bar without a restart. Save now
reconnects a renamed bridge under its new name when the bridge list otherwise matches the live
connections (that bridge's tags reload immediately) and reports it in the status bar; when an
address or display store changed too, Connect still applies the whole list.

## [1.8.0] - 2026-10-04

### Added

**The Config page can save the bridge list without connecting.** The bridge rows (address,
display store, name) were only written to the client's local configuration when a connect
succeeded, so a prepared or edited list was lost on exit and there was no way to keep a
name or address change on its own. A Save button next to Connect now persists the rows and
reports the result in the status bar; it compares the rows — name included — with the saved
configuration, so it greys out while they match — including when an edit is reverted.
Connect keeps saving the list it connected with, as before.

### Fixed

**Saving the client config no longer builds a relative path when the app-data folder is
missing.** On Linux, `Environment.GetFolderPath(ApplicationData)` verifies the folder and
returns an empty string while it does not exist — in a container whose home had no
`.config` yet, the runtime then built `OpcBridge.Hmi/hmi-config.json` relative to the
working directory, where the `OpcBridge.Hmi` apphost file made every save fail with "The
file '/app/OpcBridge.Hmi' already exists". The folder is now requested with
`SpecialFolderOption.Create` (and falls back to the temp directory when none can be
created), so Save and Connect persist the bridge list — and the trend groups beside it —
again.

**Connect is no longer greyed out after changing a bridge address while connected.** The
button was only enabled while disconnected, so a changed or added OPC Bridge address could
not be applied without disconnecting first. Connect now compares the connection targets —
each row's address and display store — with the live connection: adding, removing or
changing one re-enables it, and reverting the edit greys it out again. Renaming a bridge
is a save-only change and leaves Connect alone. Clicking Connect tears the live sessions
down and rebuilds them from the rows.

## [1.7.0] - 2026-10-04

**The runtime now versions and releases on its own.** It carries the product version it
already shipped with — 1.7.0, the v1.7.0 release included the runtime installer — and
moves on its own cadence from here: only runtime changes bump `HmiVersion` and produce
an `hmi-v*` tag and release.

## [1.4.0] - 2026-10-02

### Added

**In-app release notes.** **Help ▸ Release notes** opens this file as it ships in the
assembly: what changed in the running version, no repository or installer needed.
Releases that did not change the runtime have no section here — the server's changelog
carries the whole product history.

### Fixed

**Tag browser hover and selection no longer flicker under the live-value stream.** Every
value batch (~10 per second) rebuilt the tag list, so the list control recreated its rows:
the row under the pointer lost its hover highlight until the mouse moved, and the selected
row flickered in the left rail. Rows are now updated in place, and the list is rebuilt only
when the tags themselves change (connect, disconnect, a mapping edit on the bridge). The
bridge and source selectors were refilled on every batch as well and now only change when
their entries do.

## [1.3.0] - 2026-09-24

### Added

**Application icon.** The runtime shipped without one, so Explorer and the Start Menu
showed the generic blank icon for `OpcBridge.Hmi.exe`. The exe now carries its own icon
and the Avalonia windows use it in the title bar, taskbar and Alt-Tab. The artwork is
generated rather than hand-drawn: `scripts/icons/make-icons.py` draws every size from
16px to 256px and writes the committed `src/OpcBridge.Hmi/Assets/opcbridge-hmi.ico`.

## [1.0.0] - 2026-09-19

First tagged baseline: the operator runtime shipped with the bridge as one product.

### Added

- **Operator runtime.** The Avalonia HMI runtime: authored process displays loaded from
  the primary bridge's display store (`/api/hmi/displays`), a tag browser split by
  bridge and source, saved trend groups (Ctrl+3), faceplates and popup trend windows. It
  speaks HTTP + SignalR only — no DA/UA/COM, and it never holds an InfluxDB token.
