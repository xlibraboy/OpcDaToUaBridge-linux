using System.Text.Json;
using OpcBridge.App.Hmi;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DisplayStoreTests : IDisposable
{
    private readonly string root_;
    private readonly DisplayStore store_;

    public DisplayStoreTests()
    {
        root_ = Path.Combine(Path.GetTempPath(), "OpcBridge.DisplayStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root_);
        store_ = new DisplayStore(root_);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root_))
            {
                Directory.Delete(root_, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static DisplayDocumentDto Sample(string id = "plant-overview", int version = 0) => new()
    {
        SchemaVersion = 1,
        Id = id,
        Name = "Plant Overview",
        Version = version,
        Width = 1920,
        Height = 1080,
        Widgets =
        [
            new DisplayWidgetDto
            {
                Id = "w1",
                Type = "numeric",
                X = 10,
                Y = 20,
                W = 100,
                H = 40,
                Binding = new TagBindingDto
                {
                    BridgeId = "line1",
                    SourceId = "default",
                    DaItemId = "Tank.Level"
                }
            }
        ]
    };

    [Fact]
    public void Put_Create_SetsVersion1_AndPersists()
    {
        DisplayPutResult result = store_.Put(Sample(version: 0));
        Assert.Equal(DisplayPutStatus.Ok, result.Status);
        Assert.NotNull(result.Document);
        Assert.Equal(1, result.Document!.Version);
        Assert.True(File.Exists(Path.Combine(root_, "displays", "plant-overview.json")));

        Assert.True(store_.TryGet("plant-overview", out DisplayDocumentDto? loaded));
        Assert.Equal(1, loaded!.Version);
        Assert.Equal("Plant Overview", loaded.Name);
        Assert.Single(loaded.Widgets);
    }

    [Fact]
    public void Put_Update_BumpsVersion()
    {
        Assert.Equal(DisplayPutStatus.Ok, store_.Put(Sample(version: 0)).Status);
        DisplayDocumentDto update = Sample(version: 1);
        update.Name = "Updated";
        DisplayPutResult result = store_.Put(update);
        Assert.Equal(DisplayPutStatus.Ok, result.Status);
        Assert.Equal(2, result.Document!.Version);
        Assert.Equal("Updated", result.Document.Name);
    }

    [Fact]
    public void Put_VersionMismatch_ReturnsConflict()
    {
        Assert.Equal(DisplayPutStatus.Ok, store_.Put(Sample(version: 0)).Status);
        DisplayPutResult result = store_.Put(Sample(version: 0));
        Assert.Equal(DisplayPutStatus.Conflict, result.Status);
        Assert.Equal(1, result.CurrentVersion);
    }

    [Fact]
    public void Put_InvalidId_ReturnsInvalid()
    {
        DisplayPutResult result = store_.Put(Sample(id: "../evil"));
        Assert.Equal(DisplayPutStatus.Invalid, result.Status);
    }

    [Fact]
    public void Put_BadSchemaVersion_ReturnsInvalid()
    {
        DisplayDocumentDto doc = Sample();
        doc.SchemaVersion = 99;
        DisplayPutResult result = store_.Put(doc);
        Assert.Equal(DisplayPutStatus.Invalid, result.Status);
    }

    [Fact]
    public void Put_DuplicateWidgetIds_ReturnsInvalid()
    {
        DisplayDocumentDto doc = Sample();
        doc.Widgets.Add(new DisplayWidgetDto
        {
            Id = "w1",
            Type = "label",
            W = 10,
            H = 10
        });
        DisplayPutResult result = store_.Put(doc);
        Assert.Equal(DisplayPutStatus.Invalid, result.Status);
    }

    [Fact]
    public void List_ReturnsSummaries()
    {
        store_.Put(Sample());
        IReadOnlyList<DisplayListItemDto> items = store_.List();
        Assert.Single(items);
        Assert.Equal("plant-overview", items[0].Id);
        Assert.Equal(1, items[0].WidgetCount);
        Assert.Equal(1, items[0].Version);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        store_.Put(Sample());
        Assert.True(store_.Delete("plant-overview"));
        Assert.False(store_.TryGet("plant-overview", out _));
        Assert.Empty(store_.List());
        Assert.False(store_.Delete("plant-overview"));
    }

    [Fact]
    public void TryGet_Unknown_ReturnsFalse()
    {
        Assert.False(store_.TryGet("missing", out _));
    }
}
