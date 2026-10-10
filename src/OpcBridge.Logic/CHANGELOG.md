# Changelog

All notable changes to the OpcBridge Logic web app are documented in this file. The app ships
this file inside its assembly and serves it at **Guide ▸ Release notes**, so what the app shows
is exactly what is written here.

The app versions and releases on its own: this file is its release authority, and its newest
section is the `LogicVersion` the app reports and the `logic-v*` installer is stamped with
(`Directory.Build.props`). A release that changes only the bridge or the desktop apps does not
touch the Logic app's version, tag or release.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-10-10

### Added

**The Logic authoring surface now ships as its own web app.** The plant logic that lived in the
bridge dashboard's Tags ▸ Logic tab — interlocks, permissives, sequences, the IEC 61131-3 network
editor and the live state chips — is now the OpcBridge Logic app: a browser app on its own port
that talks to the bridge through its published API, like the desktop HMI and the Android viewer.
The bridge keeps the engine; this app is the whole editing surface.

**The editor gets the window, the block list moves to a dialog.** The cramped split screen is
gone: the block editor spans the full width, and a large Blocks dialog picks the block being
edited and manages the list — add, per-block delete, and the interlock-group headings the phone
shows. Live chips keep repainting once a second without rebuilding the inputs, so typing is never
disturbed.

**The last known blocks stay readable without the bridge.** Definitions and evaluated state are
cached in the browser, so an unreachable bridge shows the last known blocks with the connection
flagged in the header instead of an empty page; editing is disabled until the bridge is back.

**The app finds the bridge on its own.** `Bridge:BaseUrl` in `appsettings.json` pins the address
when set; otherwise the app probes the bridge's port range (8080–8180) using the same anonymous
identity probe the phone uses, follows a bridge that was restarted on another port, and shows
the bound address in the header.
