# Changelog

All notable changes to the OpcBridge Logic Android viewer are documented in this file. The
viewer ships this file inside its assembly, so what you read matches the built APK.

The viewer versions and releases on its own: this file is its release authority, and its
newest section is the `MobileVersion` the app reports and the `mobile-v*` release is
stamped with (`Directory.Build.props`). A release that changes only the bridge, the HMI
runtime or the designer does not touch the viewer's version, tag or release.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.8.0] - 2026-10-08

### Added

- First release. Shows the plant logic authored in the bridge's dashboard **Tags ▸ Logic**
  tab: every block as a card with its state (READY / BLOCKED / NO DATA / DISABLED), the
  first blocking reason, and — for sequences — step progress with the current step.
- Block detail: each condition with its true/false/no-data mark, and — for a boolean
  condition — that state read plainly as **1** / **0** / —; a numeric comparison (a level
  above 50 %) keeps its live value instead, rendered with the tag's on/off texts or unit.
  The *what to do when not true* next-step text is highlighted while the condition is not
  true; sequences render as an ordered step flow.
- Live updates over the bridge's SignalR `/hmi` hub (`logic` snapshots and `values`
  deltas) with a 1 s snapshot refresh as the fallback, and automatic re-sync after a
  network drop.
- Action buttons defined on a block write their tag through the bridge's normal write
  path, after a confirmation dialog; notes can be left on a block and are listed newest
  first.
- Several bridges on one phone: a saved list (name + address) you switch between from the
  Logic tab's picker, one live link at a time. Each bridge keeps its own address, sign-in
  and tag values, so "Line 1" and "Packaging" can both be on the phone without mixing up.
- Settings: the saved-bridge list (tap to show a bridge, remove to forget it), add a bridge
  by host/IP with an optional name (the address is probed before it is saved), sign-in when
  the selected bridge requires it, and the live link state.
