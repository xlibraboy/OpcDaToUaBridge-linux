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

### Added

- Cards show the block's **tags** as chips under the name — the labels the bridge serves with
  the definition (authored in the dashboard's Logic tab, up to 8 per block).
- The Logic tab gained a **search box and filters**. The search matches the block name, the
  description or any tag (every word must match); tag chips narrow the list to the blocks
  carrying any of the ticked labels; the state row narrows it to Ready, Blocked, No data or
  Disabled. A *n of m blocks* line reports how much of the list is showing, the empty state
  says when a filter matched nothing, and live state keeps flowing into the filtered list
  without rebuilding it.

### Fixed

- **Add & Connect now finds a real bridge.** The port probe read the bridge's reply off the
  network stream — which a keep-alive connection never ends — so it sat until its 1.2 s
  timeout and reported *no bridge answered* although the bridge had answered in milliseconds
  (the same read works against a test listener that closes the connection, which is why the
  suite never caught it). The reply body is read directly now, and the typed port gets 3 s.
- A bridge that asks for a session reads as **the bridge requires sign-in** instead of *no
  bridge answered*: the definitions/tags/notes calls dropped the HTTP status code, so a 401
  sign-in gate looked like an unreachable bridge.

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
