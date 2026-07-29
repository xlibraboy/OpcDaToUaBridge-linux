# Phase 2 Source Client Rename — Plan

**Goal:** `IDaClient`→`ISourceClient`, `DaClientFactory`→`SourceClientFactory`, `DaItemId`→`ItemId` with JSON wire compatibility.

### Task 1: Core renames

- Rename interface file + type; update implementors (OpcDa, Ua, Melsec, Mock)
- Rename factory type + DI registration
- Rename `TagMapping` / `BridgeValue` properties + JsonPropertyName
- Mechanical replace call sites in src + tests
- Build + focused tests
- Commit
