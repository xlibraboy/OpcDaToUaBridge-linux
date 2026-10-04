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

**Connect is no longer greyed out after editing the bridge list while connected.** The
button was only enabled while disconnected, so adding or changing an OPC Bridge address
could not be applied without disconnecting first. Any bridge-row edit — adding, removing
or changing an address, store or name — now re-enables Connect, which tears the live
sessions down and rebuilds them from the rows; the button greys out again once a
successful connect has applied the edits.

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
