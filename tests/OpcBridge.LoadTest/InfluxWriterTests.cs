using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Influx;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class InfluxWriterTests
{
    [Fact]
    public void InfluxOptions_Defaults_AreSafe()
    {
        InfluxOptions options = new();
        Assert.False(options.Enabled);
        Assert.Equal("http://localhost:8086", options.Url);
        Assert.Equal(string.Empty, options.Org);
        Assert.Equal(string.Empty, options.Bucket);
        Assert.Null(options.Token);
        Assert.Equal("opc_tags", options.Measurement);
        Assert.Equal(5000, options.TimeoutMs);
        Assert.True(options.VerifySsl);
    }

    [Fact]
    public void TagMapping_InfluxEnabled_JsonRoundTrip()
    {
        TagMapping tag = new()
        {
            SourceId = "default",
            DaItemId = "Random.Int1",
            InfluxEnabled = true
        };

        string json = JsonSerializer.Serialize(tag);
        TagMapping? roundTrip = JsonSerializer.Deserialize<TagMapping>(json);
        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.InfluxEnabled);
    }

    [Fact]
    public void MappingStore_Preserves_InfluxEnabled()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "mappings.json");
        if (File.Exists(path)) File.Delete(path);

        MappingStore store = new(Options.Create(new BridgeOptions()));
        store.Add(
        [
            new TagMapping
            {
                SourceId = "default",
                DaItemId = "tag.a",
                DisplayName = "A",
                InfluxEnabled = true
            }
        ]);

        (IReadOnlyList<TagMapping> snapshot, _) = store.GetSnapshot();
        TagMapping mapping = Assert.Single(snapshot.Where(m => m.DaItemId == "tag.a"));
        Assert.True(mapping.InfluxEnabled);
    }

    [Theory]
    [InlineData(true, "bool")]
    [InlineData((long)42, "long")]
    [InlineData(3.5, "double")]
    [InlineData("hi", "string")]
    public void InfluxPointBuilder_Types_ValueField(object raw, string kind)
    {
        InfluxOptions options = new() { Measurement = "opc_tags" };
        BridgeValue value = new("src", "item.1", raw, DateTime.UtcNow, 192, true);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, "Name");
        Assert.Equal("opc_tags", point.Measurement);
        Assert.Equal("src", point.Tags["source_id"]);
        Assert.Equal("item.1", point.Tags["da_item_id"]);
        Assert.Equal("Name", point.Tags["display_name"]);
        Assert.Equal(kind, point.ValueFieldKind);
        Assert.Equal(192, point.Quality);
        Assert.True(point.IsGood);
    }

    [Fact]
    public void InfluxPointBuilder_Omits_EmptyDisplayName()
    {
        InfluxOptions options = new();
        BridgeValue value = new("src", "item.1", 1L, DateTime.UtcNow, 0, false);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, "  ");
        Assert.False(point.Tags.ContainsKey("display_name"));
    }

    [Fact]
    public void InfluxRuntimeSettings_Persists_ToDisk()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "influx.json");
        if (File.Exists(path)) File.Delete(path);

        InfluxRuntimeSettings settings = new(Options.Create(new InfluxOptions()));
        settings.UpsertOptions(new InfluxOptions
        {
            Enabled = true,
            Url = "http://127.0.0.1:8086",
            Org = "factory",
            Bucket = "tags",
            Token = "secret",
            Measurement = "opc_tags"
        });

        Assert.True(File.Exists(path));
        InfluxRuntimeSettings reloaded = new(Options.Create(new InfluxOptions()));
        InfluxOptions opts = reloaded.GetOptions();
        Assert.True(opts.Enabled);
        Assert.Equal("factory", opts.Org);
        Assert.Equal("tags", opts.Bucket);
        Assert.Equal("secret", opts.Token);
    }
}
