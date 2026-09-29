using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Reading a tag list out of an MX OPC Configurator CSV and deciding what each row means
/// (issue #30): the file names a tag, and the bridge says whether that tag is new, already
/// mapped, mapped with a different description, repeated in the file — and, when the source
/// could be read, whether the source really exposes it.
/// </summary>
public sealed class TagImportFileTests
{
    private const string MxExport = """
        #LimitAlarmDefinitions;
        "LocationPath","Name","UpdateRate"

        #MX_DataTags;
        "LocationPath","Name","Description","Enable","AddressAsString","DataTypeAsString"
        "\Address Space\DRYEND_PLC\Input_X","X000","1D Canvas Stretch","Yes","X0","BOOL"
        "\Address Space\DRYEND_PLC\Input_X","X001","","Yes","X1","BOOL"
        "\Address Space\DRYEND_PLC\Output_Y","Y100","Coil, ""main"" drive","Yes","Y100","BOOL"

        #MX_DataTagsRoot;
        "LocationPath","Name"
        "\","Production Line"
        """;

    [Fact]
    public void Parse_ReadsTagNameDescriptionAndGroup()
    {
        Assert.True(TagImportFile.TryParseMxOpcTags(MxExport, out List<ImportedTag> tags, out string error), error);

        Assert.Equal(3, tags.Count);
        Assert.Equal("X000", tags[0].ItemId);
        Assert.Equal("1D Canvas Stretch", tags[0].Description);
        Assert.Equal(@"\Address Space\DRYEND_PLC\Input_X", tags[0].Group);
        // A blank cell means "no description", not an empty one — an import must never blank out
        // a description someone typed in the bridge.
        Assert.Null(tags[1].Description);
        // Delimiters, quotes and later sections: a quoted description keeps its comma and quotes,
        // and the tag table ends at the next "#Section;" line.
        Assert.Equal("Y100", tags[2].ItemId);
        Assert.Equal("Coil, \"main\" drive", tags[2].Description);
        Assert.DoesNotContain(tags, tag => tag.ItemId == "Production Line");
    }

    [Fact]
    public void Parse_WithoutTheTagTable_FailsWithAnActionableReason()
    {
        const string otherFile = """
            Id,Name,Value
            1,Not a tag table,5
            """;

        Assert.False(TagImportFile.TryParseMxOpcTags(otherFile, out List<ImportedTag> tags, out string error));
        Assert.Empty(tags);
        Assert.Contains(TagImportFile.MxOpcTagsSection, error);
    }

    [Fact]
    public void Parse_EmptyTagTable_ReadsZeroTagsRatherThanFailing()
    {
        const string empty = "#MX_DataTags;\r\n\"LocationPath\",\"Name\",\"Description\"\r\n";

        Assert.True(TagImportFile.TryParseMxOpcTags(empty, out List<ImportedTag> tags, out string error), error);
        Assert.Empty(tags);
    }

    [Fact]
    public void Parse_RowsWithoutATagName_AreLeftOut()
    {
        const string withBlank = """
            #MX_DataTags;
            "LocationPath","Name","Description"
            "\Plc\In","","No name here"
            "\Plc\In","X10","Kept"
            """;

        Assert.True(TagImportFile.TryParseMxOpcTags(withBlank, out List<ImportedTag> tags, out string error), error);
        Assert.Equal("X10", Assert.Single(tags).ItemId);
    }

    [Fact]
    public void Parse_EmptyOrMissingText_IsRejected()
    {
        Assert.False(TagImportFile.TryParseMxOpcTags(null, out _, out string nullError));
        Assert.False(string.IsNullOrWhiteSpace(nullError));
        Assert.False(TagImportFile.TryParseMxOpcTags("   ", out _, out string blankError));
        Assert.False(string.IsNullOrWhiteSpace(blankError));
    }

    [Fact]
    public void Compare_SeparatesNewMappedDifferingAndRepeatedRows()
    {
        DateTime stamp = new(2026, 9, 29, 3, 15, 0, DateTimeKind.Utc);
        Dictionary<string, TagMapping> mapped = new(StringComparer.OrdinalIgnoreCase)
        {
            ["X000"] = new TagMapping { SourceId = "default", ItemId = "X000", Description = "Kept description", AddedUtc = stamp },
            ["Y100"] = new TagMapping { SourceId = "default", ItemId = "Y100", Description = null, AddedUtc = stamp }
        };
        List<ImportedTag> imported = new()
        {
            new ImportedTag("X000", "Kept description", @"\Plc\In"),
            new ImportedTag("Y100", "Now described", @"\Plc\Out"),
            new ImportedTag("X010", "New tag", @"\Plc\In"),
            new ImportedTag("x010", "Repeated tag", @"\Plc\In")
        };
        HashSet<string> source = new(StringComparer.OrdinalIgnoreCase) { "X000", "X010" };

        List<TagImportRow> rows = TagImportComparer.Compare(imported, mapped, source);

        Assert.Equal(TagImportStatus.Mapped, rows[0].Status);
        Assert.Equal(stamp, rows[0].AddedUtc);
        Assert.True(rows[0].OnSource);
        // A stored mapping with no description and a file that has one differs — that is the
        // import's chance to fill it in.
        Assert.Equal(TagImportStatus.DescriptionDiffers, rows[1].Status);
        Assert.Null(rows[1].ExistingDescription);
        // Y100 is not on the source: the row says so instead of looking like an ordinary add.
        Assert.False(rows[1].OnSource);
        Assert.Equal(TagImportStatus.New, rows[2].Status);
        // The file's second X010 is reported as a repeat; item ids match case-insensitively, as
        // the mapping store keys them.
        Assert.Equal(TagImportStatus.DuplicateInFile, rows[3].Status);
    }

    [Fact]
    public void Compare_WithoutASourceRead_LeavesEveryRowUnknown()
    {
        List<ImportedTag> imported = new() { new ImportedTag("X000", null, @"\Plc\In") };

        List<TagImportRow> rows = TagImportComparer.Compare(
            imported,
            new Dictionary<string, TagMapping>(StringComparer.OrdinalIgnoreCase),
            sourceTags: null);

        Assert.Null(Assert.Single(rows).OnSource);
    }
}
