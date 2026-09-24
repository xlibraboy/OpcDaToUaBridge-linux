# Changelog

All notable changes to the OpcBridge HMI Designer are documented in this file. The
designer ships this file inside its assembly and shows it under **Help ▸ Release
notes**, so what you read in the app is exactly what is written here.

Only releases that changed this app are listed; a release that ships the designer
unchanged appears only in the server's changelog (`CHANGELOG.md`). The version number
is the shared OpcBridge version from `Directory.Build.props`, the same one the bridge,
the runtime and the designer report.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
