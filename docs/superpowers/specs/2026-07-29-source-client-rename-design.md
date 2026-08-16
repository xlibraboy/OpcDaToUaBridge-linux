# Phase 2: Source Client + ItemId Rename

- **Date:** 2026-07-29
- **Branch:** `feature/modif-bridge-app`
- **Status:** Implementing
- **Depends on:** Phase 1 typed source config (`9684b97`)

## Goal

Stop calling the universal source port “DA” and stop calling every source tag a `DaItemId`.

## Scope

| Rename | To |
|--------|-----|
| `IDaClient` | `ISourceClient` |
| `DaClientFactory` | `SourceClientFactory` |
| `TagMapping.DaItemId` | `ItemId` |
| `TagMapping.ProviderDaItemId` | `ProviderItemId` |
| `BridgeValue.DaItemId` | `ItemId` |
| Method params `daItemId` on `ISourceClient` | `itemId` |

## Wire / disk compatibility

`System.Text.Json` file stores use PascalCase property names today (`"DaItemId"`).

Keep disk + external JSON stable:

```csharp
[JsonPropertyName("DaItemId")]
public string ItemId { get; set; }

[JsonPropertyName("ProviderDaItemId")]
public string? ProviderItemId { get; set; }

// BridgeValue positional record — use [property: JsonPropertyName("DaItemId")]
```

API anonymous objects that currently emit `daItemId = x.DaItemId` become `daItemId = x.ItemId` (same JSON key).

## Out of scope

- Renaming `DaRuntimeSettings`, `DaLinkStore`, `OpcDaClient`, packages, UI labels “OPC DA”
- `ISubscribableSourceClient` (already generic)
- Dirty-only reconfigure (Phase 3)

## Success

1. No remaining `IDaClient` / `DaClientFactory` symbols.
2. No remaining `DaItemId` / `ProviderDaItemId` **C# property/field** names (JSON attributes may still say DaItemId).
3. Build + LoadTest settings/factory/API focused suite green.
