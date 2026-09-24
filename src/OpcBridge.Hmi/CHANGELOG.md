# Changelog

All notable changes to the OpcBridge HMI Runtime are documented in this file. The
runtime ships this file inside its assembly and shows it under **Help ▸ Release
notes**, so what an operator reads in the app is exactly what is written here.

Only releases that changed this app are listed; a release that ships the runtime
unchanged appears only in the server's changelog (`CHANGELOG.md`). The version number
is the shared OpcBridge version from `Directory.Build.props`, the same one the bridge,
the runtime and the designer report.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
