#!/usr/bin/env bash
# Seed the "Primary Arm" interlock example into a running OpcBridge instance, authored in the
# IEC 61131-3 network form the Logic tab edits: contacts (NO / NC), gates (AND / OR / XOR /
# NOT), a TON on-delay timer, a CTU counter and a warn-only element.
#
# It reads the simulator's self-toggling Boolean status tags, so the example is alive as soon
# as it lands — the phone shows every element with its live state and the reading it needs
# ("should 1 · actual 0"). Run scripts/demo/seed-industrial-scada.sh first (or map the same
# status tags by hand) so the ua-sim source exists.
#
# The script is idempotent: a block of the same name keeps its id, so re-running it updates the
# example instead of duplicating it.
#
# Usage:
#   scripts/demo/seed-logic-primary-arm.sh [API_BASE_URL]
#
# Example:
#   scripts/demo/seed-logic-primary-arm.sh http://127.0.0.1:8080

set -euo pipefail

API="${1:-http://127.0.0.1:8080}"
export LOGIC_SOURCE_ID="${LOGIC_SOURCE_ID:-ua-sim}"

if ! curl -sf -m 5 "$API/api/logic" > /dev/null; then
  echo "no bridge answered at $API" >&2
  exit 1
fi

python3 - "$API" <<'PY'
import json
import os
import sys
import urllib.request
import uuid

api = sys.argv[1]
source = os.environ.get("LOGIC_SOURCE_ID", "ua-sim")

new_id = lambda: str(uuid.uuid4())


def contact(text, item, op="on", severity="block"):
    """A contact element: NO (op on), NC (op off) or a comparison."""
    return {
        "id": new_id(),
        "kind": "contact",
        "text": text,
        "sourceId": source,
        "itemId": item,
        "op": op,
        "severity": severity,
    }


def element(kind, text, inputs, **extra):
    """A gate or a standard function block over the given inputs."""
    node = {"id": new_id(), "kind": kind, "text": text, "inputs": inputs}
    node.update(extra)
    return node


def tag(name):
    return "ns=2;s=Status/" + name


def block(name, group, description, elements, tags):
    return {
        "id": new_id(),
        "name": name,
        "group": group,
        "description": description,
        "kind": "interlock",
        "enabled": True,
        "tags": tags,
        "elements": elements,
        "conditions": [],
        "steps": [],
        "actions": [],
    }


# Primary Arm Up — the AND is the block itself (the roots below); the OR gate is an any-of, the
# TON turns "control is in Auto" into "has held for 3 s", and the alarm is monitored only.
arm_up = block(
    "Primary Arm Up",
    "Primary Arm",
    "Raises the primary arm: permit, clear retract line, a running hydraulic source, auto control held 3 s.",
    [
        contact("Line 01 start permit must be given", tag("Line01.Permit"), "on"),
        contact("Retract line must be clear", tag("Valve02.Open"), "off"),
        element(
            "or",
            "Start permissive",
            [
                contact("Pump 01 must be running", tag("Pump01.Run")),
                contact("Compressor 01 must be running", tag("Compressor01.Run")),
            ],
        ),
        element(
            "ton",
            "Control must be in Auto",
            [contact("Control must be in Auto", tag("Mode01.Auto"))],
            ptMs=3000,
        ),
        contact("High level alarm must be normal", tag("Alarm01.HighLevel"), "on", "warn"),
    ],
    ["Primary Arm", "Line 1"],
)

# Primary Arm Down — NOT as a gate, XOR as a cross-check of the two run feedbacks, and a warn
# counter that reports its count without blocking the move.
arm_down = block(
    "Primary Arm Down",
    "Primary Arm",
    "Lowers the primary arm: valve 01 open, extend line clear (NOT), exactly one run feedback, start attempts monitored.",
    [
        contact("Valve 01 must be open", tag("Valve01.Open"), "on"),
        element("not", "Extend line must be clear", [contact("Extend line must be clear", tag("Valve02.Open"))]),
        element(
            "xor",
            "Only one run feedback at a time",
            [
                contact("Motor 02 must be running", tag("Motor02.Run")),
                contact("Pump 01 must be running", tag("Pump01.Run")),
            ],
        ),
        element(
            "ctu",
            "Start attempts",
            [contact("Motor 02 must be running", tag("Motor02.Run"))],
            pv=2,
            severity="warn",
        ),
    ],
    ["Primary Arm", "Line 1"],
)


def existing_ids():
    with urllib.request.urlopen(api + "/api/logic", timeout=10) as response:
        blocks = json.load(response).get("blocks", [])
    return {b.get("name"): b.get("id") for b in blocks}


ids = existing_ids()
print("Seeding the Primary Arm example into " + api + " (source " + source + ")")
for seeded in (arm_up, arm_down):
    if ids.get(seeded["name"]):
        seeded["id"] = ids[seeded["name"]]
    request = urllib.request.Request(
        api + "/api/logic/blocks",
        data=json.dumps({"block": seeded}).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        body = json.load(response)
    saved = body["block"]
    print("  " + saved["name"] + " · " + str(len(saved.get("elements", []))) + " roots · v" + str(body.get("version")))
PY
