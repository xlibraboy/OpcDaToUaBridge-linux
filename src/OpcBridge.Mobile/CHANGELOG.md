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

- **A light surface, with an Appearance selector.** The viewer now draws on either the dark
  plant-floor palette or a light daylight palette, chosen in Settings ▸ Appearance as **System**
  (follow the phone's day/night setting), **Light** or **Dark**. The choice is remembered across
  launches and switches live with no restart: page grounds, cards, fields, chips, state colours,
  the tab bar, the status/navigation bars and the Lucide icons all follow it. The light palette
  keeps the same semantic roles — state colours are darkened so streaming, blocked and pending
  words keep their contrast on white cards.
- **Offline viewing without an account.** The phone now keeps a durable copy of each bridge's
  synced dataset — the logic blocks, the areas' named tags, tag metadata and the operator
  notes — in a local SQLite database (`opcbridge-logic.db`) instead of transient cache files.
  The first sync seeds it and every later sync replaces it in one step, so a failed refresh
  leaves the previous copy intact. When the bridge is unreachable, Sign in offers **View
  offline data**: the Logic, Elements and block screens open without a username or password,
  structure only (no readings, no evaluated states, no writes) and labelled with the last sync
  time. The view ends the moment the bridge answers again, returning the app to Sign in.
- **The viewer requires a bridge account to open.** The app now starts on a sign-in screen:
  pick a saved bridge or add one, then enter a username and password checked against that
  bridge's own accounts (`/api/auth/login`). The Logic, Elements and Settings screens appear
  only for an accepted session; signing out, switching to a bridge with no session, or a
  session the bridge refuses returns the app to the sign-in screen. Every launch asks again —
  the account is not stored on the phone — and the account is required even when the bridge
  runs with `Auth:Enabled=false`. Only the read-only offline view opens without one.

### Changed

- **Offline screens show structure only.** Elements no longer renders last-known readings when
  the link is down: offline rows keep their names, tags and step order, but every contact,
  timer, counter and block state reads as no data. A lost link also no longer keeps stale
  readings in the phone's database.
- Settings' optional **Sign in** section became **Account**: it names the signed-in user and
  role and offers **Sign out**. The credentials now live on the sign-in screen.
- **The viewer was re-skinned and re-laid out to the V2 console design.** Each screen now
  follows the same carded hierarchy: a title with its live status line, square icon actions
  drawn from the frames' Lucide set at their exact sizes, a bridge link strip with compact
  mono sync/latency readings, and inline kind badges. Logic leads
  with READY/BLOCKED/NO DATA metric cards, Elements uses a segmented kind filter and grouped
  element cards, Settings groups bridges, account, sync/outbox and about, and the block screen
  carries its own back bar, checkpoint rail and send-note composer. The saved-bridge row now
  opens a sheet instead of a platform picker, so choosing a bridge cannot stall the page.
  The tab bar, splash and app theme follow the same dark plant-floor palette.
- **Choosing a bridge is a themed sheet, not the platform action sheet.** The picker — opened
  from the Sign in bridge row or the Logic tab's bridge chip — now draws the app's own scrim and
  bottom sheet in the palette's colours, on both appearances: the saved bridges with their state
  dot, address and an **ACTIVE** pill on the running one, a **New bridge…** row (Sign in's
  add-bridge form, or Settings ▸ Bridges from the tabs) and **Cancel**. Opening the sheet
  dismisses the soft keyboard, Android's back button closes it instead of leaving the screen,
  its rows repaint with an appearance switch, and an empty saved list says *No saved bridges yet.*
- **Sign in keeps adding a bridge out of the picking flow.** The saved-bridge card now shows
  only the picker and a **New bridge…** row; the host/name fields and **Add & connect** moved
  into their own card that opens on request (from the row or the picker sheet's **New bridge…**)
  and closes again once a bridge connects — so picking a saved bridge no longer sits next to an
  **Add & connect** button that answers *Enter the bridge host or IP first.*
- **The start-up bridge search can be interrupted.** Sign in used to lock the saved-bridge
  picker until the remembered bridge's search finished, so a phone away from that bridge's
  network could not switch to another saved bridge until the probe timed out. The picker now
  opens during the search, and picking a bridge cancels the search and connects the picked one.

### Fixed

- **The offline banner says when nothing is stored.** A bridge that has never synced now reads
  *offline · no stored structure yet* instead of implying a stored structure exists; the
  **View offline data** button stays hidden until a sync has stored one.
- **Adding a bridge no longer looks frozen.** The 8080–8180 port sweep is capped at eight
  seconds, runs off the UI thread, and shows a spinner with a **Cancel** button on both Sign in
  and Settings. Cancelling now stops the sweep immediately instead of walking every remaining
  port.

## [1.11.0] - 2026-11-02

### Added

- A **network latency state** for the selected bridge. While it is active the phone asks the
  public `GET /health` every ten seconds and shows the measured round-trip with its band:
  **GOOD · 12 ms** (≤ 50 ms), **FAIR · 87 ms** (≤ 150 ms) or **POOR · 210 ms** (above that);
  a lost link reads **OFFLINE**, and a ping that cannot be measured reads **latency —** rather
  than inventing a number. The state sits beside the sync badge on the Logic and Elements
  screens and on the bridge row and Sync section in Settings. It is measured separately from
  the snapshot refresh, so it reports the phone-to-bridge path, not how long three API calls
  took.

## [1.10.0] - 2026-11-02

### Added

- An **Elements** tab lists every element of every logic block in one place — contacts, coils,
  gates, timers and counters — grouped under their block's collapsible heading with the block's
  state and an element count. Rows keep their nesting indent, state mark, tag label, actual
  value, **should** line and timer/counter progress; a sequence row names its step. Search
  matches the block, element, tag or step, and kind chips narrow the list to interlocks,
  permissives or sequences. Tapping a row opens that block's Result.
- **The element list is persistent.** The phone keeps the last known evaluation and readings
  per bridge, so the Elements tab still shows the complete element list with its values after
  the link drops or the app is restarted, marked *last known · <time>*. The live Logic list and
  block screens still clear their readings on a lost link, so nothing is presented as current;
  only this explicitly-labelled view shows the saved data. The snapshot is refreshed on every
  successful sync and on live pushes (throttled), and notes reads no longer wipe its state.
- A **sync badge** on the Logic and Elements headers (and beside each bridge in Settings) reads
  **SYNCED**, **SYNCING…**, **OFFLINE**, **N PENDING** or **N failed**, with the last synced
  time. Tapping it opens Settings ▸ Sync. A toast of pending work cannot be painted over by a
  live value push: the state is derived in one place from the link, the read/flush in progress
  and the outbox.
- A durable **offline outbox**. Notes and actions taken while the bridge is unreachable are
  written to disk, counted on the sync badge and listed in Settings ▸ Sync with **Retry** and
  **Discard**. Notes flush automatically on reconnect and carry a client id, so a retry cannot
  duplicate one on the bridge. An action queued while the app knows it is offline is sent on
  reconnect; an action whose call failed after it may have reached the bridge is kept as
  *not confirmed* and is **never** replayed automatically. Pending actions older than two
  minutes are marked stale and wait for the operator's Retry; a manual Retry starts a fresh
  window. A note still in the outbox shows in the block's Notes section as *pending sync*.

- Blocks that read their area's **area tags** (the HMI Logic page's AREA TAGS tab) render fully:
  a named contact shows the entry and the tag it is bound to (`START_PB · Dry End Start Permit`),
  its live value is read through the resolved tag, and an action button writes the resolved tag —
  while an entry that has not been assigned yet reads `START_PB (not assigned)` and the write
  reports why instead of touching the wrong tag. The areas' tags travel with the definitions and
  are kept in the **offline cache** beside the blocks, so the labels and write targets survive a
  lost link like everything else.
- Each block card shows its **area** (e.g. `DRY END`) beside the kind, and the search box matches
  it too — the area is the plant section whose tags the block shares with every other interlock in
  it (authored in the HMI Logic page's AREA field).
- Relay elements in a network read properly: a **coil** row shows `COIL M01` with the bit it
  drives and a **relay contact** shows `NO`/`NC` with `relay M01` under it and the same
  **should 1 · actual 0** line as a tag contact, instead of an unnamed row — and relay rows no
  longer look themselves up in the tag cache, because they read the bridge's relay state, not a
  tag.
- The block screen carries the Result tab's **Checkpoints** rail: the network's parents — the
  AND / OR / XOR / NOT groups and the function blocks that own the checks below them; a coil
  publishes the result and a leaf contact is already named in the block's reason — appear once,
  in the network's own order, as state-coloured dots (green fulfilled, red open, muted no data)
  with a met/total count in the summary's colour. Only the dot is drawn; the parent's name and
  state are its accessible label, so a long interlock keeps every checkpoint in one wrapping
  strip. Tapping a dot scrolls its evaluated row to the middle of the screen and rings it for a
  moment, so "which group is open" and "where is it" are the same tap. Sequence blocks get the
  rail over their step cards too.
- The block screen's header shows the block's **kind** and its **output-tag feedback** (`OUTPUT
  PUMP1_RUN · VERIFIED` / `MISMATCH` / `UNVERIFIED`), so an interlock that agrees with itself but
  not with the plant reads apart; a sequence also shows its step progress (`1/2 steps · Open
  valve`).
- Each block card on the Logic tab **folds on its own chevron** to its name and state row, so a
  long list reads as a compact index while the blocks being worked on stay open; the fold follows
  the card through live state pushes and is remembered while the app is open.
- A result row's **tag line is a door**, as it is on the web Result tab: it opens a sheet with
  **where the tag comes from** — its area entry, recorded device contact and field state, mapped
  source, live reading, quality and writability — and **which blocks use it**, listing every block
  that reads, writes or drives the tag, each opening that block's Result. The tag label is
  underlined while it is a link; an unassigned entry or a name missing from the block's area says
  so in the sheet instead of showing a silent blank.
- The block screen's network rows draw the web Result board's **nesting rails**: one thin vertical
  guide per enclosing level, on the same 14 px step the rows indent by, so a deep interlock reads
  as a tree and a row's parents are visible beside it.

### Changed

- **A lost link clears the live screen instead of freezing it.** When the bridge link drops
  (including while it is reconnecting) or a refresh fails, every block state and tag reading is
  dropped to **no data** / **—** and the banner reads *offline · live values hidden · last synced
  …*; the block definitions, groups, tags and notes stay readable, the **Elements** tab keeps the
  last-known readings from the persistent snapshot, and the next snapshot or successful refresh
  fills the live readings back in. The live list never shows a cached reading as if the bridge
  had just served it.

### Fixed

- **Adding a bridge no longer stops the app when the address's socket closes.** Android's HTTP
  handler reports a connection closed before any response as `WebException: Socket closed`; the
  probe's failure filter did not include it, so the exception escaped the async **Add & Connect**
  handler and took the process down. The probe now treats a web, I/O or socket failure as "not a
  bridge" and keeps sweeping, and the Settings actions report a failure on the page instead of
  letting it reach the process.
- **The pages sit on the plant-floor dark ground again.** The Shell page fell back to the
  platform's light window background, so the Logic list, Settings and block screens drew their
  light headings on a white page — nearly invisible — while the cards kept the dark palette.
  Each page now sets `PageBg` itself, so the whole surface reads in the palette it was designed
  for.

## [1.9.0] - 2026-10-11

### Added

- The Logic tab lists blocks **under their interlock group's heading** (the group authored in the
  dashboard's Logic tab, e.g. *Primary Arm*), collapsible by tapping the heading — the heading
  keeps a live summary (*2 blocks · 1 blocked · 1 no data*) while its members are hidden, and an
  ungrouped-only list stays flat. Groups match case-insensitively, keep their collapsed state
  across state pushes, and only re-parent cards when the membership actually changes.
- The block screen renders the block's **IEC 61131-3 network**: every element as a row indented by
  its depth — contacts (NO / NC / comparison), gates (AND / OR / XOR / NOT) and function blocks
  (TON / TOF / TP, CTU / CTD, SR / RS, R_TRIG / F_TRIG) — each with its live state and, for a
  contact, the reading it needs against the live one: **should 1 · actual 0** (a mismatched line
  is coloured, the row keeps its ✓/✗ mark and state word). Timers and counters report their
  progress (*holding 1.5 s of 3 s*, *count 2 of 5*), and the network the bridge expanded a flat
  condition list into shows up the same way, so the simple and network forms read alike.
- **Offline mode.** The phone keeps the last logic definitions, evaluated state and tag values the
  bridge served, per bridge, and shows them on start-up and after a failed call with an **offline
  banner** naming when the data was last read (*offline · showing Plant 1 as of 2026-10-09
  12:31:05*) — the floor list still reads when the link is down, and the banner clears on the next
  successful refresh.
- **The block screen reads offline in full.** It opens instantly from the cached definitions,
  state and last values — no network call holds the render — and the **last notes the bridge
  served are kept per block**, so the notes section reads on the floor with the link down instead
  of coming up empty. A later notes read replaces them without making the list look fresher than
  it is; a failed one leaves the last-known set in place.

- Cards show the block's **tags** as chips under the name — the labels the bridge serves with
  the definition (authored in the dashboard's Logic tab, up to 8 per block).
- The Logic tab gained a **search box and filters**. The search matches the block name, the
  description or any tag (every word must match); tag chips narrow the list to the blocks
  carrying any of the ticked labels; the state row narrows it to Ready, Blocked, No data or
  Disabled. A *n of m blocks* line reports how much of the list is showing, the empty state
  says when a filter matched nothing, and live state keeps flowing into the filtered list
  without rebuilding it.

### Fixed

- **The empty list is honest about why.** A bridge that was never reached now reads *No data yet
  — no bridge answered on …* (or asks for the sign-in it needs) instead of *No logic blocks
  here*, and a bridge that answered with nothing authored no longer tells the operator to add a
  bridge they already have. A block that is not in the last synced list says so instead of asking
  for a refresh that cannot run.
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
