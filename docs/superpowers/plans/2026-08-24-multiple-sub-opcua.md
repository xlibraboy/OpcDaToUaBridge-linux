# Multiple Named Subscriptions per OPC UA Source — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** OPC UA inbound sources support multiple named subscriptions, each with its own update rate; tags are explicitly assigned to a subscription or ride the source default.

**Architecture:** Each rate bucket is a real OPC UA `Subscription` (one publishing queue per bucket, per OPC UA Part 4 §5.14) managed inside the existing `OpcUaSourceClient` session. A pure planner partitions desired mappings into buckets; reconcile diffs each bucket independently. Definitions persist in `sources.json`, tag assignment in `TagMapping.Subscription` / `mappings.json`; dashboard gets a Subscriptions tab and Maps dropdown.

**Tech Stack:** .NET 8, OPCFoundation.NetStandard.Opc.Ua 1.5.378.145, xUnit, ASP.NET Core minimal API + single-page dashboard (`DashboardPage.cs`).

**Spec:** `docs/superpowers/specs/2026-08-24-multiple-sub-opcua-design.md`

## Global Constraints

- **Worktree:** all work in `/mnt/c/Users/xlibr/Documents/OpcDaToUaBridge/.worktrees/feature/multiple-sub-opcua`. Always use absolute paths with `edit`/`write`.
- **Build/test command:** `export DOTNET_CLI_TELEMETRY_OPTOUT=1; export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo` (local SDK verified working). Filter single tests with `--filter "FullyQualifiedName~<TestName>"`.
- **Zero-warning build bar.** New code must not add warnings.
- **Default-bucket behavior is frozen:** unassigned tags keep today's exact algorithm — item sampling = per-tag `PollRateMs` > 0 else source `UpdateRateMs`; publishing = fastest desired sampling clamped ≥ 100 ms; source-rate change still recreates the session (`SourceConnectionEquals` keeps `UpdateRateMs`). `SourceConnectionEqualsTests` must stay green untouched.
- **Reconcile stays serialized** through the existing `SemaphoreSlim` (known stale-monitored-item failure mode).
- Named-bucket rates clamp to ≥ 100 ms; names trimmed, unique case-insensitive per source, 1–64 chars; soft cap 16 named subs/source.
- Conventional commits (`feat:`, `fix:`, `test:`, `docs:`).
- Batch sizes preserved: `ReadChunkSize=500`, `MonitoredItemBatchSize=750`, `NotificationFlushSize=1000`.

---

### Task 1: Core types — `UaSubscriptionSettings` + `TagMapping.Subscription`

**Files:**
- Create: `src/OpcBridge.Core/UaSubscriptionSettings.cs`
- Modify: `src/OpcBridge.Core/TagMapping.cs`
- Test: `tests/OpcBridge.LoadTest/CoreUaSubscriptionTests.cs`

**Interfaces:**
- Produces: `public sealed record UaSubscriptionSettings(string Name, int UpdateRateMs);` and `TagMapping.Subscription : string` (default `""`, JSON name `"subscription"`). Every later task consumes these.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/OpcBridge.LoadTest/CoreUaSubscriptionTests.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class CoreUaSubscriptionTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    [Fact]
    public void TagMapping_Subscription_DefaultsEmpty_AndRoundTrips()
    {
        TagMapping mapping = new() { SourceId = "ua-a", ItemId = "ns=2;s=Tag1" };
        Assert.Equal(string.Empty, mapping.Subscription);

        mapping.Subscription = "Fast";
        string json = JsonSerializer.Serialize(mapping, SerializerOptions);
        Assert.Contains("\"subscription\"", json);

        TagMapping? parsed = JsonSerializer.Deserialize<TagMapping>(json, SerializerOptions);
        Assert.NotNull(parsed);
        Assert.Equal("Fast", parsed!.Subscription);
    }

    [Fact]
    public void TagMapping_WithoutSubscriptionField_LoadsAsEmpty()
    {
        // Old payloads (pre-feature) must load unchanged.
        string legacyJson = "{\"SourceId\":\"ua-a\",\"ItemId\":\"ns=2;s=Tag1\"}";
        TagMapping? parsed = JsonSerializer.Deserialize<TagMapping>(legacyJson, SerializerOptions);
        Assert.NotNull(parsed);
        Assert.Equal(string.Empty, parsed!.Subscription);
    }

    [Fact]
    public void UaSubscriptionSettings_RecordEquality_IsCaseSensitiveOnName()
    {
        var a = new UaSubscriptionSettings("Fast", 250);
        var b = new UaSubscriptionSettings("Fast", 250);
        var c = new UaSubscriptionSettings("fast", 250);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~CoreUaSubscriptionTests"`
Expected: FAIL — `'TagMapping' does not contain a definition for 'Subscription'` (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `src/OpcBridge.Core/UaSubscriptionSettings.cs`:

```csharp
namespace OpcBridge.Core;

/// <summary>
/// One named OPC UA subscription definition on an OpcUa-type source: a display name
/// and the update rate (ms) used as both the subscription's PublishingInterval and
/// its member MonitoredItems' SamplingInterval. Pure data type — validation/clamping
/// happens at the settings/API layer (100 ms floor, see spec §4).
/// </summary>
public sealed record UaSubscriptionSettings(string Name, int UpdateRateMs);
```

Modify `src/OpcBridge.Core/TagMapping.cs` — add after the `InfluxEnabled` property (inside the class):

```csharp
    /// <summary>
    /// OPC UA sources only: name of the source-defined named subscription this tag rides on.
    /// Empty string = the source's default bucket (source UpdateRateMs semantics, unchanged).
    /// Matched case-insensitively against the source's definitions; unknown names group into
    /// the default bucket at runtime (spec §4).
    /// </summary>
    [JsonPropertyName("subscription")]
    public string Subscription { get; set; } = string.Empty;
```

Note: `TagMapping.cs` already imports `System.Text.Json.Serialization`.

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~CoreUaSubscriptionTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.Core/UaSubscriptionSettings.cs src/OpcBridge.Core/TagMapping.cs tests/OpcBridge.LoadTest/CoreUaSubscriptionTests.cs
git commit -m "feat(core): UaSubscriptionSettings record and TagMapping.Subscription field"
```

---

### Task 2: Bucket planner — `UaSubscriptionPlan.GroupByBucket`

**Files:**
- Create: `src/OpcBridge.Ua/UaSubscriptionPlan.cs`
- Test: `tests/OpcBridge.LoadTest/UaSubscriptionPlanTests.cs`

**Interfaces:**
- Consumes: `TagMapping.Subscription`, `UaSubscriptionSettings` (Task 1); filter parity with `UaSamplingRates.BuildDesiredSampling` (existing, `src/OpcBridge.Ua/UaSamplingRates.cs`).
- Produces:
```csharp
public static class UaSubscriptionPlan
{
    public const string DefaultBucketKey = "";
    public static Dictionary<string, Dictionary<string, int>> GroupByBucket(
        IReadOnlyList<TagMapping> desiredMappings,
        IReadOnlyList<UaSubscriptionSettings>? subscriptions,
        int defaultSamplingMs);
}
```
Returns bucketKey → ordered nodeId → samplingInterval(ms). Task 3 consumes this exact shape.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/OpcBridge.LoadTest/UaSubscriptionPlanTests.cs
using OpcBridge.Core;
using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class UaSubscriptionPlanTests
{
    private static readonly UaSubscriptionSettings[] Subs =
    {
        new("Fast", 250),
        new("Slow", 5000)
    };

    private static TagMapping Map(
        string itemId,
        string subscription = "",
        int pollRateMs = 0,
        bool enabled = true,
        string mode = TagMode.Source,
        string accessRights = TagAccessRights.Read)
        => new()
        {
            SourceId = "ua-a",
            ItemId = itemId,
            Subscription = subscription,
            PollRateMs = pollRateMs,
            Enabled = enabled,
            Mode = mode,
            AccessRights = accessRights
        };

    [Fact]
    public void AssignedTags_GoToTheirNamedBucket_AtBucketRate()
    {
        var mappings = new List<TagMapping>
        {
            Map("ns=2;s=A", subscription: "FAST"),   // case-insensitive
            Map("ns=2;s=B", subscription: "slow")
        };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, Subs, defaultSamplingMs: 1000);

        Assert.Equal(250, plan["Fast"]["ns=2;s=A"]);
        Assert.Equal(5000, plan["Slow"]["ns=2;s=B"]);
        Assert.False(plan.ContainsKey(UaSubscriptionPlan.DefaultBucketKey));
    }

    [Fact]
    public void UnassignedAndUnknownTags_FallBackToDefaultBucket_WithLegacyRates()
    {
        var mappings = new List<TagMapping>
        {
            Map("ns=2;s=D1"),                          // default rate
            Map("ns=2;s=D2", pollRateMs: 400),         // per-tag override still wins in default
            Map("ns=2;s=X", subscription: "Ghost")     // unknown sub -> default
        };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, Subs, defaultSamplingMs: 1000);

        Assert.Single(plan);
        Dictionary<string, int> defaultBucket = plan[UaSubscriptionPlan.DefaultBucketKey];
        Assert.Equal(1000, defaultBucket["ns=2;s=D1"]);
        Assert.Equal(400, defaultBucket["ns=2;s=D2"]);
        Assert.Equal(1000, defaultBucket["ns=2;s=X"]);
    }

    [Fact]
    public void Filters_ParityWithBuildDesiredSampling()
    {
        var mappings = new List<TagMapping>
        {
            Map("ns=2;s=Off", enabled: false),
            Map("ns=2;s=Man", mode: TagMode.Manual),
            Map("   "),                                     // empty itemId
            Map("ns=2;s=W", accessRights: TagAccessRights.Write), // write-only not source-read
            Map("ns=2;s=Ok", subscription: "Fast")
        };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, Subs, defaultSamplingMs: 1000);

        Assert.Single(plan[UaSubscriptionPlan.DefaultBucketKey]); // only the empty-id one is excluded... 
        // Correction: the empty ItemId row is filtered out entirely, so default bucket holds ZERO entries
        // and is absent from the plan. The named bucket holds exactly the Ok tag.
        Assert.False(plan.ContainsKey(UaSubscriptionPlan.DefaultBucketKey));
        Assert.Equal(new[] { "ns=2;s=Ok" }, plan["Fast"].Keys.ToArray());
    }

    [Fact]
    public void NullSubscriptions_AllTagsGoToDefault_LegacyShape()
    {
        var mappings = new List<TagMapping> { Map("ns=2;s=A", pollRateMs: 300), Map("ns=2;s=B") };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, null, defaultSamplingMs: 1000);

        Assert.Single(plan);
        Assert.Equal(300, plan[""][ "ns=2;s=A"]);
        Assert.Equal(1000, plan[""]["ns=2;s=B"]);
    }

    [Fact]
    public void DuplicateNodeIds_FirstWins_PerBucket()
    {
        var mappings = new List<TagMapping>
        {
            Map("ns=2;s=A", subscription: "Fast"),
            Map(" ns=2;s=A ", subscription: "Fast", pollRateMs: 999) // same node after trim
        };

        Dictionary<string, Dictionary<string, int>> plan =
            UaSubscriptionPlan.GroupByBucket(mappings, Subs, defaultSamplingMs: 1000);

        Assert.Equal(250, plan["Fast"]["ns=2;s=A"]); // first wins; named bucket ignores PollRateMs
    }
}
```

(Note: in `Filters_ParityWithBuildDesiredSampling`, fix the stray mid-test comment — remove the first misleading `Assert.Single(plan[...])` line and its comment; keep the two final asserts.)

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~UaSubscriptionPlanTests"`
Expected: FAIL — compile error, `UaSubscriptionPlan` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/OpcBridge.Ua/UaSubscriptionPlan.cs
using OpcBridge.Core;

namespace OpcBridge.Ua;

/// <summary>
/// Partitions the desired mapped-tag set into per-subscription buckets (spec §5).
/// Pure function — no session/SDK types — so bucket grouping is unit-testable in isolation.
/// Filter parity with <see cref="UaSamplingRates.BuildDesiredSampling"/>: enabled, non-Manual,
/// non-empty NodeId, non-Write-only tags only.
/// </summary>
public static class UaSubscriptionPlan
{
    /// <summary>Bucket key for unassigned tags (the implicit source-default subscription).</summary>
    public const string DefaultBucketKey = "";

    /// <summary>
    /// Bucket key → (nodeId → sampling interval ms), preserving desired order within a bucket.
    /// Named buckets sample every member at the bucket's configured rate (clamped ≥ 100 ms);
    /// the default bucket keeps legacy per-tag override semantics.
    /// </summary>
    public static Dictionary<string, Dictionary<string, int>> GroupByBucket(
        IReadOnlyList<TagMapping> desiredMappings,
        IReadOnlyList<UaSubscriptionSettings>? subscriptions,
        int defaultSamplingMs)
    {
        Dictionary<string, Dictionary<string, int>> plan = new(StringComparer.Ordinal);
        if (desiredMappings is null)
        {
            return plan;
        }

        // Case-insensitive lookup: normalized name → canonical bucket key.
        Dictionary<string, string> bucketByKey = new(StringComparer.OrdinalIgnoreCase);
        if (subscriptions is not null)
        {
            foreach (UaSubscriptionSettings sub in subscriptions)
            {
                string key = NormalizeName(sub.Name);
                if (key.Length == 0 || bucketByKey.ContainsKey(key))
                {
                    continue;
                }

                bucketByKey[key] = key;
            }
        }

        for (int i = 0; i < desiredMappings.Count; i++)
        {
            TagMapping mapping = desiredMappings[i];
            if (!mapping.Enabled
                || string.Equals(mapping.Mode, TagMode.Manual, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(mapping.ItemId)
                || string.Equals(mapping.AccessRights, TagAccessRights.Write, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string nodeId = mapping.ItemId.Trim();
            string requested = NormalizeName(mapping.Subscription);
            string bucketKey = requested.Length > 0 && bucketByKey.ContainsKey(requested)
                ? bucketByKey[requested]
                : DefaultBucketKey;

            int sampling;
            if (bucketKey == DefaultBucketKey)
            {
                sampling = mapping.PollRateMs > 0 ? mapping.PollRateMs : defaultSamplingMs;
                if (sampling < 0)
                {
                    sampling = defaultSamplingMs;
                }
            }
            else
            {
                int configured = subscriptions!.First(s => NormalizeName(s.Name) == bucketKey).UpdateRateMs;
                sampling = Math.Max(100, configured);
            }

            if (!plan.TryGetValue(bucketKey, out Dictionary<string, int>? items))
            {
                items = new Dictionary<string, int>(StringComparer.Ordinal);
                plan[bucketKey] = items;
            }

            // First wins; Diff keys are unique per bucket.
            if (!items.ContainsKey(nodeId))
            {
                items[nodeId] = sampling;
            }
        }

        return plan;
    }

    /// <summary>Trimmed bucket name; empty string when null/whitespace (the default bucket).</summary>
    public static string NormalizeName(string? name) => name?.Trim() ?? string.Empty;
}
```

Then clean up the test file per the note in Step 1 (remove the contradictory assert + comment block in `Filters_ParityWithBuildDesiredSampling`).

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~UaSubscriptionPlanTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.Ua/UaSubscriptionPlan.cs tests/OpcBridge.LoadTest/UaSubscriptionPlanTests.cs
git commit -m "feat(ua): pure bucket planner for multi-subscription grouping"
```

---

### Task 3: `OpcUaSourceClient` multi-bucket reconcile + live status

**Files:**
- Modify: `src/OpcBridge.Ua/OpcUaSourceClientOptions.cs`
- Create: `src/OpcBridge.Ua/UaSubscriptionStatus.cs`
- Modify: `src/OpcBridge.Ua/OpcUaSourceClient.cs` (fields ~lines 27–41, `ReconcileMonitoredItemsCoreAsync` lines 541–760, `EnsureSubscriptionAsync` 853–931, `TearDownSubscriptionAsync` 981–1055, `OnMonitoredItemNotification` 1095–1111)

**Interfaces:**
- Consumes: `UaSubscriptionPlan.GroupByBucket` (Task 2), `MonitoredItemReconcile.Diff` (existing), options plumbing added here.
- Produces: `OpcUaSourceClientOptions.Subscriptions : IReadOnlyList<UaSubscriptionSettings>`; `public IReadOnlyList<UaSubscriptionStatus> GetSubscriptionsStatus();` where `public sealed record UaSubscriptionStatus(string BucketKey, int RequestedPublishingIntervalMs, double ActualPublishingIntervalMs, int ItemCount, bool Created);` — Task 8 consumes via BridgeWorker.

This is the largest task. No new unit tests (client internals require a live session — covered by the full regression suite plus rig verification in Task 10); every behavioral rule it must preserve is asserted by existing tests.

- [ ] **Step 1: Options + status record**

Add to `OpcUaSourceClientOptions` (after `UseSubscriptions`):

```csharp
    /// <summary>Named subscription definitions (name + rate ms). Empty = legacy single-subscription behavior.</summary>
    public IReadOnlyList<OpcBridge.Core.UaSubscriptionSettings> Subscriptions { get; set; } =
        Array.Empty<OpcBridge.Core.UaSubscriptionSettings>();
```

Create `src/OpcBridge.Ua/UaSubscriptionStatus.cs`:

```csharp
namespace OpcBridge.Ua;

/// <summary>Live snapshot of one subscription bucket on a connected UA source (spec §6).</summary>
public sealed record UaSubscriptionStatus(
    string BucketKey,
    int RequestedPublishingIntervalMs,
    double ActualPublishingIntervalMs,
    int ItemCount,
    bool Created);
```

- [ ] **Step 2: Rework client state fields**

In `OpcUaSourceClient.cs`, replace the single `private Subscription? subscription_;` field with:

```csharp
    private sealed class SubscriptionBucket
    {
        public string Key { get; init; } = UaSubscriptionPlan.DefaultBucketKey;
        public int PublishingIntervalMs { get; set; }
        public Subscription? Subscription { get; set; }
        public HashSet<string> ItemIds { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, SubscriptionBucket> buckets_ =
        new(StringComparer.OrdinalIgnoreCase);
```

Keep `monitored_items_` and `node_id_by_display_` exactly as-is — they remain the global nodeId index used by teardown, reconnect adoption, failed-item retry, and notification resolution. Bucket membership lives in `SubscriptionBucket.ItemIds`; a nodeId is a member of exactly one bucket.

Every site that currently reads/writes `subscription_` under `lock (gate_)` gains bucket-aware handling per the steps below. In `ConnectAsync` and `OnReconnectComplete`, replace `subscription_ = null; monitored_items_.Clear(); ...` blocks with `ResetBucketBookkeepingLocked();` :

```csharp
    private void ResetBucketBookkeepingLocked()
    {
        // Called under gate_: drop all bucket state; the SDK-side subscriptions die with the old session.
        buckets_.Clear();
        monitored_items_.Clear();
        node_id_by_display_.Clear();
        subscriptions_active_ = false;
    }
```

- [ ] **Step 3: Rewrite the reconcile core**

Replace `ReconcileMonitoredItemsCoreAsync` body with the bucket loop (structure preserved: same try/catch fallback contract, same batching constants):

```csharp
    private async Task ReconcileMonitoredItemsCoreAsync(
        IReadOnlyList<TagMapping> desiredMappings,
        CancellationToken cancellationToken)
    {
        if (!options_.UseSubscriptions)
        {
            await TearDownAllBucketsAsync(keepSession: true).ConfigureAwait(false);
            return;
        }

        Session? session;
        lock (gate_)
        {
            session = session_;
            if (session is null || !session.Connected)
            {
                subscriptions_active_ = false;
                return;
            }
        }

        try
        {
            int defaultSampling = Math.Max(100, options_.UpdateRateMs);
            Dictionary<string, Dictionary<string, int>> plan =
                UaSubscriptionPlan.GroupByBucket(desiredMappings, options_.Subscriptions, defaultSampling);

            lock (gate_)
            {
                if (failed_items_.Count > 0)
                {
                    IEnumerable<string> desiredIds = plan.Values.SelectMany(b => b.Keys);
                    failed_items_.RemoveWhere(id => !desiredIds.Contains(id));
                    if (failed_items_.Count == 0)
                    {
                        failed_item_retry_timer_?.Dispose();
                        failed_item_retry_timer_ = null;
                    }
                }
            }

            int totalDesired = plan.Values.Sum(b => b.Count);

            // Buckets that are no longer defined/desired go away entirely.
            List<string> staleBuckets;
            lock (gate_)
            {
                staleBuckets = buckets_.Keys.Where(k => !plan.ContainsKey(k)).ToList();
            }
            foreach (string staleKey in staleBuckets)
            {
                await RemoveBucketAsync(session, staleKey, keepSession: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (KeyValuePair<string, Dictionary<string, int>> bucketPlan in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReconcileBucketAsync(
                    session,
                    bucketPlan.Key,
                    bucketPlan.Value,
                    defaultSampling,
                    cancellationToken).ConfigureAwait(false);
            }

            lock (gate_)
            {
                bool allCreated = buckets_.Values.All(b => b.Subscription is { Created: true });
                subscriptions_active_ = totalDesired > 0 && allCreated && monitored_items_.Count > 0;
            }

            logger_.LogInformation(
                "OPC UA source {SourceId} subscription reconcile: desired={Desired} active={Active} buckets=[{Buckets}]",
                options_.SourceId,
                totalDesired,
                monitored_items_.Count,
                string.Join(", ", plan.Select(kv =>
                    $"{(kv.Key.Length == 0 ? "default" : kv.Key)}:{kv.Value.Count}@{kv.Value.Values.DefaultIfEmpty(defaultSampling).Min()}ms")));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger_.LogWarning(
                ex,
                "OPC UA source {SourceId} subscription reconcile failed; falling back to poll",
                options_.SourceId);
            await TearDownAllBucketsAsync(keepSession: true).ConfigureAwait(false);
        }
    }
```

Add the per-bucket reconcile (same add/remove/sampling-align logic as the current single-subscription version, scoped to one bucket):

```csharp
    private async Task ReconcileBucketAsync(
        Session session,
        string bucketKey,
        Dictionary<string, int> desiredSampling,
        int defaultSampling,
        CancellationToken cancellationToken)
    {
        SubscriptionBucket bucket;
        lock (gate_)
        {
            if (!buckets_.TryGetValue(bucketKey, out SubscriptionBucket? found))
            {
                found = new SubscriptionBucket { Key = bucketKey };
                buckets_[bucketKey] = found;
            }
            bucket = found;
        }

        Subscription subscription = await EnsureBucketSubscriptionAsync(
                session, bucket, desiredSampling.Values.DefaultIfEmpty(defaultSampling).Min(),
                cancellationToken)
            .ConfigureAwait(false);

        string[] activeIds;
        lock (gate_)
        {
            activeIds = bucket.ItemIds.ToArray();
        }

        (IReadOnlyList<string> toAdd, IReadOnlyList<string> toRemove) =
            MonitoredItemReconcile.Diff(desiredSampling.Keys, activeIds);

        for (int offset = 0; offset < toRemove.Count; offset += MonitoredItemBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(MonitoredItemBatchSize, toRemove.Count - offset);
            List<MonitoredItem> batch = new(count);
            lock (gate_)
            {
                for (int i = 0; i < count; i++)
                {
                    string nodeId = toRemove[offset + i];
                    if (monitored_items_.Remove(nodeId, out MonitoredItem? item))
                    {
                        node_id_by_display_.Remove(item.DisplayName);
                        item.Notification -= OnMonitoredItemNotification;
                        batch.Add(item);
                    }
                    bucket.ItemIds.Remove(nodeId);
                }
            }

            if (batch.Count == 0)
            {
                continue;
            }

            subscription.RemoveItems(batch);
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        for (int offset = 0; offset < toAdd.Count; offset += MonitoredItemBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(MonitoredItemBatchSize, toAdd.Count - offset);
            List<MonitoredItem> batch = new(count);

            for (int i = 0; i < count; i++)
            {
                string nodeIdString = toAdd[offset + i];
                if (!NodeId.TryParse(nodeIdString, out NodeId? nodeId) || nodeId is null)
                {
                    logger_.LogDebug(
                        "Skipping invalid NodeId for UA subscription on {SourceId}: {NodeId}",
                        options_.SourceId, nodeIdString);
                    continue;
                }

                int sampling = desiredSampling.TryGetValue(nodeIdString, out int s)
                    ? s
                    : defaultSampling;

#pragma warning disable CS0618
                MonitoredItem item = new()
                {
                    StartNodeId = nodeId,
                    AttributeId = Attributes.Value,
                    DisplayName = nodeIdString,
                    SamplingInterval = sampling,
                    QueueSize = 1,
                    DiscardOldest = true,
                    MonitoringMode = MonitoringMode.Reporting,
                    Handle = nodeIdString
                };
#pragma warning restore CS0618
                item.Notification += OnMonitoredItemNotification;
                batch.Add(item);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            subscription.AddItems(batch);
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);

            lock (gate_)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    MonitoredItem item = batch[i];
                    string key = item.Handle as string ?? item.DisplayName;
                    ServiceResult? createError = item.Status.Error;
                    if (!item.Status.Created
                        || (createError is not null && StatusCode.IsBad(createError.StatusCode)))
                    {
                        logger_.LogDebug(
                            "MonitoredItem create failed for {SourceId} {NodeId}: created={Created} status={Status}",
                            options_.SourceId, key, item.Status.Created, createError?.StatusCode);
                        item.Notification -= OnMonitoredItemNotification;
                        subscription.RemoveItem(item);
                        NoteItemCreateFailure(key);
                        continue;
                    }

                    monitored_items_[key] = item;
                    node_id_by_display_[item.DisplayName] = key;
                    bucket.ItemIds.Add(key);
                    failed_items_.Remove(key);
                }
            }

            if (subscription.ChangesPending)
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Rate-only changes on surviving members (per-tag overrides in the default bucket).
        bool samplingChanged = false;
        lock (gate_)
        {
            foreach (KeyValuePair<string, int> kv in desiredSampling)
            {
                if (monitored_items_.TryGetValue(kv.Key, out MonitoredItem? item))
                {
                    int desired = kv.Value > 0 ? kv.Value : defaultSampling;
                    if (item.SamplingInterval != desired)
                    {
                        item.SamplingInterval = desired;
                        samplingChanged = true;
                    }
                }
            }
        }

        if (samplingChanged)
        {
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
```

- [ ] **Step 4: Replace Ensure/TearDown with bucket versions**

Replace `EnsureSubscriptionAsync` with:

```csharp
    private async Task<Subscription> EnsureBucketSubscriptionAsync(
        Session session,
        SubscriptionBucket bucket,
        int publishingIntervalMs,
        CancellationToken cancellationToken)
    {
        int publishing = Math.Max(100, publishingIntervalMs);

        lock (gate_)
        {
            Subscription? current = bucket.Subscription;
            if (current is not null
                && ReferenceEquals(current.Session, session)
                && current.Created
                && bucket.PublishingIntervalMs == publishing)
            {
                return current;
            }
        }

        // Servers don't reliably apply a publishing-interval change to a live subscription,
        // so recreate just this bucket — the caller's reconcile re-adds its monitored items.
        await RemoveBucketAsync(session, bucket.Key, keepSession: true, cancellationToken)
            .ConfigureAwait(false);

#pragma warning disable CS0618
        Subscription subscription = new()
        {
            DisplayName = bucket.Key.Length == 0
                ? $"OpcBridge_{options_.SourceId}"
                : $"OpcBridge_{options_.SourceId}_{bucket.Key}",
            PublishingEnabled = true,
            PublishingInterval = publishing,
            KeepAliveCount = 10,
            LifetimeCount = 1000,
            MaxNotificationsPerPublish = 0,
            TimestampsToReturn = TimestampsToReturn.Both,
            Priority = 0
        };
#pragma warning restore CS0618

        subscription.FastDataChangeCallback = OnFastDataChange;

        if (!session.AddSubscription(subscription))
        {
            try { subscription.FastDataChangeCallback = null; } catch { }
            subscription.Dispose();
            throw new InvalidOperationException(
                $"Failed to add OPC UA subscription '{bucket.Key}' for source '{options_.SourceId}'.");
        }

        try
        {
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
            if (!subscription.Created)
            {
                throw new InvalidOperationException(
                    $"OPC UA subscription create failed for source '{options_.SourceId}' bucket '{bucket.Key}'.");
            }
        }
        catch
        {
            await DiscardUnownedSubscriptionAsync(session, subscription).ConfigureAwait(false);
            throw;
        }

        lock (gate_)
        {
            bucket.Subscription = subscription;
            bucket.PublishingIntervalMs = publishing;
        }

        return subscription;
    }
```

Replace `TearDownSubscriptionAsync(bool keepSession)` with two methods (keep the same inner delete/remove/dispose sequence per subscription):

```csharp
    private async Task TearDownAllBucketsAsync(bool keepSession)
    {
        List<string> keys;
        lock (gate_)
        {
            keys = buckets_.Keys.ToList();
        }

        foreach (string key in keys)
        {
            await RemoveBucketAsync(GetSessionIfAny(), key, keepSession, CancellationToken.None)
                .ConfigureAwait(false);
        }

        List<MonitoredItem> orphans;
        lock (gate_)
        {
            orphans = monitored_items_.Values.ToList();
            monitored_items_.Clear();
            node_id_by_display_.Clear();
            subscriptions_active_ = false;
        }

        for (int i = 0; i < orphans.Count; i++)
        {
            try { orphans[i].Notification -= OnMonitoredItemNotification; } catch { }
        }
    }

    private Session? GetSessionIfAny()
    {
        lock (gate_)
        {
            return session_;
        }
    }

    private async Task RemoveBucketAsync(Session? session, string bucketKey, bool keepSession, CancellationToken cancellationToken)
    {
        SubscriptionBucket bucket;
        lock (gate_)
        {
            if (!buckets_.Remove(bucketKey, out SubscriptionBucket? found))
            {
                return;
            }
            bucket = found;

            foreach (string nodeId in bucket.ItemIds)
            {
                if (monitored_items_.Remove(nodeId, out MonitoredItem? item))
                {
                    node_id_by_display_.Remove(item.DisplayName);
                    try { item.Notification -= OnMonitoredItemNotification; } catch { }
                }
            }
            bucket.ItemIds.Clear();

            if (monitored_items_.Count == 0)
            {
                subscriptions_active_ = false;
            }
        }

        Subscription? subscription = bucket.Subscription;
        bucket.Subscription = null;
        if (subscription is null)
        {
            return;
        }

        try { subscription.FastDataChangeCallback = null; } catch { }

        try
        {
            if (subscription.Session is not null && subscription.Created)
            {
                await subscription.DeleteAsync(silent: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger_.LogDebug(ex, "Error deleting OPC UA subscription '{Bucket}' for source {SourceId}",
                bucketKey, options_.SourceId);
        }

        try
        {
            if (session is not null && subscription.Session is ISession s && keepSession)
            {
                await s.RemoveSubscriptionAsync(subscription, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore remove races on dispose
        }

        try { subscription.Dispose(); } catch { }
    }
```

Also: in `DisposeAsync`, replace `await TearDownSubscriptionAsync(keepSession: false)...` with `await TearDownAllBucketsAsync(keepSession: false).ConfigureAwait(false);` (before `reconcile_gate_.Dispose()`).

- [ ] **Step 5: Status surface + notification fallback**

Add after `GetFailedItemIds()`:

```csharp
    /// <summary>Live per-bucket snapshot for the dashboard (requested vs server-revised interval).</summary>
    public IReadOnlyList<UaSubscriptionStatus> GetSubscriptionsStatus()
    {
        List<UaSubscriptionStatus> statuses = new();
        lock (gate_)
        {
            foreach (SubscriptionBucket bucket in buckets_.Values.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
            {
                Subscription? sub = bucket.Subscription;
                statuses.Add(new UaSubscriptionStatus(
                    bucket.Key,
                    bucket.PublishingIntervalMs,
                    sub?.CurrentPublishingInterval ?? 0,
                    bucket.ItemIds.Count,
                    sub?.Created ?? false));
            }
        }
        return statuses;
    }
```

In `OnMonitoredItemNotification`, replace `if (subscription_?.FastDataChangeCallback is not null)` with checking any bucket:

```csharp
        lock (gate_)
        {
            if (buckets_.Values.Any(b => b.Subscription?.FastDataChangeCallback is not null))
            {
                return;
            }
        }
```

`OnFastDataChange` needs no change — it receives the owning `Subscription` instance and resolves items by client handle on that instance.

- [ ] **Step 6: Build and run the full suite**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet build OpcBridge.sln --nologo -v q && dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo`
Expected: build 0 errors 0 new warnings; full suite green (planner + core tests from Tasks 1–2 pass; all pre-existing tests pass — especially `SourceConnectionEqualsTests`).

- [ ] **Step 7: Commit**

```bash
git add src/OpcBridge.Ua/OpcUaSourceClientOptions.cs src/OpcBridge.Ua/UaSubscriptionStatus.cs src/OpcBridge.Ua/OpcUaSourceClient.cs
git commit -m "feat(ua): multiple named subscription buckets in OpcUaSourceClient"
```

---

### Task 4: Settings registry — definitions on UA sources

**Files:**
- Modify: `src/OpcBridge.App/DaRuntimeSettings.cs` (`OpcUaSourceOptions` line ~510, `OpcUaSourceOptionsDto` line ~725, `SourceConfigMigration` line ~775, `DaSourceRuntimeSettings` compat getters line ~550+, `ToUaOptions` line ~634, new registry methods near `SetSourceUpdateRate` line ~120)
- Test: `tests/OpcBridge.LoadTest/UaSourceSubscriptionsTests.cs`

**Interfaces:**
- Consumes: `UaSubscriptionSettings` (Task 1).
- Produces:
  - `OpcUaSourceOptions` gains optional param `IReadOnlyList<UaSubscriptionSettings>? Subscriptions = null`;
  - `DaSourceRuntimeSettings.UaSubscriptions : IReadOnlyList<UaSubscriptionSettings>` (compat getter, empty default);
  - `DaSourceRuntimeSettings.UaSubscriptionsEqual(DaSourceRuntimeSettings other) : bool` (order-insensitive, case-insensitive names, exact rates);
  - `DaRuntimeSettingsSnapshot UpsertUaSubscription(string sourceId, string name, int updateRateMs)` — throws `ArgumentException` on validation failure;
  - `DaRuntimeSettingsSnapshot RemoveUaSubscription(string sourceId, string name)` — throws when missing/not UA;
  - `ToUaOptions` populates `OpcUaSourceClientOptions.Subscriptions`.
  
  Task 8 consumes all of these.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/OpcBridge.LoadTest/UaSourceSubscriptionsTests.cs
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class UaSourceSubscriptionsTests : IDisposable
{
    private readonly string _binDir = AppContext.BaseDirectory;
    private readonly string _sourcesPath;

    public UaSourceSubscriptionsTests()
    {
        _sourcesPath = Path.Combine(_binDir, "sources.json");
        if (File.Exists(_sourcesPath)) File.Delete(_sourcesPath);
    }

    public void Dispose()
    {
        if (File.Exists(_sourcesPath)) File.Delete(_sourcesPath);
    }

    private static DaRuntimeSettings CreateSettings() => new(
        Options.Create(new DaClientOptions { ProgId = "P", Host = "h", UpdateRateMs = 1000 }));

    private static DaSourceRuntimeSettings UaSource(string id = "ua-a") => SourceConfigMigration.Normalize(
        new DaSourceRuntimeSettings(id, id, SourceTypes.OpcUa, 1000, true, 10000,
            OpcUa: new OpcUaSourceOptions("opc.tcp://127.0.0.1:49321/x", "None", "None", null, null, 60000, 5000)),
        1000);

    [Fact]
    public void Upsert_AddsValidatesClampsAndDedupes()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));

        DaRuntimeSettingsSnapshot s1 = settings.UpsertUaSubscription("ua-a", "  Fast ", 250);
        Assert.Single(s1.GetSource("ua-a")!.UaSubscriptions);
        Assert.Equal("Fast", s1.GetSource("ua-a")!.UaSubscriptions[0].Name);
        Assert.Equal(250, s1.GetSource("ua-a")!.UaSubscriptions[0].UpdateRateMs);

        // Rate below floor clamps to 100; re-upsert same name (case-insensitive) updates in place.
        DaRuntimeSettingsSnapshot s2 = settings.UpsertUaSubscription("UA-A", "fAst", 10);
        DaSourceRuntimeSettings src = s2.GetSource("ua-a")!;
        Assert.Single(src.UaSubscriptions);
        Assert.Equal(100, src.UaSubscriptions[0].UpdateRateMs);
    }

    [Fact]
    public void Upsert_RejectsBadInput()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));

        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("ua-a", "   ", 250));   // blank
        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("ua-a", new string('x', 65), 250)); // too long
        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("nope", "Fast", 250)); // unknown source

        DaRuntimeSettingsSnapshot capped = settings.UpsertUaSubscription("ua-a", $"S{new string('y', 60)}", 250);
        _ = capped;
        for (int i = 0; i < 15; i++)
        {
            settings.UpsertUaSubscription("ua-a", $"sub{i}", 500 + i);
        }
        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("ua-a", "overflow", 250)); // cap 16
    }

    [Fact]
    public void Upsert_RejectsNonUaSource()
    {
        DaRuntimeSettings settings = CreateSettings();
        DaSourceRuntimeSettings da = SourceConfigMigration.Normalize(
            new DaSourceRuntimeSettings("opc-vm", "opc-vm", SourceTypes.OpcDa, 1000, true, 10000,
            OpcDa: new OpcDaSourceOptions("Matrikon.OPC.Simulation.1", "localhost", null, null, null)), 1000);
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { da }, 0));

        Assert.Throws<ArgumentException>(() => settings.UpsertUaSubscription("opc-vm", "Fast", 250));
    }

    [Fact]
    public void Remove_DeletesByName_CaseInsensitive_AndThrowsWhenMissing()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));
        settings.UpsertUaSubscription("ua-a", "Fast", 250);

        DaRuntimeSettingsSnapshot after = settings.RemoveUaSubscription("ua-a", "fAST");
        Assert.Empty(after.GetSource("ua-a")!.UaSubscriptions);
        Assert.Throws<ArgumentException>(() => settings.RemoveUaSubscription("ua-a", "Fast"));
    }

    [Fact]
    public void SubscriptionsEqual_OrderInsensitive_NameCaseInsensitive()
    {
        DaSourceRuntimeSettings a = UaSource() with
        {
            OpcUa = new OpcUaSourceOptions("opc.tcp://x", "None", "None", null, null, 60000, 5000,
                Subscriptions: new List<UaSubscriptionSettings> { new("Fast", 250), new("Slow", 5000) })
        };
        DaSourceRuntimeSettings b = a with
        {
            OpcUa = new OpcUaSourceOptions("opc.tcp://x", "None", "None", null, null, 60000, 5000,
                Subscriptions: new List<UaSubscriptionSettings> { new("slow", 5000), new("FAST", 250) })
        };
        DaSourceRuntimeSettings c = a with
        {
            OpcUa = new OpcUaSourceOptions("opc.tcp://x", "None", "None", null, null, 60000, 5000,
                Subscriptions: new List<UaSubscriptionSettings> { new("Fast", 300), new("Slow", 5000) })
        };

        Assert.True(a.UaSubscriptionsEqual(b));
        Assert.False(a.UaSubscriptionsEqual(c));
    }

    [Fact]
    public void ToUaOptions_CarriesSubscriptions()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));
        settings.UpsertUaSubscription("ua-a", "Fast", 250);

        DaRuntimeSettingsSnapshot snap = settings.GetSnapshot();
        OpcUaSourceClientOptions opts = snap.GetSource("ua-a")!.ToUaOptions(snap);

        Assert.Single(opts.Subscriptions);
        Assert.Equal("Fast", opts.Subscriptions[0].Name);
    }

    [Fact]
    public void SourcesJson_RoundTripsSubscriptions()
    {
        DaRuntimeSettings settings = CreateSettings();
        settings.RestoreFromSnapshot(new DaRuntimeSettingsSnapshot(1000, true, new List<DaSourceRuntimeSettings> { UaSource() }, 0));
        settings.UpsertUaSubscription("ua-a", "Fast", 250);

        DaRuntimeSettings reloaded = CreateSettings(); // loads sources.json from disk
        DaRuntimeSettingsSnapshot snap = reloaded.GetSnapshot();
        Assert.Single(snap.GetSource("ua-a")!.UaSubscriptions);
        Assert.Equal(250, snap.GetSource("ua-a")!.UaSubscriptions[0].UpdateRateMs);
    }
}
```

Check `DaClientOptions` construction matches its actual shape (read `src/OpcBridge.Core/BridgeOptions.cs` / `OpcBridge.Da/DaClientOptions.cs` first and adjust the helper if ProgId/Host differ); check whether other test classes already handle the shared `sources.json` in bin (search tests for `sources.json`) and follow that pattern if so.

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~UaSourceSubscriptionsTests"`
Expected: FAIL — compile errors (`UpsertUaSubscription`, `UaSubscriptions`, etc. missing).

- [ ] **Step 3: Implement**

1. `OpcUaSourceOptions` (line ~510): add optional trailing param:
```csharp
public sealed record OpcUaSourceOptions(
    string EndpointUrl,
    string SecurityMode,
    string SecurityPolicy,
    string? Username,
    string? Password,
    int SessionTimeoutMs,
    int ReconnectDelayMs,
    int WatchdogTimeoutMs = 60000,
    IReadOnlyList<UaSubscriptionSettings>? Subscriptions = null);
```

2. `DaSourceRuntimeSettings` compat getters (near line 594, after `WatchdogTimeoutMs`):
```csharp
    /// <summary>Named UA subscription definitions; empty for non-UA sources or legacy configs.</summary>
    public IReadOnlyList<UaSubscriptionSettings> UaSubscriptions
        => OpcUa?.Subscriptions ?? Array.Empty<UaSubscriptionSettings>();

    /// <summary>Order-insensitive comparison of named-subscription definitions (case-insensitive names).</summary>
    public bool UaSubscriptionsEqual(DaSourceRuntimeSettings other)
    {
        IReadOnlyList<UaSubscriptionSettings> left = UaSubscriptions;
        IReadOnlyList<UaSubscriptionSettings> right = other.UaSubscriptions;
        if (left.Count != right.Count)
        {
            return false;
        }

        Dictionary<string, int> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (UaSubscriptionSettings s in left)
        {
            byName[s.Name.Trim()] = s.UpdateRateMs;
        }

        foreach (UaSubscriptionSettings s in right)
        {
            string key = s.Name.Trim();
            if (!byName.TryGetValue(key, out int rate) || rate != s.UpdateRateMs)
            {
                return false;
            }
            byName.Remove(key);
        }

        return byName.Count == 0;
    }
```

3. `OpcUaSourceOptionsDto`: add
```csharp
    public List<UaSubscriptionDto>? Subscriptions { get; set; }
```
and next to the DTO classes add:
```csharp
public sealed class UaSubscriptionDto
{
    public string? Name { get; set; }
    public int UpdateRateMs { get; set; }
}
```

4. `SourceConfigMigration`: in the `ToDto` mapping for UA options add `Subscriptions = source.UaSubscriptions.Count == 0 ? null : source.UaSubscriptions.Select(s => new UaSubscriptionDto { Name = s.Name, UpdateRateMs = s.UpdateRateMs }).ToList(),` and in `FromDto` map back `Subscriptions = dto.Subscriptions is { Count: > 0 } ? dto.Subscriptions.Select(d => new UaSubscriptionSettings(d.Name ?? string.Empty, d.UpdateRateMs)).ToList() : null`. Also extend `Normalize(source, defaultUpdateRate)` so a rebuilt UA source runs through `NormalizeUaSubscriptions` below (locate where it reconstructs `OpcUaSourceOptions` and thread the normalized list).

Add normalization helper to `SourceConfigMigration`:
```csharp
    public const int MaxUaSubscriptionsPerSource = 16;

    /// <summary>Trim names, dedupe case-insensitively (first wins), clamp rates to >= 100 ms, drop blanks.</summary>
    public static IReadOnlyList<UaSubscriptionSettings> NormalizeUaSubscriptions(
        IEnumerable<UaSubscriptionSettings>? subscriptions)
    {
        if (subscriptions is null)
        {
            return Array.Empty<UaSubscriptionSettings>();
        }

        Dictionary<string, UaSubscriptionSettings> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (UaSubscriptionSettings sub in subscriptions)
        {
            string name = sub.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            int rate = Math.Max(100, sub.UpdateRateMs);
            if (!result.ContainsKey(name))
            {
                result[name] = new UaSubscriptionSettings(name, rate);
            }
        }

        return result.Values.ToList();
    }
```

5. Registry methods on `DaRuntimeSettings` (next to `SetSourceUpdateRate`):
```csharp
    /// <summary>Add/update a named UA subscription on an OpcUa-type source. Throws ArgumentException on invalid input.</summary>
    public DaRuntimeSettingsSnapshot UpsertUaSubscription(string sourceId, string name, int updateRateMs)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 64)
        {
            throw new ArgumentException("Subscription name must be 1-64 characters.", nameof(name));
        }

        lock (sync_)
        {
            DaSourceRuntimeSettings? source = FindSourceLocked(sourceId)
                ?? throw new ArgumentException($"Source '{sourceId}' does not exist.", nameof(sourceId));
            if (!string.Equals(source.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Source '{sourceId}' is not an OPC UA source; subscriptions apply to OPC UA sources only.",
                    nameof(sourceId));
            }

            List<UaSubscriptionSettings> subs = SourceConfigMigration
                .NormalizeUaSubscriptions(source.UaSubscriptions)
                .ToList();
            int clamped = Math.Max(100, updateRateMs);
            int index = subs.FindIndex(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            UaSubscriptionSettings updated = new(trimmed, clamped);
            if (index >= 0)
            {
                subs[index] = updated;
            }
            else
            {
                if (subs.Count >= SourceConfigMigration.MaxUaSubscriptionsPerSource)
                {
                    throw new ArgumentException(
                        $"Source '{sourceId}' already has the maximum of {SourceConfigMigration.MaxUaSubscriptionsPerSource} named subscriptions.");
                }
                subs.Add(updated);
            }

            return UpsertSourceLocked(source with
            {
                OpcUa = (source.OpcUa ?? new OpcUaSourceOptions(string.Empty, "None", "None", null, null, 60000, 5000))
                    with { Subscriptions = subs }
            });
        }
    }

    /// <summary>Remove a named UA subscription. Throws ArgumentException when the source/sub doesn't exist.</summary>
    public DaRuntimeSettingsSnapshot RemoveUaSubscription(string sourceId, string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        lock (sync_)
        {
            DaSourceRuntimeSettings? source = FindSourceLocked(sourceId)
                ?? throw new ArgumentException($"Source '{sourceId}' does not exist.", nameof(sourceId));

            List<UaSubscriptionSettings> subs = SourceConfigMigration
                .NormalizeUaSubscriptions(source.UaSubscriptions)
                .ToList();
            int index = subs.FindIndex(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new ArgumentException($"Subscription '{trimmed}' does not exist on source '{sourceId}'.");
            }

            subs.RemoveAt(index);
            return UpsertSourceLocked(source with
            {
                OpcUa = (source.OpcUa ?? new OpcUaSourceOptions(string.Empty, "None", "None", null, null, 60000, 5000))
                    with { Subscriptions = subs }
            });
        }
    }
```
Adapt to actual internals: if there is no `FindSourceLocked`/`UpsertSourceLocked` helper, inline the equivalents from `SetSourceUpdateRate`'s body (it shows the established locked find+replace pattern — mirror it exactly, including `Persist()` and version bump).

6. `ToUaOptions` (line ~650): add `Subscriptions = UaSubscriptions` to the initializer.

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~UaSourceSubscriptionsTests"`
Expected: PASS (7 tests). Then run the FULL suite once (registry touches shared state):
`dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo` — expected green.

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/DaRuntimeSettings.cs tests/OpcBridge.LoadTest/UaSourceSubscriptionsTests.cs
git commit -m "feat(app): named UA subscription definitions in source registry"
```

---

### Task 5: MappingStore — normalize + auto-reassign

**Files:**
- Modify: `src/OpcBridge.App/MappingStore.cs` (normalization path at ~line 312 where `NormalizeAccessRights` is called; new method after `RemoveSource` line ~159)
- Test: `tests/OpcBridge.LoadTest/MappingSubscriptionTests.cs`

**Interfaces:**
- Consumes: `TagMapping.Subscription` (Task 1).
- Produces: stored values always trimmed (empty ok); `public int ReassignSubscription(string sourceId, string subscriptionName)` returning count moved to default. Task 8 consumes `ReassignSubscription`.

- [ ] **Step 1: Write the failing test**

Follow the store-construction pattern used by `MappingGroupTests.cs` / `AccessRightsNormalizationTests.cs` (read one first and copy its setup; they construct `MappingStore` against a temp file path). Tests:

```csharp
[Fact] Store_TrimsSubscription_OnAddAndUpdate();
// add {"subscription":" Fast "} -> snapshot value == "Fast"

[Fact] Store_RoundTripsSubscription_ToDisk();
// add + new MappingStore(same path) -> value survives; unknown-name value also survives verbatim

[Fact] ReassignSubscription_MovesOnlyMatchingSource_CaseInsensitive_ReturnsCount();
// two sources, tags on "Fast"/"fast"/unassigned; ReassignSubscription("ua-a","FAST") -> returns 2,
// both now "", other source untouched

[Fact] ReassignSubscription_NoOp_ForBlankOrUnknownName();
// returns 0, nothing changed
```

Write them concretely using the copied setup pattern before implementing.

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~MappingSubscriptionTests"`
Expected: FAIL — `ReassignSubscription` missing / trim not applied.

- [ ] **Step 3: Implement**

In the same normalization method that calls `NormalizeAccessRights` (line ~312), add:
```csharp
        tag.Subscription = (tag.Subscription ?? string.Empty).Trim();
```
After `RemoveSource`, add:
```csharp
    /// <summary>
    /// Move every mapping of one source off a named subscription back onto the source default
    /// (empty Subscription). Used when a named subscription is deleted (spec §6). Returns count moved.
    /// </summary>
    public int ReassignSubscription(string sourceId, string subscriptionName)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(subscriptionName))
        {
            return 0;
        }

        string target = subscriptionName.Trim();
        (IReadOnlyList<TagMapping> mappings, _) = GetSnapshot();
        int moved = 0;
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            if (!string.Equals(mapping.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals((mapping.Subscription ?? string.Empty).Trim(), target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TagMapping updated = CloneMapping(mapping);
            updated.Subscription = string.Empty;
            if (TryUpdate(updated, out _))
            {
                moved++;
            }
        }

        return moved;
    }

    private static TagMapping CloneMapping(TagMapping m) => new()
    {
        ProviderSourceId = m.ProviderSourceId,
        ProviderItemId = m.ProviderItemId,
        SourceId = m.SourceId,
        ItemId = m.ItemId,
        UaNodeId = m.UaNodeId,
        DisplayName = m.DisplayName,
        Description = m.Description,
        DataType = m.DataType,
        Enabled = m.Enabled,
        Mode = m.Mode,
        ManualValue = m.ManualValue,
        PollRateMs = m.PollRateMs,
        DaGroup = m.DaGroup,
        DeadbandPct = m.DeadbandPct,
        Writeable = m.Writeable,
        AccessRights = m.AccessRights,
        MqttEnabled = m.MqttEnabled,
        MqttTopic = m.MqttTopic,
        InfluxEnabled = m.InfluxEnabled,
        Subscription = m.Subscription
    };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~MappingSubscriptionTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpcBridge.App/MappingStore.cs tests/OpcBridge.LoadTest/MappingSubscriptionTests.cs
git commit -m "feat(app): mapping subscription normalization and reassign-on-delete"
```

---

### Task 6: Effective-rate resolution in `DashboardValues`

**Files:**
- Modify: `src/OpcBridge.App/DashboardValues.cs` (`BuildUpdateRateLookup` line 36)
- Modify: caller in `src/OpcBridge.App/Program.cs` line ~378 (pass subscription defs)
- Test: extend `tests/OpcBridge.LoadTest/DashboardValuesTests.cs`

**Interfaces:**
- Produces: overload
```csharp
public static Dictionary<string, int> BuildUpdateRateLookup(
    IReadOnlyList<TagMapping> mappings,
    IReadOnlyDictionary<string, int> sourceDefaultRates,
    IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionSettings>> uaSubscriptionsBySource)
```
Precedence: assigned named sub (clamped ≥ 100) > per-tag `PollRateMs` > source default (old signature delegates with an empty map).

- [ ] **Step 1: Write the failing test** (in `DashboardValuesTests.cs`, matching its existing style):

```csharp
[Fact]
public void BuildUpdateRateLookup_SubscriptionAssignment_WinsOverPollRateAndDefault()
{
    var subs = new Dictionary<string, IReadOnlyList<UaSubscriptionSettings>>(StringComparer.OrdinalIgnoreCase)
    {
        ["ua-a"] = new List<UaSubscriptionSettings> { new("Fast", 250), new("Slow", 5000) }
    };
    var defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["ua-a"] = 1000 };
    var mappings = new List<TagMapping>
    {
        new() { SourceId = "ua-a", ItemId = "t1", Subscription = "fast", PollRateMs = 777 }, // sub wins
        new() { SourceId = "ua-a", ItemId = "t2", Subscription = "Ghost", PollRateMs = 333 }, // unknown -> poll rate
        new() { SourceId = "ua-a", ItemId = "t3" }                                            // default
    };

    var lookup = DashboardValues.BuildUpdateRateLookup(mappings, defaults, subs);

    Assert.Equal(250, DashboardValues.LookupUpdateRate(lookup, "ua-a", "t1"));
    Assert.Equal(333, DashboardValues.LookupUpdateRate(lookup, "ua-a", "t2"));
    Assert.Equal(1000, DashboardValues.LookupUpdateRate(lookup, "ua-a", "t3"));
}
```

- [ ] **Step 2: Run to verify fail** — compile error (missing overload).
Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo --filter "FullyQualifiedName~DashboardValuesTests"`

- [ ] **Step 3: Implement** — old body becomes the delegatee; new overload inserts before the default lookup:

```csharp
    public static Dictionary<string, int> BuildUpdateRateLookup(
        IReadOnlyList<TagMapping> mappings,
        IReadOnlyDictionary<string, int> sourceDefaultRates)
        => BuildUpdateRateLookup(mappings, sourceDefaultRates, EmptySubscriptions);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionSettings>> EmptySubscriptions =
        new Dictionary<string, IReadOnlyList<UaSubscriptionSettings>>(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, int> BuildUpdateRateLookup(
        IReadOnlyList<TagMapping> mappings,
        IReadOnlyDictionary<string, int> sourceDefaultRates,
        IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionSettings>> uaSubscriptionsBySource)
    {
        Dictionary<string, int> lookup = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mappings.Count; i++)
        {
            TagMapping mapping = mappings[i];
            uaSubscriptionsBySource.TryGetValue(mapping.SourceId,
                out IReadOnlyList<UaSubscriptionSettings>? subs);
            int rate = ResolveEffectiveRate(mapping, sourceDefaultRates, subs);
            lookup[BridgeState.NormalizeKey(mapping.SourceId, mapping.ItemId)] = rate;
        }

        return lookup;
    }

    private static int ResolveEffectiveRate(
        TagMapping mapping,
        IReadOnlyDictionary<string, int> sourceDefaultRates,
        IReadOnlyList<UaSubscriptionSettings>? subscriptions)
    {
        string requested = (mapping.Subscription ?? string.Empty).Trim();
        if (requested.Length > 0 && subscriptions is not null)
        {
            for (int i = 0; i < subscriptions.Count; i++)
            {
                if (string.Equals(subscriptions[i].Name.Trim(), requested, StringComparison.OrdinalIgnoreCase))
                {
                    return Math.Max(100, subscriptions[i].UpdateRateMs);
                }
            }
        }

        return mapping.PollRateMs > 0
            ? mapping.PollRateMs
            : (sourceDefaultRates.TryGetValue(mapping.SourceId, out int sourceRate) ? sourceRate : 0);
    }
```
(`TryGetNonEnumeratedValue` exists on `IReadOnlyDictionary<TKey,TValue>` in .NET 8 — otherwise use `TryGetValue`.) Update the Program.cs caller (~line 377–379) to build `Dictionary<string, IReadOnlyList<UaSubscriptionSettings>>` from `snapshot.Sources.Where(s => s.UaSubscriptions.Count > 0).ToDictionary(s => s.SourceId, s => s.UaSubscriptions, StringComparer.OrdinalIgnoreCase)` and call the new overload.

- [ ] **Step 4: Run to verify pass**, then commit:

```bash
git add src/OpcBridge.App/DashboardValues.cs src/OpcBridge.App/Program.cs tests/OpcBridge.LoadTest/DashboardValuesTests.cs
git commit -m "feat(app): effective update rate resolves named-subscription assignment"
```

---

### Task 7: Live status accessor + reconcile trigger in `BridgeWorker`

**Files:**
- Modify: `src/OpcBridge.App/BridgeWorker.cs` (connection-equal branch lines ~905–924; new method near `GetDiagnostics`)

**Interfaces:**
- Consumes: `OpcUaSourceClient.GetSubscriptionsStatus()` (Task 3), `DaSourceRuntimeSettings.UaSubscriptionsEqual` (Task 4), `source_mapping_cache_` field (line 55).
- Produces: `public IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionStatus>> GetUaSubscriptionStatus()` keyed by sourceId (only UA sources with ≥1 bucket reported). Task 8's GET endpoint consumes this.

- [ ] **Step 1: Trigger reconcile when definitions change (connection otherwise equal)**

In `ReconfigureSessionsAsync`, inside the connection-equal branch (`if (... SourceConnectionEquals(existing.Source, source))`, lines 905–924), after the existing `SourceSettingsEquals` refresh block adds `changed.Add(...)` for rate/subscription-flag flips, append:

```csharp
                    // Named-subscription definition changes need a MonitoredItem reconcile,
                    // NOT a session rebuild: buckets are created/deleted/re-rated live (spec §6).
                    if (!existing.Source.UaSubscriptionsEqual(source)
                        && sessions[source.SourceId].Client is OpcUaSourceClient uaDefClient)
                    {
                        SourceMappingCache? defCache = source_mapping_cache_;
                        if (defCache is not null)
                        {
                            try
                            {
                                await uaDefClient.ReconcileMonitoredItemsAsync(
                                    defCache.GetSourceReadMappings(source.SourceId),
                                    cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                logger_.LogWarning(ex,
                                    "UA subscription-def reconcile failed for source {SourceId}", source.SourceId);
                            }
                        }
                    }
```

- [ ] **Step 2: Add the status accessor**

Near `GetDiagnostics()`:

```csharp
    /// <summary>Live named-subscription status per connected UA source (dashboard Subscriptions tab).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionStatus>> GetUaSubscriptionStatus()
    {
        Dictionary<string, IReadOnlyList<UaSubscriptionStatus>> result =
            new(StringComparer.OrdinalIgnoreCase);
        foreach ((string sourceId, SourceSession session) in active_sessions_)
        {
            if (session.Client is OpcUaSourceClient uaClient)
            {
                result[sourceId] = uaClient.GetSubscriptionsStatus();
            }
        }

        return result;
    }
```

(Verify the exact field/method names for the live session dictionary — `active_sessions_` is assigned at line ~370; reuse whatever `GetDiagnostics()` itself uses.)

- [ ] **Step 3: Build + full suite**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet build OpcBridge.sln --nologo -v q && dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo`
Expected: green, no new warnings.

- [ ] **Step 4: Commit**

```bash
git add src/OpcBridge.App/BridgeWorker.cs
git commit -m "feat(app): reconcile trigger and live status for UA subscription definitions"
```

---

### Task 8: HTTP endpoints + mapping DTO field

**Files:**
- Create: `src/OpcBridge.App/UaSubscriptionRequests.cs`
- Modify: `src/OpcBridge.App/Program.cs` (new endpoints near `/api/ua/test-connection`; `MappingTagDto` usage via `ToTagMapping` line 2492)
- Modify: `src/OpcBridge.App/MappingRequests.cs` (`MappingTagDto` gains `Subscription`)
- Test: `tests/OpcBridge.LoadTest/UaSubscriptionsApiTests.cs` (pattern: `BridgeAppApiTests` + `TestAppHandle.StartAsync`)

**Interfaces:**
- Consumes: everything from Tasks 4, 5, 7.
- Produces wire API: `GET /api/ua/subscriptions?sourceId=`, `POST /api/ua/subscriptions` `{sourceId,name,updateRateMs}`, `POST /api/ua/subscriptions/remove` `{sourceId,name}` (response includes `movedMappings`); mappings accept/return `subscription`.

- [ ] **Step 1: Requests DTO file**

```csharp
// src/OpcBridge.App/UaSubscriptionRequests.cs
namespace OpcBridge.App;

public sealed record UaSubscriptionUpsertRequest(string SourceId, string Name, int UpdateRateMs);

public sealed record UaSubscriptionRemoveRequest(string SourceId, string Name);
```

- [ ] **Step 2: DTO + ToTagMapping**

`MappingRequests.cs`: add `string? Subscription = null` as the last parameter of `MappingTagDto`. In `ToTagMapping` (Program.cs line 2492) add:
```csharp
    Subscription = tag.Subscription ?? string.Empty,
```
(`/api/mappings` serializes `TagMapping` directly, so responses pick the field up automatically.)

- [ ] **Step 3: Endpoints** (place near `/api/ua/test-connection`; mirror its parameter-binding style):

```csharp
app.MapGet("/api/ua/subscriptions", (DaRuntimeSettings settings, BridgeWorker worker, string? sourceId) =>
{
    DaRuntimeSettingsSnapshot snapshot = settings.GetSnapshot();
    IReadOnlyDictionary<string, IReadOnlyList<UaSubscriptionStatus>> live = worker.GetUaSubscriptionStatus();
    IEnumerable<DaSourceRuntimeSettings> sources = string.IsNullOrWhiteSpace(sourceId)
        ? snapshot.Sources
        : snapshot.Sources.Where(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

    object payload = new
    {
        sources = sources
            .Where(s => string.Equals(s.SourceType, SourceTypes.OpcUa, StringComparison.OrdinalIgnoreCase))
            .Select(s => new
            {
                sourceId = s.SourceId,
                displayName = s.DisplayName,
                defaultUpdateRateMs = s.UpdateRateMs,
                subscriptions = s.UaSubscriptions
                    .OrderBy(def => def.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(def =>
                    {
                        UaSubscriptionStatus? status = live.TryGetValue(s.SourceId, out IReadOnlyList<UaSubscriptionStatus>? list)
                            ? list.FirstOrDefault(st => string.Equals(st.BucketKey, def.Name, StringComparison.OrdinalIgnoreCase))
                            : null;
                        return new
                        {
                            name = def.Name,
                            updateRateMs = def.UpdateRateMs,
                            itemCount = status?.ItemCount ?? 0,
                            actualPublishingIntervalMs = status?.ActualPublishingIntervalMs ?? 0,
                            created = status?.Created ?? false
                        };
                    })
                    .ToList()
            })
            .ToList()
    };
    return Results.Json(payload);
});

app.MapPost("/api/ua/subscriptions", (UaSubscriptionUpsertRequest request, DaRuntimeSettings settings) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceId))
    {
        return Results.BadRequest(new { error = "sourceId is required." });
    }

    try
    {
        DaRuntimeSettingsSnapshot snapshot = settings.UpsertUaSubscription(request.SourceId, request.Name, request.UpdateRateMs);
        return Results.Ok(new { ok = true, version = snapshot.Version });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/ua/subscriptions/remove", (UaSubscriptionRemoveRequest request, DaRuntimeSettings settings, MappingStore store) =>
{
    try
    {
        DaRuntimeSettingsSnapshot snapshot = settings.RemoveUaSubscription(request.SourceId, request.Name);
        int movedMappings = store.ReassignSubscription(request.SourceId, request.Name);
        return Results.Ok(new { ok = true, version = snapshot.Version, movedMappings });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
```

- [ ] **Step 4: Integration tests** (copy the appsettings boilerplate verbatim from `BridgeAppApiTests`; seed a UA source + subscription through the API itself):

```csharp
public sealed class UaSubscriptionsApiTests
{
    [Fact]
    public async Task Upsert_List_Remove_FullCycle()
    {
        await using var handle = await TestAppHandle.StartAsync(dir => { /* same appsettings boilerplate */ });

        // Seed a UA source (POST /api/da/sources with sourceType "opc-ua"; mirror the JSON body
        // the dashboard sends — read Program.cs POST /api/da/sources handler for exact field names).
        // 1. POST /api/ua/subscriptions {"sourceId":"ua-t","name":"Fast","updateRateMs":250} -> ok:true
        // 2. GET /api/ua/subscriptions?sourceId=ua-t -> one entry name=="Fast", updateRateMs==250
        // 3. POST duplicate name different case ("fASt") -> still one subscription, rate updated
        // 4. POST bad rate (-5) -> 400 with error
        // 5. Add a mapping with subscription:"Fast" (POST /api/mappings/add), then
        //    POST /api/ua/subscriptions/remove -> ok:true AND movedMappings==1
        // 6. GET /api/mappings -> that mapping now subscription==""
        // 7. GET /api/mappings/add response round-trip: add mapping with subscription "X" -> GET lists subscription=="X" even though no such sub defined (tolerated storage, spec §4)
    }
}
```

Write these as six/seven separate `[Fact]`s following `BridgeAppApiTests` conventions exactly (each fact starts its own app instance; assertions via `handle.GetJsonAsync` + `RootElement` checks, `HttpStatusCode.BadRequest` asserts via raw send like existing negative-path tests do).

- [ ] **Step 5: Run suite**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo`
Expected: green including new API tests.

- [ ] **Step 6: Commit**

```bash
git add src/OpcBridge.App/UaSubscriptionRequests.cs src/OpcBridge.App/MappingRequests.cs src/OpcBridge.App/Program.cs tests/OpcBridge.LoadTest/UaSubscriptionsApiTests.cs
git commit -m "feat(app): /api/ua/subscriptions endpoints and mapping subscription field"
```

---

### Task 9: Dashboard UI — Subscriptions tab + Maps dropdown

**Files:**
- Modify: `src/OpcBridge.App/DashboardPage.cs` (nav buttons at lines 483–489; view sections; JS)

**Interfaces:**
- Consumes: Task 8 wire API.
- Produces: user-facing UI only.

- [ ] **Step 1: Nav + view shell**

Next to line 486 (`data-tab="opc-ua"` button) insert:
```html
<button class="tabbtn" data-tab="ua-subs" data-route="connectivity/ua-subs" onclick="navigate('connectivity/ua-subs')">UA Subs</button>
```
Copy the structure of an existing simple view section (e.g. how `view-connection` is declared with `id="view-..."` + hidden-by-default class) to add `<section id="view-ua-subs">` containing: a toolbar (source select `id="subSrcSel"`, name input `id="subNameInp"`, rate input `id="subRateInp"`, Add/Save button `onclick="saveUaSub()"`, Delete button `onclick="removeUaSub()"`), a table `<table id="subsTable">` (header: Source | Name | Rate | Tags | Actual | Status), and a status line `id="subsMsg"`. Follow `showTab`'s route handling convention (grep `showTab(name, route)` line 2871 and register `ua-subs` alongside its siblings so navigation activates the section).

- [ ] **Step 2: JS functions** (add near the other connectivity-tab fetch helpers; reuse global `fetchJSON`/error-toast helpers already present in the page):

```javascript
let uaSubsCache = [];
async function loadUaSubs() {
  const data = await fetchJSON('/api/ua/subscriptions');
  uaSubsCache = data.sources || [];
  renderUaSubs();
  const sel = document.getElementById('subSrcSel');
  sel.innerHTML = uaSubsCache.map(s =>
    `<option value="${esc(s.sourceId)}">${esc(s.displayName || s.sourceId)}</option>`).join('');
}
function renderUaSubs() {
  const rows = [];
  for (const s of uaSubsCache) {
    for (const sub of s.subscriptions) {
      rows.push(`<tr><td>${esc(s.sourceId)}</td><td>${esc(sub.name)}</td>
        <td>${formatMs(sub.updateRateMs)}</td><td>${sub.itemCount}</td>
        <td title="requested ${formatMs(sub.updateRateMs)}">${formatMs(Math.round(sub.actualPublishingIntervalMs))}</td>
        <td>${sub.created ? '<span class="badge ok">live</span>' : '<span class="badge warn">idle</span>'}</td></tr>`);
    }
    if (s.subscriptions.length === 0) {
      rows.push(`<tr class="dim"><td>${esc(s.sourceId)}</td><td colspan="5">(no named subscriptions — all tags on default)</td></tr>`);
    }
  }
  document.querySelector('#subsTable tbody').innerHTML = rows.join('');
}
async function saveUaSub() {
  const body = { sourceId: val('subSrcSel'), name: val('subNameInp'), updateRateMs: parseInt(val('subRateInp'), 10) };
  const res = await postJSON('/api/ua/subscriptions', body);
  showMsg('subsMsg', res.ok ? 'Saved.' : res.error, res.ok);
  await loadUaSubs();
}
async function removeUaSub() {
  const body = { sourceId: val('subSrcSel'), name: val('subNameInp') };
  const res = await postJSON('/api/ua/subscriptions/remove', body);
  showMsg('subsMsg', res.ok ? `Removed. ${res.movedMappings} tag(s) moved to default.` : res.error, res.ok);
  await loadUaSubs();
  if (typeof refreshMaps === 'function') refreshMaps(); // subscription pills/rates on Maps rows
}
```
Adjust helper names (`fetchJSON`, `postJSON`, `esc`, `val`, `formatMs`, badge classes, toast/msg pattern) to the page's actual existing utilities — grep for them first; every dashboard tab uses shared helpers, do not invent parallel ones. `formatMs` already exists (context.md notes it renders `—` for unknown).

- [ ] **Step 3: Maps dropdown + pill + rate column**

In the Maps add/edit form rendering for OPC UA rows (find where the UA map-type form builds inputs — grep `mapTypeSources(` / `setMapType` referenced at DashboardPage.cs line 26): add a select populated from `uaSubsCache` entry for the row's source: option `""` labeled `Source default (${formatMs(src.defaultUpdateRateMs)})`, then one per named sub `${name} (${formatMs(updateRateMs)})`; bind it to the mapping's `subscription` field through the same collect/submit path used for `pollRateMs`. When a non-empty subscription is chosen, disable the per-tag rate input (spec §7). In the row badge cluster, render a pill `sub.name` when `subscription` is truthy. The Rate column needs no change — it reads `updateRate` which Task 6 now resolves correctly.

- [ ] **Step 4: Extend `DashboardPageTests`** (string-containment style, matching its existing facts): nav button `data-tab="ua-subs"`, endpoint strings `/api/ua/subscriptions`, and the `saveUaSub`/`removeUaSub` function names appear in `DashboardPage.FullHtml`.

- [ ] **Step 5: Run suite + manual smoke**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo`
Expected: green. Then run the app locally (`dotnet run --project src/OpcBridge.App`) and manually verify: tab navigates, list renders, add/remove works against a scratch `sources.json`, browser force-reload with `?force=<timestamp>` (page caching gotcha).

- [ ] **Step 6: Commit**

```bash
git add src/OpcBridge.App/DashboardPage.cs tests/OpcBridge.LoadTest/DashboardPageTests.cs
git commit -m "feat(ui): UA Subscriptions tab and Maps subscription assignment"
```

---

### Task 10: Full verification + docs refresh

**Files:**
- Modify: `docs/context.md` (OPC UA inbound sources section)

- [ ] **Step 1: Full suite**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo`
Expected: all green (previous 457 + ~20 new). Record counts in the commit message body.

- [ ] **Step 2: Rig verification (manual, requires Docker rig)**

Using the documented sim containers: create `fast` @ 250 ms and `slow` @ 5000 ms on source `ua-a`, assign tags from each, then confirm (a) bridge logs show two buckets with distinct intervals, (b) notification timestamps for fast tags cluster ≤ 250 ms while slow tags cluster ≈ 5 s, (c) the tab shows requested vs actual intervals, (d) deleting `slow` moves its tags to default and values continue arriving at the default cadence, (e) restart the bridge — buckets survive (`sources.json`) and assignments survive (`mappings.json`).

- [ ] **Step 3: Update `docs/context.md`**

Under "OPC UA inbound sources": add a short paragraph — named subscriptions per UA source (`/api/ua/subscriptions*`, `sources.json` `OpcUa.options.subscriptions`), `TagMapping.Subscription` assignment (empty = default), named-bucket rate changes recreate only their subscription while source-rate changes recreate the session (still in `SourceConnectionEquals`), reconcile groups via `UaSubscriptionPlan.GroupByBucket`, delete auto-reassigns tags. Bump the header date note.

- [ ] **Step 4: Commit**

```bash
git add docs/context.md
git commit -m "docs(context): named UA subscriptions feature notes"
```

- [ ] **Step 5: Finish** — invoke superpowers:finishing-a-development-branch (merge decision is the human partner's).
