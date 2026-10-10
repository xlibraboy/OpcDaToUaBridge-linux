namespace OpcBridge.Logic;

/// <summary>
/// The Guide dialog's markdown: how the plant logic is authored here and evaluated by the
/// bridge. Kept as a const so the guide ships inside the assembly and a test can pin the
/// vocabulary it documents to the editor's real option lists.
/// </summary>
internal static class HelpContent
{
    public const string Markdown = """
# Logic (Interlocks, Permissives & Sequences)

This app authors the plant logic the **OpcBridge Logic** phone app renders on the floor: named
blocks of conditions built from mapped tags, evaluated by the bridge against live values. The
evaluation runs server-side, so neither this app nor the phone carries plant logic — both show
exactly what the bridge derived.

---

# Block kinds

- **Interlock / permissive** — a flat list of conditions. The block is **ready** only when every
  *blocks*-severity condition and every OR group is satisfied. A false condition (or a group
  whose members are all false) makes it **blocked**, with that condition's sentence — or the
  group's members — as the reason; a missing or bad-quality value makes it **unknown** — never
  silently ready.
- **Sequence** — ordered steps. A step is **done** when its conditions are true and, when a
  *completion handshake* tag is set, that tag is true too. The first step that is not done is the
  **current** one — its failing condition (or unsatisfied handshake) is the block's reason — and
  every later step stays **pending** until the sequence reaches it.

---

# Conditions (the simple form)

Each condition is one tag comparison, with the operator sentence the phone shows and a "what to
do when not true" next-step line:

- `ON (NO contact)` / `OFF (NC contact)` — the reading the contact must show: a **normally open**
  contact should read 1, a **normally closed** one should read 0. Boolean tags and Byte 0/1 tags
  both work (ON is any non-zero value).
- `>` / `<` / `=` — numeric comparison against the value you enter.
- **OR group** — conditions that share the same label are an **OR gate**: the group is satisfied
  as soon as one member is true. Everything else is the block's **AND** — every group and every
  ungrouped condition must be satisfied for the block to be ready.
- **hold** — a **TON on-delay timer**: the contact counts as satisfied only after its input has
  held true that many seconds. The bridge re-times a hold when the signal drops, and a restart
  re-times every running hold.
- **warn only** conditions are shown and tracked, but never block the block and never carry an
  OR gate or a timer.

The phone renders every contact as **should 1 / actual 0** — the reading the interlock needs
against the live value — and a contact whose input is right but whose timer is still running shows
the timer's progress.

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
rows are the block's roots — all of them must be true (the block's AND). The bridge evaluates the
network with three-valued logic (a value that has not arrived reads *unknown*, never silently
true), and the phone lists every element, indented, with its own live state. A **warn** element is
reported but inert: it neither satisfies nor blocks its parent.

`Convert to IEC network` never changes what the block does: the flat form *is* a network — one
contact per condition, an OR gate per group, a TON around a held contact — and the bridge expands
it exactly the same way when it evaluates.

---

# Interlock group

A block can carry an **interlock group** (e.g. `Primary Arm`). The phone collects blocks that share
it under one collapsible heading, so a machine's up/down interlocks read together — name the
children after it (`Primary Arm Up`, `Primary Arm Down`) and give them the same group. At most 40
characters; blank blocks stay ungrouped. The Blocks dialog groups the list the same way.

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
allow writes and the caller's role must permit them. The editor only offers writeable tags for
actions.

---

# Live state

The bridge evaluates every block on the value stream and serves the result at
`GET /api/logic/state`, pushing changes to connected apps over the `logic` hub message. This app
shows the same chips the phone does — green ready, red blocked, grey unknown/disabled — with the
first blocking reason and the next-step text. The chips repaint once a second while a block is
open, without rebuilding the inputs, so typing is never disturbed.

While the bridge is unreachable the app keeps the last known blocks and chips on screen, flags the
connection in the header, and disables editing until the bridge is back.
""";
}
