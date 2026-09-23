# Changelog

All notable changes to OpcBridge are documented in this file. The bridge serves this
file at `GET /api/changelog` and renders it under **Help ▸ Release Notes**, so what
operators read in the app is exactly what is written here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
