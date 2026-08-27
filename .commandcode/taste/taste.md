# Taste

## Workflow
- Prefers that completed changes be committed and pushed to the remote (e.g. `origin/main`) rather than left unstaged; expects the assistant to run git commit/push when work is finished. Confidence: 0.6
- Prefers validating features end-to-end against realistic simulated infrastructure — e.g., multiple dockerized OPC UA sim servers exposing real tag nodes — rather than mocked or in-app data alone, when the goal is to reproduce and confirm a reported behavior. Confidence: 0.55

## Dashboard UX
- Prefers at-a-glance, consolidated visualizations for verifying live data flows over scanning raw tables row by row — e.g., explicitly requested an "Interlink Flow" panel in Live Values showing provider→consumer value pairs side by side with status pills, "instead [of] see one by one on Live Values". When a feature spans multiple related items, surface their relationships (paired values, status, direction) in one view. Confidence: 0.55
- When splitting related views apart, prefers sub-tab switchers within the existing page (e.g., "Live Values" | "Interlink Flow" sub-tabs on ops/values) over adding new sidebar/top-level navigation entries — keeps related functionality grouped under one route. Chose the sub-tab option when offered alternatives (own sidebar tab, moving into the Interlinks page). Confidence: 0.5
