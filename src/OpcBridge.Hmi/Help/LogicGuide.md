# Logic (Interlocks, Permissives & Sequences)

The Logic page authors the plant logic the **phone app** renders on the floor: named blocks of
conditions built from mapped tags, evaluated by the bridge against live values. The evaluation runs
server-side, so neither the runtime nor the phone carries plant logic — both show exactly what the
bridge derived. The page is opened from **View ▸ Logic** (Ctrl+4).

---

# Block kinds

- **Interlock / permissive** — a flat list of conditions. The block is **ready** only when every
  *blocks*-severity condition and every OR group is satisfied. A false condition (or a group whose
  members are all false) makes it **blocked**, with that condition's sentence — or the group's
  members — as the reason; a missing or bad-quality value makes it **unknown** — never silently
  ready.
- **Sequence** — ordered steps. A step is **done** when its conditions are true and, when a
  *completion handshake* tag is set, that tag is true too. The first step that is not done is the
  **current** one — its failing condition (or unsatisfied handshake) is the block's reason — and
  every later step stays **pending** until the sequence reaches it.

---

# Conditions (the simple form)

Each condition is one tag comparison, with the operator sentence the phone shows and a "what to do
when not true" next-step line:

- `ON (NO contact)` / `OFF (NC contact)` — the reading the contact must show: a **normally open**
  contact should read 1, a **normally closed** one should read 0. Boolean tags and Byte 0/1 tags
  both work (ON is any non-zero value).
- `>` / `<` / `=` — numeric comparison against the value you enter.
- **OR group** — conditions that share the same label are an **OR gate**: the group is satisfied as
  soon as one member is true. Everything else is the block's **AND** — every group and every
  ungrouped condition must be satisfied for the block to be ready.
- **hold** — a **TON on-delay timer**: the contact counts as satisfied only after its input has
  held true that many seconds. The bridge re-times a hold when the signal drops, and a restart
  re-times every running hold.
- **warn only** conditions are shown and tracked, but never block the block and never carry an OR
  gate or a timer.

The live chip on each row reads `1` / `0` / `no data` for a bit and keeps the live value for a
numeric comparison — a level above 50 % stays a number, never a bit. The phone renders every
contact as **should 1 / actual 0** — the reading the interlock needs against the live value — and a
contact whose input is right but whose timer is still running shows the timer's progress.

---

# Arranging rows

Every row in the editor — a condition, a network element, a step, an action — carries its own order
buttons beside `✕`: `+↑` and `+↓` insert a fresh row directly above or below it, so a list is built
where you are reading instead of only appended at its end, and `↑` / `↓` move the row one place.
In the network a row moves **with the rows nested under it** — a gate keeps its inputs — and insert
places the new row beside it at the same nesting level (use `→` afterwards to make it an input).
The buttons grey out at the ends of a level; nothing moves past its siblings.

---

# Network (IEC 61131-3)

A block can be authored as a **network** instead of the flat list — `Convert to IEC network`
rewrites the current conditions into one, and the editor then works on **element rows**. The
vocabulary is IEC 61131-3, so any logic flow can be drawn:

- **Contacts** — `NO` / `NC` (`on` / `off`), or a comparison (`>`, `<`, `=`).
- **Gates** — `AND`, `OR`, `XOR`, `NOT`, nested to any depth.
- **Function blocks** — `TON` / `TOF` / `TP` timers (preset `PT`, elapsed reported), `CTU` / `CTD`
  counters (preset `PV`, count reported), `SR` / `RS` latches, and `R_TRIG` / `F_TRIG` edge
  triggers.

Rows nest by indentation: a row's inputs are the rows under it at a deeper level, and the outermost
rows are the block's roots — all of them must be true (the block's AND). Use `→` to indent a row
under the one above and `←` to move it back out. The bridge evaluates the network with
three-valued logic (a value that has not arrived reads *unknown*, never silently true), and the
phone lists every element, indented, with its own live state. An element may be **saved before it
is wired**: a gate, function block or coil with nothing under it yet reads *unknown* with
`(not wired yet)` as its cause (`no data for OR (not wired yet)`), so a network is built row by row
and a half-built one is never reported as satisfied. A **warn** element is reported but
inert: it neither satisfies nor blocks its parent. A network holds at most 256 elements and nests
at most 16 levels; a deeper or larger save is refused with the reason, never truncated.

`Convert to IEC network` never changes what the block does: the flat form *is* a network — one
contact per condition, an OR gate per group, a TON around a held contact — and the bridge expands
it exactly the same way when it evaluates.

---

# Internal relays (M coils)

A network can also use **internal relays** — the PLC's `M` bits, `M01`, `M02`, … — to split a big
interlock into readable pieces instead of one deep tree:

- **Coil** (`Coil — drive internal relay (IN, M)`) — takes one input and **drives** the
  relay it names with that input, every evaluation. Type a new name in the `RELAY` field or pick
  one of the relays the other blocks already drive. The coil's own output is what it wrote, so a
  coil can sit inside a gate like any other element.
- **Relay contact** — a `Contact` with its `RELAY` field filled: `ON` reads the relay as 1, `OFF`
  reads it as 0, and it is **not** a tag (the tag picker disappears while a relay is typed). Its
  live chip and `should 1 · actual 0` line read the same as a tag contact's.
- **Wiring across blocks** — relays are shared by the whole bridge, so a relay a coil drives in one
  block feeds **any** gate or function block — `AND`, `OR`, `XOR`, `NOT`, a timer's `IN`, a
  counter's `CU`, a latch's set — in **any other block**, at any nesting position, mixed freely
  with tag contacts in the same gate. Split a chain of interlocks the PLC way: block 1 computes the
  intermediate result and drives `M01`, block 2 reads `M01`, block 3 reads it as a timer input, and
  so on.

How it runs, in PLC scan terms: the bridge evaluates blocks in their order and each network from
the top row down, so a coil's write is visible to everything evaluated **after** it — keep each
coil above the rows (and the blocks) that read it, or a reader sees the previous pass's value for
one pass. A relay **no coil has driven yet reads unknown**, never a silent 0 — a mistyped `M001`
shows up as an unknown reading instead of quietly blocking. A **disabled** block evaluates for
display but its coils **do not** drive anything. Relay values live in the bridge process like the
timers: a restart forgets them, and the first pass re-derives every relay from its coil.

Relay names are uppercase and start with a letter (`M01`, `StartPermit`, max 16 characters);
`m01` is stored as `M01`. Keep one coil per relay — if several blocks drive the same relay, the
last one in scan order wins.

---

# Area and interlock group

A block carries two headings:

- **AREA** (e.g. `DRY END`) — the plant's main section, and the scope its tags are shared by: every
  block of the area reads one **area tags** list (the Tags tab), so a signal is assigned once per
  area and re-used by all of its interlocks, whatever their group. **`New block` asks for the area
  first**, so the block is authored inside its area from the start and its rows pick from the area's
  tags.
- **INTERLOCK GROUP** (e.g. `Primary Arm`) — a display heading only: the front list, the Blocks
  window and the phone collect blocks that share it under one heading, so a machine's up/down
  interlocks read together. Name the children after it (`Primary Arm Up`, `Primary Arm Down`) and
  give them the same group.

At most 40 characters each. A block without an area still works — its named tags resolve through
the name it stores, else its interlock group (the legacy fallback) — and giving it an area moves its
tags onto the area's list.

---

# Tags

A block can carry up to 8 short **tags** — comma-separated labels typed in the editor's Tags box
(e.g. `Line 1, Safety`), each at most 24 characters. The store trims them, drops blanks and
case-insensitive duplicates, and refuses a save that exceeds the limits. The phone shows them as
chips on the block's card and filters the list by them; its search box matches the block name, the
description or any tag.

---

# Area tags (per area)

A block does not name the bridge tag in every row: it reads the **named tags of its area**, and
those are assigned in one place — the **Tags** tab next to the editor. The tab holds the area's
**area tags** — the named tags every block of that area reads — so a signal is assigned **once per
area** instead of once per condition:

- Type the **area's name** (`DRY END`, `WET END`, …) in the editor's `AREA` field — or in the Tags
  tab's own box, which is the same value. Every block of the area shares one list of tags: assign a
  tag here and every one of those blocks follows. The tab says how many loaded blocks carry the
  area.
- **This is where a tag gets its source.** Each row of the tab is one signal: the **NAME** a block's
  rows carry (`START_PB`, `FAULT`, …), its **SOURCE** (`DRY END`'s own sources are listed, e.g. the
  simulation PLC) and the **TAG** on that source. Name it, leave it unassigned, and assign the
  bridge tag later — a block authored against an unassigned entry reads **unknown** with the entry
  named in the reason (`no data for START_PB (not assigned)`), never a silent false. A name the
  area's tags do not hold reads `… (not in DRY END)`.
- **`+ New tag`** creates an entry with a name you choose; pick its source and tag on the same row.
  The **TAG** column also marks a bound tag the source does not offer for writing
  (`read-only tag — an action cannot write it.`), so an action button that could never write is
  spotted where it is assigned rather than on the phone.
- In the editor, every row — a condition, a network contact, a step's completion handshake, an
  action — carries one **AREA TAG** picker listing the area's entries. Pick a name there; the row
  stores the name and nothing else, so the pair lives on the entry and moving it moves every block
  of the area at once. `✕` clears the pick and leaves the row unassigned until you pick another name.
- **A block authored before areas existed still works.** Its rows carry a direct source/tag pair;
  the moment such a block is opened with an area (or given one), those pairs **join the area's tags**
  — one entry each, named after the tag's display name (`LINE_01_START_PERMIT`, …), which the row
  then references — and **Save** stores the move. The editor says so (`Moved 1 tag into "DRYEND" —
  Save to store them.`). Rename an entry on the tab if the automatic name reads badly. A pair from a
  block with no area at all has no list to join: the row shows it as a `Direct tag: …` line instead,
  keeps it through the save, and points you at the Tags tab — assign the area and it moves in.
- **Save** stores the area's tags and the block together (whichever changed, and the tab's
  `used by` line tells you who shares them). **`Delete area tags`** removes the area's list; the
  bridge refuses while any block still reads it, naming how many.
- The **LIVE** column shows the bound tag's current value (`1` / `0` for a bit, the number
  otherwise, `bad` for bad quality), so an assignment can be checked without leaving the tab.

Two blocks of the same area that read the same signal read the same entry: changing the binding
moves both at once.

---

# Actions

A block can carry action buttons (label, tag, value, confirm). The phone renders them as operator
buttons; a press writes the value through the bridge's normal write path, so the target tag must
allow writes and the caller's role must permit them. This page authors the buttons only — the write
stays on the phone — and the button writes one of the area's tags: pick it in its `AREA TAG` field,
and the Tags tab marks an entry whose tag the source does not offer for writing.

---

# Live state

The bridge evaluates every block on the value stream and serves the result at
`GET /api/logic/state`, pushing changes to connected apps over the `logic` hub message. The Result
tab shows the same chips the phone does — green ready, red blocked, grey unknown/disabled — with
the first blocking reason and the next-step text. The chips refresh once a second (and instantly on
a bridge push) while the page is open, without rebuilding the rows, so typing is never disturbed.

While the bridge is unreachable the page keeps the last known blocks and chips on screen, flags the
connection in the header, and disables editing until the bridge is back.

---

# Result tab

The **Result** tab next to the editor shows what the bridge currently evaluates from the saved
configuration — the same result the phone presents, refreshed once a second while the tab is open.
It writes nothing: actions stay on the phone.

- The list on the left collects every block under its **area**, then its **interlock group**
  heading, with the live state badge (`ready` / `blocked` / `unknown` / `disabled`), the first
  failing reason and a sequence's progress (`2/3 steps · Open valve`).
- Selecting a block shows its evaluated network on the right: the **mark** (`✓` / `✗` / `—`), the
  contact kind (`NO` / `NC` / `>` / `<` / `=` / `AND` / `OR` / `TON` …), the sentence and its
  reading — `should 1 · actual 0` — plus a **hold** timer's or counter's progress
  (`holding 1.5 s of 3 s`, `count 2 of 5`) and the next-step text while a contact is unsatisfied.
- **Sequences** list their steps with their state — `Done` / `Current` / `Pending` / `No data` —
  the step's reason, and the step's own elements.
