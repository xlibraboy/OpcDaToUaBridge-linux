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

### Added

**The Logic editor's rows insert and move where you are working.** Every row — a condition, a
network element, a step, an action — now carries `+↑` / `+↓` beside `✕` to insert a fresh row
directly above or below it, so a list is built where it is being read instead of only appended at
its end, plus `↑` / `↓` to move the row up or down. In the network a row moves **with the rows
nested under it** — a gate keeps its inputs instead of leaving them behind — and insert places the
new row beside it at the same nesting level; the buttons grey out at the ends of a level.

**The Logic page shares tags per area: an area's tags assigned once, read by all its blocks.**
Beside Editor and Result, **Tags** edits the selected block's area's tags — type the area (`DRY
END`, `WET END`, …) in the editor's new **AREA** field, or in the tab's own box (the same value),
then `+ New tag` for each signal the area's blocks read. Every block of the area shares one list,
so a new group or interlock filed under the area re-uses the tags already there instead of starting
its own: a signal is assigned **once per area**, the tab shows **used by N blocks**, and the block
list (and the **Blocks…** window) now reads two levels — **AREA**, then the interlock group, then
the blocks. The interlock group is a display heading only; a block with no area keeps working —
its named tags resolve through the name it stores, else its interlock group (the legacy fallback),
which the tab names while offering to move the block onto an area. An entry can be created
**empty** and bound later: the row pickers already offer it, the block evaluates **no data for
START_PB (not assigned)** until a tag is picked, and the tab's **LIVE** column shows the bound
tag's value so the assignment can be checked in place. **New block** now asks for the block's
**AREA** (and its interlock group) first, so the area is set before anything is authored — and
every tag picked on a row afterwards **joins the area's tags automatically**: it becomes one named
entry (named after the tag's display name) that every block of the area picks from its own **AREA
TAG** list, which hides the direct SOURCE/TAG pickers while selected (the `✕` clears it and the row
reads a direct tag again; the store keeps the two forms mutually exclusive). A block authored
before areas existed joins its area the moment it is given one — or on its next save — so nothing
has to be moved by hand. **`Delete area tags`** is refused while blocks still read them (the bridge
names how many), and **Save** stores the tags and the block together, whichever changed
(`✓ Area tags saved.` when only the area's tags moved). The Result tab labels a named contact as
`START_PB · Line 01 Start Permit`, and the page's **Guide…** documents the whole flow.
**The network editor authors internal relays (M coils).** The element kind list gained **Coil —
drive internal relay (IN, M)**: it takes one input and drives the relay it names, and a contact
row gained a **RELAY** field that turns it into a relay contact reading that relay instead of a
tag — its tag pickers disappear while a relay is typed and a numeric comparison is coerced back
to ON/OFF, because a relay reads a bit. The relay field suggests the relays the loaded blocks'
coils already drive, so a relay authored in one block is nameable in the next, and a coil row
hides SEVERITY (an output has no "warn only"). Editor rows and the Result tab label relay
elements with the relay they read or drive (`COIL · relay M01 · 1`), the indent clamp follows the
engine's raised bound (16 levels, 256 elements), and the page's **Guide…** documents the
scan-order rules: a coil's write is visible to everything evaluated after it, a relay nobody
drives reads unknown, and a disabled block's coils do not drive anything.

**The runtime authors and watches the plant logic.** A new **Logic** page (View ▸ Logic, Ctrl+4)
carries the whole logic surface, per bridge: the block form (name, kind, enabled, interlock group,
description, tags), the flat **condition** editor (sentence, source/tag pickers,
ON/OFF/`>`/`<`/`=`, OR group, hold timer, severity, next step), the **IEC 61131-3 network** editor
(indented element rows — contacts, AND/OR/XOR/NOT, TON/TOF/TP, CTU/CTD, SR/RS, R_TRIG/F_TRIG —
with `Convert to IEC network` and back), sequence **steps** with an optional completion handshake,
and **actions** the phone renders. Every editor row is its own bordered line with a label on each
field — nothing is keyed off a column header — and a network row carries a rail for each level it
nests under, so both forms reflow with the window instead of scrolling sideways. Every row shows
the bridge's live state as a chip (1 / 0 / no data, or the live value); the **block list** stands
beside both tabs, every block under its interlock group heading with its state pill, first reason
and sequence progress, so the block being edited is picked and kept in sight from the screen that
edits it, and the **Blocks…** window is where blocks are created and deleted. The **Result** tab
renders the selected block's evaluated configuration the way the phone does — every element its
own bordered row, the live reading in large mono digits, with `should 1 · actual 0`, timer and
counter progress and the tag it reads under it, nested by a rail for every level — fed by the
bridge's `logic` hub push plus a once-a-second state poll while the page is open; it names the
time the state last arrived, never rebuilds rows under the operator, and shows a newly picked
block's state at once instead of the one before it. Definitions save through the bridge's own
`/api/logic*` API (its errors shown verbatim, Save greyed until the draft differs); while a bridge
is unreachable the page keeps the last known blocks on screen and disables editing. A **Guide…**
button carries the logic guide the dashboard used to describe.

### Fixed

**The Logic page's empty list says which of the four things happened.** Reading the blocks for
the first time, an unreachable bridge, a definitions read the bridge refused, and a genuinely
empty list all read as "No logic blocks yet" — a failed fetch looked like an empty plant. The
list, the blocks summary and the Result pane's empty state each name the actual state now, and a
read that fails no longer caches an empty list: the next load retries.

**Editing greys out in the same tick as the link drop.** The rows' own add, remove, indent and
move controls only followed the bridge's editability on the next draft reload, so a link that
dropped while the page sat open left them live against a draft that could not be saved. They now
follow the link state as it changes, beside the "Editing is disabled" hint.

**The Result pane's freshness line names a dead feed.** The line already counted up the age of
the last snapshot, but a bridge that dropped only said so up in the bridge bar, never in the pane
showing the frozen readings. It now reads "stale — bridge unreachable" (or "stale — reconnecting")
in the warning colour, and switching bridges no longer carries the previous bridge's timestamp.

**The confirm dialog's safe answer is the default one.** The destructive confirmations (delete a
block, discard unsaved changes, drop a network) offered the destructive button first, focused and
default, so a stray Enter triggered the very loss the dialog was asking about. Cancel is now the
first, focused and default action; the destructive button takes an explicit Tab. **A cancelled
block pick in the Blocks… window no longer closes the window** — declining the unsaved-changes
confirm kept the draft but still dismissed the picker, so the operator had to reopen it to choose
another block.

### Accessibility

**The app is usable without a pointer.** The destructive confirms focus their safe answer; the
Blocks… window, the trend-group picker (its filter box), the guide and the release notes (their
Close button) and the faceplate (its write box, on a writeable tag) each open with the control
their task starts on. The display surface's widgets answer Enter and Space to open the faceplate,
the keyboard equivalent of the click, and block rows announce name, kind, state and reason as one
stable name so a truncated row stays readable to a screen reader. The logic page's tag and source
pickers now carry the full tag identity (name and source) as their accessible description rather
than only in a hover tooltip, step rows read name and state as one announcement, and the Config
page's display picker is named.

## [1.8.2] - 2026-10-06

### Fixed

**The connection state is now live, not latched at connect.** The status pill, the Home bridge
card and each Config row kept the value the last successful connect produced — pulling the
network still read "Connected", while the Influx poll was already reporting the outage. The
runtime now follows the SignalR link itself: a dropped link reads "Reconnecting" on a warning
pill, a closed one reads "Failed" with the error in the row's tooltip, and a summary line names
the bridges behind the state. Heartbeats are tuned (5 s keep-alive, 15 s server timeout, retries
0/1/2/5 s then every 5 s), so a silent drop is noticed within about 15 seconds instead of 30.

**Tag values refresh as soon as the link comes back.** A single un-retried snapshot fetch stood
between a reconnect and fresh values, and a failure left the pre-outage values on screen. The
post-reconnect snapshot now retries on its own schedule, and if that schedule is exhausted the
background tick keeps trying until the values actually land — the status bar says "values
refreshed" when they have.

**One unreachable bridge no longer fails the whole connect.** A single bridge that could not be
reached tore every session down. Each bridge now connects independently: an unreachable one
shows Failed with its error and is retried on a backoff (2 s → 30 s) until it joins, while the
healthy bridges keep serving tags. Connect still reports failure when no bridge can be reached.

**Trend values now show the same digits as the rest of the app.** Hovering or pinning the chart
echoed the value stored in InfluxDB at full precision, so a tag the bridge rounds to a couple of
decimals — stored as a widened float, e.g. 12.34 as 12.340000152587891 — read as a long string of
digits in the value chips and on the Y-axis. The chart (hover chips, pinned readout and axis
labels) now formats to the same 3-decimal display precision as the pen table, trailing zeros
trimmed, so those numbers read 12.34 again.

**Boolean tags now plot in the trend charts.** A Boolean tag with InfluxDB history drew an
empty chart ("No history") even though its points were stored and served: the runtime
deserializes history values as JSON, and the numeric converter behind both the trend window
and the faceplate's 1h chart did not recognise a JSON boolean (`true`/`false`), so every
sample was dropped before the chart saw it. Booleans now map to the digital strip's 0/1 band
and draw their square wave like any other digital pen.

**The boolean indicator widget follows live values again.** The widget's own on/off coercion
missed JSON booleans — the shape the tag cache holds after wire deserialization — so a bound
lamp stayed on its OFF text whatever the tag read. It now uses the shared coercion the
faceplate already used.

## [1.8.1] - 2026-10-05

### Fixed

**Saving a renamed bridge now applies the new name to the live connections.** Save persisted the
rows but left the running bridges labeled with the names they connected with — a rename never
appeared in the tag browser, the bridge selectors or the status bar without a restart. Save now
reconnects a renamed bridge under its new name when the bridge list otherwise matches the live
connections (that bridge's tags reload immediately) and reports it in the status bar; when an
address or display store changed too, Connect still applies the whole list.

## [1.8.0] - 2026-10-04

### Added

**The Config page can save the bridge list without connecting.** The bridge rows (address,
display store, name) were only written to the client's local configuration when a connect
succeeded, so a prepared or edited list was lost on exit and there was no way to keep a
name or address change on its own. A Save button next to Connect now persists the rows and
reports the result in the status bar; it compares the rows — name included — with the saved
configuration, so it greys out while they match — including when an edit is reverted.
Connect keeps saving the list it connected with, as before.

### Fixed

**Saving the client config no longer builds a relative path when the app-data folder is
missing.** On Linux, `Environment.GetFolderPath(ApplicationData)` verifies the folder and
returns an empty string while it does not exist — in a container whose home had no
`.config` yet, the runtime then built `OpcBridge.Hmi/hmi-config.json` relative to the
working directory, where the `OpcBridge.Hmi` apphost file made every save fail with "The
file '/app/OpcBridge.Hmi' already exists". The folder is now requested with
`SpecialFolderOption.Create` (and falls back to the temp directory when none can be
created), so Save and Connect persist the bridge list — and the trend groups beside it —
again.

**Connect is no longer greyed out after changing a bridge address while connected.** The
button was only enabled while disconnected, so a changed or added OPC Bridge address could
not be applied without disconnecting first. Connect now compares the connection targets —
each row's address and display store — with the live connection: adding, removing or
changing one re-enables it, and reverting the edit greys it out again. Renaming a bridge
is a save-only change and leaves Connect alone. Clicking Connect tears the live sessions
down and rebuilds them from the rows.

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
