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
- Block detail: each condition with its true/false/no-data mark, the live value rendered
  with the tag's on/off texts or unit, and the *what to do when not true* next-step text
  highlighted while the condition is not true; sequences render as an ordered step flow.
- Live updates over the bridge's SignalR `/hmi` hub (`logic` snapshots and `values`
  deltas) with a 1 s snapshot refresh as the fallback, and automatic re-sync after a
  network drop.
- Action buttons defined on a block write their tag through the bridge's normal write
  path, after a confirmation dialog; notes can be left on a block and are listed newest
  first.
- Settings: bridge host with a **Scan** (probes the bridge's own port range), sign-in when
  the bridge requires it, and the live link state.
