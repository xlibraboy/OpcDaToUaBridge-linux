# Changelog

All notable changes to the OpcBridge HMI Designer are documented in this file. The
designer ships this file inside its assembly and shows it under **Help ▸ Release
notes**, so what you read in the app is exactly what is written here.

The designer versions and releases on its own: this file is its release authority, and
its newest section is the `DesignerVersion` the app reports and the `designer-v*`
installer is stamped with (`Directory.Build.props`). A release that changes only the
bridge or the runtime does not touch the designer's version, tag or release.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

**Pick a source and bind a tag — no more typing IDs.** The Designer now connects to the
bridge and lists its configured sources in the rail, with the source type and connection
state. Select a widget, choose **Choose tag…**, and the picker shows that source's mapped
tags with their live values: pick one and **Bind tag** — the widget previews the real value
on the canvas right away. The free-text `bridgeId`/`sourceId`/`daItemId` boxes are gone, and
the panel warns when a saved binding no longer maps to a tag on the bridge.

**Sign-in for bridges with authentication enabled.** Source listing needs a signed-in user,
so the rail offers **Sign in…** (username/password, remembered for the session) and shows who
is signed in; signed out, the Designer still binds tags from the live snapshot.

**Live values while designing.** The Designer follows the same feed the runtime uses
(SignalR + the HMI tag snapshot), so bound widgets and the tag picker show values and quality
as they change. The toolbar pill reports the link: **Live**, **Snapshot**, or **Offline**.

## [1.7.0] - 2026-10-04

**The designer now versions and releases on its own.** It carries the product version it
already shipped with — 1.7.0, the v1.7.0 release included the designer installer — and
moves on its own cadence from here: only designer changes bump `DesignerVersion` and
produce a `designer-v*` tag and release.

## [1.4.0] - 2026-10-02

### Added

**In-app release notes.** **Help ▸ Release notes** opens this file as it ships in the
assembly: what changed in the running version, no repository or installer needed.
Releases that did not change the designer have no section here — the server's changelog
carries the whole product history.

## [1.3.0] - 2026-09-24

### Added

**Application icon.** The designer shipped without one, so Explorer and the Start Menu
showed the generic blank icon for `OpcBridge.Hmi.Designer.exe`. The exe now carries its
own icon and the Avalonia windows use it in the title bar, taskbar and Alt-Tab. The
artwork is generated rather than hand-drawn: `scripts/icons/make-icons.py` draws every
size from 16px to 256px and writes the committed
`src/OpcBridge.Hmi.Designer/Assets/opcbridge-designer.ico`.

## [1.0.0] - 2026-09-19

First tagged baseline: the designer shipped with the bridge as one product.

### Added

- **Display authoring.** The standalone OpcBridge HMI Designer: a palette of widget
  types, add/move/resize on the display surface, undo/redo, and Open/Save of display
  documents against the primary bridge's store. It speaks HTTP only.
