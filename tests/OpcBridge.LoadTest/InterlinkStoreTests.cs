using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

[Collection(nameof(InterlinkApiAppCollection))]
public sealed class InterlinkStoreTests
{
    // InterlinkStore persists to a fixed file under AppContext.BaseDirectory. Clear it before each
    // test so a prior run cannot leak rules into the snapshot under test.
    private static InterlinkStore CreateStore()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "links.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return new InterlinkStore(Options.Create(new BridgeOptions()));
    }

    private static string GetPersistPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "links.json");
    }

    private static InterlinkRule CreateRule(
        Guid? id = null,
        string providerSourceId = "providerA",
        string providerItemId = "itemP",
        string consumerSourceId = "consumerA",
        string consumerItemId = "itemA",
        bool enabled = true,
        short? providerCanonicalType = 5,
        short? consumerCanonicalType = 5)
    {
        return new InterlinkRule(
            id ?? Guid.NewGuid(),
            providerSourceId,
            providerItemId,
            consumerSourceId,
            consumerItemId,
            enabled,
            providerCanonicalType,
            consumerCanonicalType);
    }

    [Fact]
    public void TryAdd_RejectsCanonicalTypeMismatchWithExplicitError()
    {
        InterlinkStore store = CreateStore();

        bool ok = store.TryAdd(
            CreateRule(providerCanonicalType: 5, consumerCanonicalType: 3),
            out _,
            out string? error);

        Assert.False(ok);
        Assert.Equal("Provider and consumer must use the same data type.", error);
    }

    [Fact]
    public void TryUpdate_RejectsCanonicalTypeMismatchWithExplicitError()
    {
        InterlinkStore store = CreateStore();
        Guid id = Guid.NewGuid();

        Assert.True(store.TryAdd(
            CreateRule(id: id, providerCanonicalType: 5, consumerCanonicalType: 5),
            out _,
            out _));

        bool ok = store.TryUpdate(
            CreateRule(id: id, providerCanonicalType: 5, consumerCanonicalType: 3),
            out _,
            out string? error);

        Assert.False(ok);
        Assert.Equal("Provider and consumer must use the same data type.", error);
    }

    [Fact]
    public void SetAll_RejectsCanonicalTypeMismatchWithExplicitError()
    {
        InterlinkStore store = CreateStore();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            store.SetAll(new[]
            {
                CreateRule(providerCanonicalType: 5, consumerCanonicalType: 3)
            }));

        Assert.Equal("Provider and consumer must use the same data type.", ex.Message);
    }

    [Fact]
    public void MigrateFromMappings_ThrowsWhenLegacyLinkIsRejected()
    {
        var mappings = new[]
        {
            new TagMapping
            {
                SourceId = "consumerA",
                ItemId = "itemA",
                ProviderSourceId = "providerA",
                ProviderItemId = "itemP1"
            },
            new TagMapping
            {
                SourceId = "consumerA",
                ItemId = "itemA",
                ProviderSourceId = "providerB",
                ProviderItemId = "itemP2"
            }
        };

        InterlinkStore store = CreateStore();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            store.MigrateFromMappings(mappings));

        Assert.Equal("Consumer already has a provider.", ex.Message);
    }

    [Fact]
    public void MigrateFromMappings_FailureLeavesStoreAndPersistedFileUnchanged()
    {
        InterlinkStore store = CreateStore();
        InterlinkRule existing = CreateRule(
            providerSourceId: "providerSeed",
            providerItemId: "itemSeedP",
            consumerSourceId: "consumerSeed",
            consumerItemId: "itemSeedC");

        Assert.True(store.TryAdd(existing, out _, out _));
        string before = File.ReadAllText(GetPersistPath());

        TagMapping[] mappings =
        {
            new()
            {
                SourceId = "consumerA",
                ItemId = "itemA",
                ProviderSourceId = "providerA",
                ProviderItemId = "itemP1"
            },
            new()
            {
                SourceId = "consumerA",
                ItemId = "itemA",
                ProviderSourceId = "providerB",
                ProviderItemId = "itemP2"
            }
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            store.MigrateFromMappings(mappings));

        Assert.Equal("Consumer already has a provider.", ex.Message);

        (IReadOnlyList<InterlinkRule> rules, long version) = store.GetSnapshot();
        InterlinkRule remaining = Assert.Single(rules);
        Assert.Equal(existing, remaining);
        Assert.Equal(1, version);
        Assert.Equal(before, File.ReadAllText(GetPersistPath()));
    }

    [Fact]
    public void SetAll_RejectsDuplicateRuleIds()
    {
        InterlinkStore store = CreateStore();
        Guid id = Guid.NewGuid();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            store.SetAll(new[]
            {
                CreateRule(id: id, providerSourceId: "providerA", providerItemId: "itemP1", consumerSourceId: "consumerA", consumerItemId: "itemA"),
                CreateRule(id: id, providerSourceId: "providerB", providerItemId: "itemP2", consumerSourceId: "consumerB", consumerItemId: "itemB")
            }));

        Assert.Equal("Rule already exists.", ex.Message);
    }

    [Fact]
    public void MigrateFromMappings_CreatesOneRulePerLegacyProviderLink()
    {
        var mappings = new[]
        {
            new TagMapping
            {
                SourceId = "consumerA",
                ItemId = "itemA",
                ProviderSourceId = "providerA",
                ProviderItemId = "itemP"
            }
        };

        InterlinkStore store = CreateStore();

        int migrated = store.MigrateFromMappings(mappings);
        (IReadOnlyList<InterlinkRule> rules, _) = store.GetSnapshot();

        Assert.Equal(1, migrated);
        InterlinkRule rule = Assert.Single(rules);
        Assert.Equal("providerA", rule.ProviderSourceId);
        Assert.Equal("itemP", rule.ProviderItemId);
        Assert.Equal("consumerA", rule.ConsumerSourceId);
        Assert.Equal("itemA", rule.ConsumerItemId);
    }

    [Fact]
    public void TryAdd_RejectsSecondProviderForSameConsumer()
    {
        InterlinkStore store = CreateStore();

        Assert.True(store.TryAdd(
            CreateRule(providerSourceId: "providerA", providerItemId: "itemP1", consumerSourceId: "consumerA", consumerItemId: "itemA"),
            out _,
            out _));

        bool ok = store.TryAdd(
            CreateRule(providerSourceId: "providerB", providerItemId: "itemP2", consumerSourceId: "consumerA", consumerItemId: "itemA"),
            out _,
            out string? error);

        Assert.False(ok);
        Assert.Equal("Consumer already has a provider.", error);
    }
}