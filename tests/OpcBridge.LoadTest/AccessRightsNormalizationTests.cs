using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Regression tests for mapping access-rights normalization:
/// "writeable" alone must take effect (was defeated by a Read pre-fill),
/// and both "ReadWrite" and "Read-Write" spellings must normalize the same.
/// </summary>
[Collection(nameof(DaLinkApiAppCollection))]
public sealed class AccessRightsNormalizationTests
{
    private static MappingStore CreateStore()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "mappings.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return new MappingStore(Options.Create(new BridgeOptions()));
    }

    private static void AddTag(MappingStore store, string itemId, string? accessRights, bool writeable)
    {
        List<TagMapping> tags =
        [
            new TagMapping
            {
                SourceId = "default",
                ItemId = itemId,
                AccessRights = accessRights ?? string.Empty,
                Writeable = writeable
            }
        ];
        store.Add(tags);
    }

    private static TagMapping Get(MappingStore store, string itemId)
    {
        (IReadOnlyList<TagMapping> mappings, _) = store.GetSnapshot();
        return mappings.Single(m => m.ItemId == itemId);
    }

    [Fact]
    public void Add_WriteableAlone_BecomesReadWrite()
    {
        MappingStore store = CreateStore();
        AddTag(store, "T1", accessRights: null, writeable: true);

        TagMapping mapping = Get(store, "T1");
        Assert.Equal(TagAccessRights.ReadWrite, mapping.AccessRights);
        Assert.True(mapping.Writeable);
    }

    [Fact]
    public void Add_AccessRightsReadWriteNoHyphen_NormalizesToReadWrite()
    {
        MappingStore store = CreateStore();
        AddTag(store, "T2", accessRights: "ReadWrite", writeable: false);

        TagMapping mapping = Get(store, "T2");
        Assert.Equal(TagAccessRights.ReadWrite, mapping.AccessRights);
        Assert.True(mapping.Writeable);
    }

    [Fact]
    public void Add_AccessRightsReadWriteHyphen_StaysReadWrite()
    {
        MappingStore store = CreateStore();
        AddTag(store, "T3", accessRights: TagAccessRights.ReadWrite, writeable: false);

        TagMapping mapping = Get(store, "T3");
        Assert.Equal(TagAccessRights.ReadWrite, mapping.AccessRights);
        Assert.True(mapping.Writeable);
    }

    [Fact]
    public void Add_ExplicitRead_WithWriteable_StaysRead()
    {
        MappingStore store = CreateStore();
        AddTag(store, "T4", accessRights: TagAccessRights.Read, writeable: true);

        TagMapping mapping = Get(store, "T4");
        Assert.Equal(TagAccessRights.Read, mapping.AccessRights);
        Assert.False(mapping.Writeable);
    }

    [Fact]
    public void Add_AccessRightsWrite_NormalizesToWrite()
    {
        MappingStore store = CreateStore();
        AddTag(store, "T5", accessRights: "write", writeable: false);

        TagMapping mapping = Get(store, "T5");
        Assert.Equal(TagAccessRights.Write, mapping.AccessRights);
        Assert.True(mapping.Writeable);
    }

    [Fact]
    public void Add_NoRightsFields_DefaultsToRead()
    {
        MappingStore store = CreateStore();
        AddTag(store, "T6", accessRights: null, writeable: false);

        TagMapping mapping = Get(store, "T6");
        Assert.Equal(TagAccessRights.Read, mapping.AccessRights);
        Assert.False(mapping.Writeable);
    }
}
