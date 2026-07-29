# Phase 4: Multi-source UI labels

- **Date:** 2026-07-29
- **Branch:** `feature/modif-bridge-app`
- **Status:** Implemented

## Goal

Stop implying every source is OPC DA in shared UI surfaces (faceplate, mappings list, access rights, diagrams).

## Changes

| Surface | Before | After |
|---------|--------|-------|
| Faceplate item field | DA Address | Item ID |
| Access rights | DA → UA / DA ↔ UA / UA → DA | Source → UA / Source ↔ UA / UA → Source |
| Mappings title | DA → OPC UA Mappings | Source → OPC UA Mappings |
| Table / sort column | DA Item ID | Item ID |
| Manual add placeholder | DA Item ID… | Item ID (DA / UA / Melsec examples) |
| Poll rate hint | DA group interval… | source poll/publish interval… |
| Architecture label | DA → UA (aggregated) | Source → UA (aggregated) |

## Unchanged (correctly DA-specific)

- OPC DA connection form (ProgID, Host, Discover)
- OPC UA / Drivers wizards and type-specific panes
- Element ids like `fpDaItemId` (JS hooks; dual-read already supports `itemId`)
