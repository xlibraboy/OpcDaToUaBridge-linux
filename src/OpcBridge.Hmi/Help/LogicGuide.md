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
phone lists every element, indented, with its own live state. A **warn** element is reported but
inert: it neither satisfies nor blocks its parent. A network holds at most 256 elements and nests
at most 16 levels; a deeper or larger save is refused with the reason, never truncated.

`Convert to IEC network` never changes what the block does: the flat form *is* a network — one
contact per condition, an OR gate per group, a TON around a held contact — and the bridge expands
it exactly the same way when it evaluates.

---

# Internal relays (M coils)

A network can also use **internal relays** — the PLC's `M` bits, `M01`, `M02`, … — to split a big
interlock into readable pieces instead of one deep tree:

- **Coil** (`Coil — drive internal relay (IN, M)`) — takes exactly one input and **drives** the
  relay it names with that input, every evaluation. Type a new name in the `RELAY` field or pick
  one of the relays the other blocks already drive. The coil's own output is what it wrote, so a
  coil can sit inside a gate like any other element.
- **Relay contact** — a `Contact` with its `RELAY` field filled: `ON` reads the relay as 1, `OFF`
  reads it as 0, and it is **not** a tag (the tag pickers disappear while a relay is typed). Its
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

# Interlock group

A block can carry an **interlock group** (e.g. `Primary Arm`). The phone — and the Blocks window —
collects blocks that share it under one heading, so a machine's up/down interlocks read together:
name the children after it (`Primary Arm Up`, `Primary Arm Down`) and give them the same group. At
most 40 characters; blank blocks stay ungrouped.

---

# Tags

A block can carry up to 8 short **tags** — comma-separated labels typed in the editor's Tags box
(e.g. `Line 1, Safety`), each at most 24 characters. The store trims them, drops blanks and
case-insensitive duplicates, and refuses a save that exceeds the limits. The phone shows them as
chips on the block's card and filters the list by them; its search box matches the block name, the
description or any tag.

---

# Actions

A block can carry action buttons (label, tag, value, confirm). The phone renders them as operator
buttons; a press writes the value through the bridge's normal write path, so the target tag must
allow writes and the caller's role must permit them. This page authors the buttons only — the write
stays on the phone — and offers writeable tags for them.

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

- The list on the left collects every block under its **interlock group** heading, with the live
  state badge (`ready` / `blocked` / `unknown` / `disabled`), the first failing reason and a
  sequence's progress (`2/3 steps · Open valve`).
- Selecting a block shows its evaluated network on the right: the **mark** (`✓` / `✗` / `—`), the
  contact kind (`NO` / `NC` / `>` / `<` / `=` / `AND` / `OR` / `TON` …), the sentence and its
  reading — `should 1 · actual 0` — plus a **hold** timer's or counter's progress
  (`holding 1.5 s of 3 s`, `count 2 of 5`) and the next-step text while a contact is unsatisfied.
- **Sequences** list their steps with their state — `Done` / `Current` / `Pending` / `No data` —
  the step's reason, and the step's own elements.
